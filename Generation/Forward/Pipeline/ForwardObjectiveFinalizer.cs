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

        var envelope = new ForwardTicketEnvelopeValidator().Validate(withEffectiveTiers, framePlan, featureIntents);
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

        var finalEnvelope = new ForwardTicketEnvelopeValidator().Validate(balanced, framePlan, featureIntents);
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
        var usableBaseSlots = Math.Min(
            availableCollectionSlots,
            NormalProgressCapacity(framePlan)
                + NoSafeFillerCapacityAllowance(objectives));
        if (usableBaseSlots < winRequired)
        {
            return BalanceResult.Fail(Fail(
                ForwardObjectiveFinalizationStatus.WinCollectionsExceedCapacity,
                $"winning targets need {winRequired}, normal progress slots={usableBaseSlots}"));
        }

        var rawNearMissCapacity = Math.Max(0, usableBaseSlots - winRequired);
        var nearMissBudget = Math.Max(0, rawNearMissCapacity - TemporalReserve(framePlan));
        if (objectives.NearMissTargets.Count > 0
            && nearMissBudget < Settings.NONWIN_MIN_TARGET
            && rawNearMissCapacity >= Settings.NONWIN_MIN_TARGET)
        {
            nearMissBudget = Settings.NONWIN_MIN_TARGET;
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
        return Settings.COLS + (framePlan.TotalTurns * 2) + Settings.NONWIN_MIN_TARGET + boardFeatureCount;
    }

    private int NormalProgressCapacity(ForwardTurnFramePlan framePlan)
    {
        var analyzer = new ForwardCellFateAnalyzer();
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

    private int NoSafeFillerCapacityAllowance(ForwardObjectives objectives) =>
        HasGuaranteedSafeFiller(objectives) || objectives.NearMissTargets.Count > 0
            ? 0
            : Settings.COLS;

    private static bool HasGuaranteedSafeFiller(ForwardObjectives objectives) =>
        objectives.FillSymbols.Any(symbol =>
            !objectives.WinTargets.ContainsKey(symbol)
            && !objectives.NearMissTargets.ContainsKey(symbol));

    private IReadOnlyList<(int r, int c)> EmptyPositionsAfterPushRotate(ForwardTurnShape shape)
    {
        var board = new Cell?[Settings.ROWS, Settings.COLS];
        for (var row = 0; row < Settings.ROWS; row++)
        {
            for (var col = 0; col < Settings.COLS; col++)
                board[row, col] = Grid.Norm(1);
        }

        return new ForwardBoardState(board)
            .PreviewAfterPushRotate(shape)
            .EmptyPositions;
    }

    private IEnumerable<(int r, int c)> AllPositions()
    {
        for (var row = 0; row < Settings.ROWS; row++)
        {
            for (var col = 0; col < Settings.COLS; col++)
                yield return (row, col);
        }
    }

    private int MinimumTarget(int requestedTarget) =>
        Math.Max(1, Settings.NONWIN_MIN_TARGET);

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
            source.WinCompletionTurn);

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
