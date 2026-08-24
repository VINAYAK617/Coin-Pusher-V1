namespace CoinPusherEngine;

internal enum ForwardObjectiveFinalizationStatus
{
    Valid,
    MissingObjectives,
    MissingFeatureIntents,
    MissingFramePlan,
    EnvelopeInvalid,
    WinCollectionsExceedCapacity,
    ExplicitNearMissExceedsCapacity,
    ProtectedNearMissExceedsCapacity,
    BalanceFailed,
}

internal sealed class ForwardObjectiveFinalizationResult
{
    internal ForwardObjectiveFinalizationResult(
        ForwardObjectiveFinalizationStatus status,
        string detail,
        ForwardObjectives? objectives)
    {
        Status = status;
        Detail = detail;
        Objectives = objectives;
    }

    internal ForwardObjectiveFinalizationStatus Status { get; }
    internal string Detail { get; }
    internal ForwardObjectives? Objectives { get; }
    internal bool IsValid => Status == ForwardObjectiveFinalizationStatus.Valid;
}

internal sealed class ForwardObjectiveFinalizer
{
    private readonly ICustomProfileSettings _settings;

    internal ForwardObjectiveFinalizer(ICustomProfileSettings settings)
    {
        _settings = settings;
    }

    internal ForwardObjectiveFinalizationResult Finalize(
        ForwardObjectives? objectives,
        ForwardFeatureIntentPlan? featureIntents,
        ForwardTurnFramePlan? framePlan)
    {
        if (objectives == null)
            return Fail(ForwardObjectiveFinalizationStatus.MissingObjectives, "forward objectives are missing");
        if (featureIntents == null)
            return Fail(ForwardObjectiveFinalizationStatus.MissingFeatureIntents, "feature intent plan is missing");
        if (framePlan == null)
            return Fail(ForwardObjectiveFinalizationStatus.MissingFramePlan, "turn frame plan is missing");

        var withEffectiveTiers = Copy(
            objectives,
            objectives.NearMissTargets,
            featureIntents.EffectivePrizeTiers,
            featureIntents.EffectiveNonWinPrizeTiers);

        var envelope = new ForwardTicketEnvelopeValidator(_settings).Validate(withEffectiveTiers, framePlan);
        if (!envelope.IsValid
            && envelope.Status != ForwardTicketEnvelopeStatus.InsufficientCollectionCapacity)
        {
            return Fail(
                ForwardObjectiveFinalizationStatus.EnvelopeInvalid,
                $"{envelope.Status}: {envelope.Detail}");
        }

        var balance = BalanceNearMissTargets(
            withEffectiveTiers,
            featureIntents,
            framePlan,
            envelope.AvailableCollectionSlots);
        if (!balance.IsValid)
            return balance.Result!;

        if (SameTargets(withEffectiveTiers.NearMissTargets, balance.Targets) && envelope.IsValid)
            return Ok("ok", withEffectiveTiers);

        var balanced = Copy(
            withEffectiveTiers,
            balance.Targets,
            withEffectiveTiers.PrizeTiers,
            withEffectiveTiers.NonWinPrizeTiers
                .Where(kv => balance.Targets.ContainsKey(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value));

        var finalEnvelope = new ForwardTicketEnvelopeValidator(_settings).Validate(balanced, framePlan);
        if (!finalEnvelope.IsValid)
        {
            return Fail(
                ForwardObjectiveFinalizationStatus.BalanceFailed,
                $"{finalEnvelope.Status}: {finalEnvelope.Detail}");
        }

        return Ok(
            $"near-miss target total adjusted from {withEffectiveTiers.NearMissTargets.Values.Sum()} to {balanced.NearMissTargets.Values.Sum()}",
            balanced);
    }

    private static bool SameTargets(
        IReadOnlyDictionary<int, int> left,
        IReadOnlyDictionary<int, int> right) =>
        left.Count == right.Count
        && left.All(kv => right.TryGetValue(kv.Key, out var value) && value == kv.Value);

    private BalanceResult BalanceNearMissTargets(
        ForwardObjectives objectives,
        ForwardFeatureIntentPlan featureIntents,
        ForwardTurnFramePlan framePlan,
        int availableCollectionSlots)
    {
        var winRequired = objectives.WinTargets.Values.Sum();
        var wheelWinBonus = WheelWinBonus(objectives, framePlan);
        var usableBaseSlots = Math.Min(
            availableCollectionSlots,
            NormalProgressCapacity(framePlan, objectives) + NoSafeFillerCapacityAllowance(objectives));
        var normalWinNeed = Math.Max(0, winRequired - wheelWinBonus);
        if (usableBaseSlots < normalWinNeed)
        {
            return BalanceResult.Fail(Fail(
                ForwardObjectiveFinalizationStatus.WinCollectionsExceedCapacity,
                $"winning targets need {winRequired}, WHEEL bonus={wheelWinBonus}, normal progress slots={usableBaseSlots}"));
        }

        var rawNearMissCapacity = Math.Max(0, usableBaseSlots - normalWinNeed);
        var temporalReserve = TemporalReserve(framePlan);
        var protectedSymbols = featureIntents.EffectiveNonWinPrizeTiers.Keys
            .Concat(featureIntents.Intents
                .Where(intent => intent.Kind == ForwardTimedFeatureKind.PrizeUpgrade && intent.UpgradeSymbol.HasValue)
                .Select(intent => intent.UpgradeSymbol!.Value))
            .ToHashSet();
        if (objectives.NearMissTargetsAreExplicit)
        {
            var requested = objectives.NearMissTargets.Values.Sum();
            if (requested > rawNearMissCapacity)
            {
                return BalanceResult.Fail(Fail(
                    ForwardObjectiveFinalizationStatus.ExplicitNearMissExceedsCapacity,
                    $"explicit near-miss targets need {requested}, raw capacity={rawNearMissCapacity}"));
            }

            return BalanceResult.Ok(objectives.NearMissTargets.ToDictionary(kv => kv.Key, kv => kv.Value));
        }

        var protectedTargetCount = objectives.NearMissTargets.Keys.Count(protectedSymbols.Contains);
        if (protectedTargetCount > rawNearMissCapacity)
        {
            return BalanceResult.Fail(Fail(
                ForwardObjectiveFinalizationStatus.ProtectedNearMissExceedsCapacity,
                $"{protectedTargetCount} protected near-miss symbols need one collection each, " +
                $"raw capacity={rawNearMissCapacity}, temporal reserve={temporalReserve}"));
        }

        var discretionaryCapacity = Math.Max(
            0,
            rawNearMissCapacity - protectedTargetCount - temporalReserve);
        var nearMissBudget = protectedTargetCount + discretionaryCapacity;

        if (objectives.NearMissTargets.Values.Sum() <= nearMissBudget)
            return BalanceResult.Ok(objectives.NearMissTargets.ToDictionary(kv => kv.Key, kv => kv.Value));

        return AllocateAutomaticTargets(
            objectives.NearMissTargets,
            protectedSymbols,
            nearMissBudget,
            rawNearMissCapacity,
            temporalReserve);
    }

    private int TemporalReserve(ForwardTurnFramePlan framePlan)
    {
        var boardFeatureCount = framePlan.Frames
            .Sum(frame => frame.FeatureIntents.Count(intent => intent.IsBoardFeature));
        return _settings.COLS + (framePlan.TotalTurns * 2) + _settings.NONWIN_MIN_TARGET + boardFeatureCount;
    }

    private int NormalProgressCapacity(
        ForwardTurnFramePlan framePlan,
        ForwardObjectives objectives)
    {
        var analyzer = new ForwardCellFateAnalyzer(_settings);
        var total = AllPositions()
            .Count(position => analyzer.Analyze(position.r, position.c, framePlan.FutureTurnsAfter(0)).IsCollected);

        foreach (var frame in framePlan.Frames.OrderBy(frame => frame.Turn))
        {
            var futureTurns = framePlan.FutureTurnsAfter(frame.Turn);
            var collectingSpawnSlots = EmptyPositionsAfterPushRotate(frame.Shape)
                .Count(position => analyzer.Analyze(position.r, position.c, futureTurns).IsCollected);
            total += collectingSpawnSlots;
        }

        return total;
    }

    private int WheelWinBonus(
        ForwardObjectives objectives,
        ForwardTurnFramePlan framePlan)
    {
        var bonus = 0;
        foreach (var intent in framePlan.Frames
                     .SelectMany(frame => frame.FeatureIntents)
                     .Where(intent => intent.Kind == ForwardTimedFeatureKind.Wheel)
                     .Where(intent => intent.WheelSymbol.HasValue && intent.ResultingWheelStack.HasValue))
        {
            var symbol = intent.WheelSymbol!.Value;
            if (!objectives.WinTargets.TryGetValue(symbol, out var target))
                continue;

            var stack = Math.Min(_settings.MAX_COIN_STACK, intent.ResultingWheelStack!.Value);
            if (stack <= 1)
                continue;

            var zone = Math.Max(0, Math.Min(target / stack, _settings.COLS - 1) - 1);
            bonus += zone * (stack - 1);
        }

        return bonus;
    }

    private int NoSafeFillerCapacityAllowance(ForwardObjectives objectives) =>
        HasGuaranteedSafeFiller(objectives) || objectives.NearMissTargets.Count > 0
            ? 0
            : _settings.COLS;

    private static bool HasGuaranteedSafeFiller(ForwardObjectives objectives) =>
        objectives.FillSymbols.Any(symbol =>
            !objectives.WinTargets.ContainsKey(symbol)
            && !objectives.NearMissTargets.ContainsKey(symbol));

    private IReadOnlyList<(int r, int c)> EmptyPositionsAfterPushRotate(ForwardTurnShape shape)
    {
        var board = new Cell?[_settings.ROWS, _settings.COLS];
        for (var row = 0; row < _settings.ROWS; row++)
        {
            for (var col = 0; col < _settings.COLS; col++)
                board[row, col] = Grid.Norm(1);
        }

        return new ForwardBoardState(board, _settings)
            .PreviewAfterPushRotate(shape)
            .EmptyPositions;
    }

    private IEnumerable<(int r, int c)> AllPositions()
    {
        for (var row = 0; row < _settings.ROWS; row++)
        {
            for (var col = 0; col < _settings.COLS; col++)
                yield return (row, col);
        }
    }

    private static BalanceResult AllocateAutomaticTargets(
        IReadOnlyDictionary<int, int> requestedTargets,
        IReadOnlySet<int> protectedSymbols,
        int budget,
        int rawCapacity,
        int temporalReserve)
    {
        var protectedEntries = requestedTargets
            .Where(kv => protectedSymbols.Contains(kv.Key))
            .OrderBy(kv => kv.Key)
            .ToArray();
        if (protectedEntries.Length > budget)
        {
            return BalanceResult.Fail(Fail(
                ForwardObjectiveFinalizationStatus.ProtectedNearMissExceedsCapacity,
                $"{protectedEntries.Length} protected near-miss symbols need one collection each, " +
                $"available capacity={budget}, raw capacity={rawCapacity}, temporal reserve={temporalReserve}"));
        }

        var selected = new List<KeyValuePair<int, int>>(protectedEntries);
        var remaining = budget - protectedEntries.Length;
        selected.AddRange(requestedTargets
            .Where(kv => !protectedSymbols.Contains(kv.Key))
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Take(remaining));

        var targets = selected.ToDictionary(kv => kv.Key, _ => 1);
        remaining = budget - targets.Count;
        if (remaining <= 0)
            return BalanceResult.Ok(targets);

        var additionalDemand = selected.Sum(kv => Math.Max(0, kv.Value - 1));
        if (additionalDemand <= 0)
            return BalanceResult.Ok(targets);

        var shares = selected
            .Select(kv =>
            {
                var demand = Math.Max(0, kv.Value - 1);
                var exact = remaining * (double)demand / additionalDemand;
                return new
                {
                    kv.Key,
                    Demand = demand,
                    Whole = Math.Min(demand, (int)Math.Floor(exact)),
                    Fraction = exact - Math.Floor(exact),
                };
            })
            .ToArray();

        foreach (var share in shares)
            targets[share.Key] += share.Whole;

        var left = remaining - shares.Sum(share => share.Whole);
        foreach (var share in shares
                     .Where(share => targets[share.Key] < requestedTargets[share.Key])
                     .OrderByDescending(share => share.Fraction)
                     .ThenBy(share => share.Key)
                     .Take(left))
        {
            targets[share.Key]++;
        }

        return BalanceResult.Ok(targets);
    }

    private static ForwardObjectives Copy(
        ForwardObjectives source,
        IReadOnlyDictionary<int, int> nearMissTargets,
        IReadOnlyDictionary<int, int> prizeTiers,
        IReadOnlyDictionary<int, int> nonWinPrizeTiers) =>
        new(
            source.WinTargets.ToDictionary(kv => kv.Key, kv => kv.Value),
            source.WinSymbols.ToArray(),
            source.FillSymbols.ToArray(),
            nearMissTargets.ToDictionary(kv => kv.Key, kv => kv.Value),
            source.SymbolCaps.ToDictionary(kv => kv.Key, kv => kv.Value),
            prizeTiers.ToDictionary(kv => kv.Key, kv => kv.Value),
            source.PrizeValues.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyDictionary<int, decimal>)kv.Value.ToDictionary(tier => tier.Key, tier => tier.Value)),
            nonWinPrizeTiers.ToDictionary(kv => kv.Key, kv => kv.Value),
            source.MaxSymbol,
            source.IsNoWin,
            source.TopPrizeSymbol,
            source.NearMissTargetsAreExplicit,
            source.WinningRoundPlan);

    private static ForwardObjectiveFinalizationResult Ok(
        string detail,
        ForwardObjectives objectives) =>
        new(ForwardObjectiveFinalizationStatus.Valid, detail, objectives);

    private static ForwardObjectiveFinalizationResult Fail(
        ForwardObjectiveFinalizationStatus status,
        string detail) =>
        new(status, detail, null);

    private sealed class BalanceResult
    {
        private BalanceResult(
            Dictionary<int, int> targets,
            ForwardObjectiveFinalizationResult? result)
        {
            Targets = targets;
            Result = result;
        }

        internal Dictionary<int, int> Targets { get; }
        internal ForwardObjectiveFinalizationResult? Result { get; }
        internal bool IsValid => Result == null;

        internal static BalanceResult Ok(Dictionary<int, int> targets) =>
            new(targets, null);

        internal static BalanceResult Fail(ForwardObjectiveFinalizationResult result) =>
            new(new Dictionary<int, int>(), result);
    }
}
