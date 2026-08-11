namespace CoinPusherEngine;

internal enum ForwardObjectiveFinalizationStatus
{
    Valid,
    MissingObjectives,
    MissingFeatureIntents,
    MissingFramePlan,
    EnvelopeInvalid,
    WinCollectionsExceedCapacity,
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
    private readonly Settings _settings;

    internal ForwardObjectiveFinalizer(Settings settings)
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
            NormalProgressCapacity(framePlan) + NoSafeFillerCapacityAllowance(objectives));
        var normalWinNeed = Math.Max(0, winRequired - wheelWinBonus);
        if (usableBaseSlots < normalWinNeed)
        {
            return BalanceResult.Fail(Fail(
                ForwardObjectiveFinalizationStatus.WinCollectionsExceedCapacity,
                $"winning targets need {winRequired}, WHEEL bonus={wheelWinBonus}, normal progress slots={usableBaseSlots}"));
        }

        var rawNearMissCapacity = Math.Max(0, usableBaseSlots - normalWinNeed);
        var nearMissBudget = Math.Max(0, rawNearMissCapacity - TemporalReserve(framePlan));
        if (objectives.NearMissTargets.Count > 0
            && nearMissBudget < _settings.NONWIN_MIN_TARGET
            && rawNearMissCapacity >= _settings.NONWIN_MIN_TARGET)
        {
            nearMissBudget = _settings.NONWIN_MIN_TARGET;
        }

        var protectedSymbols = featureIntents.EffectiveNonWinPrizeTiers.Keys
            .Concat(featureIntents.Intents
                .Where(intent => intent.Kind == ForwardTimedFeatureKind.PrizeUpgrade && intent.UpgradeSymbol.HasValue)
                .Select(intent => intent.UpgradeSymbol!.Value))
            .ToHashSet();

        var constrained = objectives.NearMissTargets.Values.Sum() > nearMissBudget;
        var targets = new Dictionary<int, int>();
        var remaining = nearMissBudget;

        foreach (var entry in objectives.NearMissTargets
                     .Where(kv => protectedSymbols.Contains(kv.Key))
                     .OrderBy(kv => kv.Key))
        {
            var minTarget = MinimumTarget(entry.Value);
            if (remaining < minTarget)
            {
                return BalanceResult.Fail(Fail(
                    ForwardObjectiveFinalizationStatus.ProtectedNearMissExceedsCapacity,
                    $"protected near-miss symbol {entry.Key} needs at least {minTarget}, remaining capacity={remaining}"));
            }

            var assigned = constrained
                ? minTarget
                : Math.Min(entry.Value, remaining);
            targets[entry.Key] = assigned;
            remaining -= assigned;
        }

        foreach (var entry in objectives.NearMissTargets
                     .Where(kv => !protectedSymbols.Contains(kv.Key))
                     .OrderBy(kv => kv.Key))
        {
            var minTarget = MinimumTarget(entry.Value);
            if (remaining < minTarget)
                continue;

            var assigned = constrained
                ? minTarget
                : Math.Min(entry.Value, remaining);
            targets[entry.Key] = assigned;
            remaining -= assigned;
        }

        return BalanceResult.Ok(targets);
    }

    private int TemporalReserve(ForwardTurnFramePlan framePlan)
    {
        var boardFeatureCount = framePlan.Frames
            .Sum(frame => frame.FeatureIntents.Count(intent => intent.IsBoardFeature));
        return _settings.COLS + (framePlan.TotalTurns * 2) + _settings.NONWIN_MIN_TARGET + boardFeatureCount;
    }

    private int NormalProgressCapacity(ForwardTurnFramePlan framePlan)
    {
        var analyzer = new ForwardCellFateAnalyzer(_settings);
        var total = AllPositions()
            .Count(position => analyzer.Analyze(position.r, position.c, framePlan.FutureTurnsAfter(0)).IsCollected);

        foreach (var frame in framePlan.Frames.OrderBy(frame => frame.Turn))
        {
            var futureTurns = framePlan.FutureTurnsAfter(frame.Turn);
            var collectingSpawnSlots = EmptyPositionsAfterPushRotate(frame.Shape)
                .Count(position => analyzer.Analyze(position.r, position.c, futureTurns).IsCollected);
            var boardFeatureCount = frame.FeatureIntents.Count(intent => intent.IsBoardFeature);
            total += Math.Max(0, collectingSpawnSlots - boardFeatureCount);
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

    private int MinimumTarget(int requestedTarget) =>
        Math.Max(1, _settings.NONWIN_MIN_TARGET);

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
            source.TopPrizeSymbol);

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
