namespace CoinPusherEngine;

internal enum ForwardTurnFrameStatus
{
    Valid,
    MissingBudget,
    MissingIntentPlan,
    TotalTurnMismatch,
    FeatureCountMismatch,
    FeatureOnFinalTurn,
    TooManyFlushColumns,
    ShapePlanningFailed,
}

internal sealed class ForwardTurnFrame
{
    internal ForwardTurnFrame(
        int turn,
        ForwardTurnShape shape,
        IReadOnlyList<ForwardFeatureIntent> featureIntents,
        IReadOnlySet<int> flushColumns)
    {
        Turn = turn;
        Shape = shape;
        FeatureIntents = featureIntents;
        FlushColumns = flushColumns;
    }

    internal int Turn { get; }
    internal ForwardTurnShape Shape { get; }
    internal IReadOnlyList<ForwardFeatureIntent> FeatureIntents { get; }
    internal IReadOnlySet<int> FlushColumns { get; }
}

internal sealed class ForwardTurnFramePlan
{
    internal ForwardTurnFramePlan(
        int totalTurns,
        IReadOnlyList<ForwardTurnFrame> frames)
    {
        TotalTurns = totalTurns;
        Frames = frames;
        ByTurn = frames.ToDictionary(frame => frame.Turn);
    }

    internal int TotalTurns { get; }
    internal IReadOnlyList<ForwardTurnFrame> Frames { get; }
    internal IReadOnlyDictionary<int, ForwardTurnFrame> ByTurn { get; }

    internal IReadOnlyList<ForwardFeatureIntent> FeatureIntentsForTurn(int turn) =>
        ByTurn.TryGetValue(turn, out var frame)
            ? frame.FeatureIntents
            : Array.Empty<ForwardFeatureIntent>();

    internal IReadOnlyList<ForwardFutureTurn> FutureTurnsAfter(int turn) =>
        Frames
            .Where(frame => frame.Turn > turn)
            .OrderBy(frame => frame.Turn)
            .Select(frame => new ForwardFutureTurn(frame.Turn, frame.Shape, frame.FeatureIntents))
            .ToArray();
}

internal sealed class ForwardTurnFrameResult
{
    internal ForwardTurnFrameResult(
        ForwardTurnFrameStatus status,
        string detail,
        ForwardTurnFramePlan? plan)
    {
        Status = status;
        Detail = detail;
        Plan = plan;
    }

    internal ForwardTurnFrameStatus Status { get; }
    internal string Detail { get; }
    internal ForwardTurnFramePlan? Plan { get; }
    internal bool IsValid => Status == ForwardTurnFrameStatus.Valid;
}

internal sealed class ForwardTurnFramePlanner
{
    private readonly Random _rng;

    internal ForwardTurnFramePlanner(int seed)
    {
        _rng = new Random(seed);
    }

    internal ForwardTurnFrameResult Plan(
        ForwardFeatureBudget? budget,
        ForwardFeatureIntentPlan? intentPlan,
        ForwardObjectives? objectives = null)
    {
        if (budget == null)
            return Fail(ForwardTurnFrameStatus.MissingBudget, "feature budget is missing");
        if (intentPlan == null)
            return Fail(ForwardTurnFrameStatus.MissingIntentPlan, "feature intent plan is missing");
        if (intentPlan.TotalTurns != budget.TotalTurns)
        {
            return Fail(
                ForwardTurnFrameStatus.TotalTurnMismatch,
                $"intent total turns={intentPlan.TotalTurns}, budget total turns={budget.TotalTurns}");
        }

        var countCheck = ValidateFeatureCounts(budget, intentPlan);
        if (!countCheck.IsValid) return countCheck;

        var finalFeature = intentPlan.Intents.FirstOrDefault(intent => intent.Turn >= budget.TotalTurns);
        if (finalFeature != null)
        {
            return Fail(
                ForwardTurnFrameStatus.FeatureOnFinalTurn,
                $"{finalFeature.Kind} scheduled on final turn {budget.TotalTurns}");
        }

        var frames = new List<ForwardTurnFrame>(budget.TotalTurns);
        var usedPushBags = new Dictionary<long, int>();
        var remainingPoppedBudget = TargetPoppedBudget(budget, intentPlan, objectives);
        var frontLoadPoppedBudget = false;
        var reserveFinalMaxPopped = objectives != null
            && RequiresBoundedWheelBonus(objectives);
        for (var turn = 1; turn <= budget.TotalTurns; turn++)
        {
            var intents = intentPlan.ByTurn.TryGetValue(turn, out var turnIntents)
                ? turnIntents
                : Array.Empty<ForwardFeatureIntent>();
            var flushCount = intents.Count(intent => intent.Kind == ForwardTimedFeatureKind.Flush);
            if (flushCount > Settings.COLS)
            {
                return Fail(
                    ForwardTurnFrameStatus.TooManyFlushColumns,
                    $"turn {turn} has {flushCount} FLUSH intent(s), board has {Settings.COLS} column(s)");
            }

            var flushColumns = PickFlushColumns(flushCount);
            var preferredPoppedCells = PreferredPoppedForBudget(
                remainingPoppedBudget,
                turn,
                budget.TotalTurns,
                intentPlan,
                frontLoadPoppedBudget,
                reserveFinalMaxPopped);
            var shape = BuildShape(
                turn,
                budget,
                objectives,
                flushColumns,
                usedPushBags,
                preferredPoppedCells);
            if (!shape.IsValid)
            {
                return Fail(
                    ForwardTurnFrameStatus.ShapePlanningFailed,
                    $"turn {turn}: {shape.Detail}");
            }

            var pushBagKey = ForwardTurnShapePlanner.PushBagKey(shape.Shape!);
            usedPushBags[pushBagKey] = usedPushBags.GetValueOrDefault(pushBagKey) + 1;
            if (remainingPoppedBudget.HasValue)
                remainingPoppedBudget = Math.Max(0, remainingPoppedBudget.Value - shape.PoppedCellCount);
            frames.Add(new ForwardTurnFrame(
                turn,
                shape.Shape!,
                intents.ToArray(),
                flushColumns));
        }

        return new ForwardTurnFrameResult(
            ForwardTurnFrameStatus.Valid,
            "ok",
            new ForwardTurnFramePlan(budget.TotalTurns, frames));
    }

    private ForwardTurnFrameResult ValidateFeatureCounts(
        ForwardFeatureBudget budget,
        ForwardFeatureIntentPlan intentPlan)
    {
        var wheel = intentPlan.Intents.Count(intent => intent.Kind == ForwardTimedFeatureKind.Wheel);
        var flush = intentPlan.Intents.Count(intent => intent.Kind == ForwardTimedFeatureKind.Flush);
        var extraGo = intentPlan.Intents.Count(intent => intent.Kind == ForwardTimedFeatureKind.ExtraGo);
        var prizeUpgrade = intentPlan.Intents.Count(intent => intent.Kind == ForwardTimedFeatureKind.PrizeUpgrade);

        if (wheel != budget.WheelCount
            || flush != budget.FlushCount
            || extraGo != budget.ExtraGoCount
            || prizeUpgrade != budget.PrizeUpgradeCount)
        {
            return Fail(
                ForwardTurnFrameStatus.FeatureCountMismatch,
                $"intent counts WHEEL={wheel}/{budget.WheelCount}, FLUSH={flush}/{budget.FlushCount}, " +
                $"EXTRA_GO={extraGo}/{budget.ExtraGoCount}, PRIZE_UPGRADE={prizeUpgrade}/{budget.PrizeUpgradeCount}");
        }

        return Ok();
    }

    private IReadOnlySet<int> PickFlushColumns(int count)
    {
        if (count <= 0) return new HashSet<int>();

        return Enumerable.Range(0, Settings.COLS)
            .OrderBy(_ => _rng.Next())
            .Take(count)
            .ToHashSet();
    }

    private ForwardTurnShapePlanResult BuildShape(
        int turn,
        ForwardFeatureBudget budget,
        ForwardObjectives? objectives,
        IReadOnlySet<int> flushColumns,
        IReadOnlyDictionary<long, int> usedPushBags,
        int? budgetPreferredPoppedCells)
    {
        var totalTurns = budget.TotalTurns;
        var normalColumns = Settings.COLS - flushColumns.Count;
        var minPopped = (flushColumns.Count * Settings.ROWS)
            + (normalColumns * Settings.MIN_PUSH);
        var maxPopped = (flushColumns.Count * Settings.ROWS)
            + (normalColumns * Settings.MAX_PUSH);
        var pressureMode = objectives != null
            && IsPressureTurnPlan(budget, objectives);
        var preferredPoppedCells = budgetPreferredPoppedCells
            ?? (pressureMode && objectives != null
            ? PressurePreferredPoppedCells(budget, objectives, minPopped, maxPopped)
            : (int?)null);

        return new ForwardTurnShapePlanner(_rng).Plan(
            minPopped,
            maxPopped,
            flushColumns,
            blockedColumns: null,
            pressureMode,
            avoidedPushBags: null,
            pushBagUseCounts: usedPushBags,
            preferredPoppedCells);
    }

    private int? TargetPoppedBudget(
        ForwardFeatureBudget budget,
        ForwardFeatureIntentPlan intentPlan,
        ForwardObjectives? objectives)
    {
        if (objectives == null || !IsPressureTurnPlan(budget, objectives))
            return null;

        var required = objectives.WinTargets.Values.Sum()
            + objectives.NearMissTargets.Values.Sum();
        var reserve = HasGuaranteedSafeFiller(objectives)
            ? Settings.COLS
            : objectives.NearMissTargets.Count > 0
            ? Math.Min(Settings.COLS, NearMissSpareCapacity(objectives))
            : 0;
        var target = required + reserve;
        var reserveFinalMaxPopped = RequiresBoundedWheelBonus(objectives);
        var minTotal = Enumerable.Range(1, budget.TotalTurns)
            .Sum(turn => MinPoppedForTurn(intentPlan, turn, budget.TotalTurns, reserveFinalMaxPopped));
        var maxTotal = Enumerable.Range(1, budget.TotalTurns)
            .Sum(turn => MaxPoppedForTurn(intentPlan, turn));

        return Math.Clamp(target, minTotal, maxTotal);
    }

    private int? PreferredPoppedForBudget(
        int? remainingBudget,
        int turn,
        int totalTurns,
        ForwardFeatureIntentPlan intentPlan,
        bool frontLoad,
        bool reserveFinalMaxPopped)
    {
        if (!remainingBudget.HasValue)
            return null;

        var remainingTurns = totalTurns - turn + 1;
        if (remainingTurns <= 0)
            return null;

        var minNow = MinPoppedForTurn(intentPlan, turn, totalTurns, reserveFinalMaxPopped);
        var maxNow = MaxPoppedForTurn(intentPlan, turn);
        var futureMin = Enumerable.Range(turn + 1, totalTurns - turn)
            .Sum(futureTurn => MinPoppedForTurn(intentPlan, futureTurn, totalTurns, reserveFinalMaxPopped));
        var futureMax = Enumerable.Range(turn + 1, totalTurns - turn)
            .Sum(futureTurn => MaxPoppedForTurn(intentPlan, futureTurn));

        var lower = Math.Max(minNow, remainingBudget.Value - futureMax);
        var upper = Math.Min(maxNow, remainingBudget.Value - futureMin);
        if (lower > upper)
            return Math.Clamp((int)Math.Ceiling(remainingBudget.Value / (double)remainingTurns), minNow, maxNow);

        if (frontLoad)
            return upper;

        var average = Math.Clamp(
            (int)Math.Ceiling(remainingBudget.Value / (double)remainingTurns),
            lower,
            upper);
        return PickBudgetedPoppedCount(lower, upper, average);
    }

    private int MinPoppedForTurn(ForwardFeatureIntentPlan intentPlan, int turn)
    {
        var totalTurns = Math.Max(turn, intentPlan.TotalTurns);
        return MinPoppedForTurn(intentPlan, turn, totalTurns, reserveFinalMaxPopped: false);
    }

    private int MinPoppedForTurn(
        ForwardFeatureIntentPlan intentPlan,
        int turn,
        int totalTurns,
        bool reserveFinalMaxPopped)
    {
        if (reserveFinalMaxPopped && turn == totalTurns)
            return MaxPoppedForTurn(intentPlan, turn);
        if (reserveFinalMaxPopped && turn == totalTurns - 1)
            return Math.Min(
                MaxPoppedForTurn(intentPlan, turn),
                Math.Max(
                    MinPoppedForTurn(intentPlan, turn, totalTurns, reserveFinalMaxPopped: false),
                    Settings.MixedPushCapacity(Settings.COLS) - 2));

        var flushCount = intentPlan.ByTurn.TryGetValue(turn, out var intents)
            ? intents.Count(intent => intent.Kind == ForwardTimedFeatureKind.Flush)
            : 0;
        return (flushCount * Settings.ROWS)
            + ((Settings.COLS - flushCount) * Settings.MIN_PUSH);
    }

    private int MaxPoppedForTurn(ForwardFeatureIntentPlan intentPlan, int turn)
    {
        var flushCount = intentPlan.ByTurn.TryGetValue(turn, out var intents)
            ? intents.Count(intent => intent.Kind == ForwardTimedFeatureKind.Flush)
            : 0;
        var rawMax = (flushCount * Settings.ROWS)
            + ((Settings.COLS - flushCount) * Settings.MAX_PUSH);
        return rawMax;
    }

    private int PickBudgetedPoppedCount(int lower, int upper, int average)
    {
        _ = average;
        if (lower >= upper)
            return lower;

        var counts = Enumerable.Range(lower, upper - lower + 1).ToArray();
        var bucketSize = Math.Max(1, (int)Math.Ceiling(counts.Length / 3.0));
        var low = counts.Take(bucketSize).ToArray();
        var mid = counts.Skip(bucketSize).Take(bucketSize).ToArray();
        var high = counts.Skip(bucketSize * 2).ToArray();
        var bucket = PickPopBucket(low, mid, high);

        return bucket.Length == 1
            ? bucket[0]
            : bucket[_rng.Next(bucket.Length)];
    }

    private int[] PickPopBucket(int[] low, int[] mid, int[] high)
    {
        var lowWeight = low.Length == 0 ? 0.0 : Settings.WPusherLowPop;
        var midWeight = mid.Length == 0 ? 0.0 : Settings.WPusherMidPop;
        var highWeight = high.Length == 0 ? 0.0 : Settings.WPusherHighPop;
        var total = lowWeight + midWeight + highWeight;
        if (total <= 0.0)
            return low.Length > 0 ? low : mid.Length > 0 ? mid : high;

        var roll = _rng.NextDouble() * total;
        if (roll < lowWeight) return low;
        roll -= lowWeight;
        if (roll < midWeight) return mid;
        return high;
    }

    private static bool HasGuaranteedSafeFiller(ForwardObjectives objectives) =>
        objectives.FillSymbols.Any(symbol =>
            !objectives.WinTargets.ContainsKey(symbol)
            && !objectives.NearMissTargets.ContainsKey(symbol));

    private static bool RequiresBoundedWheelBonus(ForwardObjectives objectives) =>
        !HasGuaranteedSafeFiller(objectives);

    private int NearMissSpareCapacity(ForwardObjectives objectives) =>
        objectives.NearMissTargets
            .Sum(kv => Math.Max(0, Settings.SymbolFillCap(kv.Key) - 1 - kv.Value));

    private int PressurePreferredPoppedCells(
        ForwardFeatureBudget budget,
        ForwardObjectives objectives,
        int minPopped,
        int maxPopped)
    {
        var required = objectives.WinTargets.Values.Sum()
            + objectives.NearMissTargets.Values.Sum();
        var averageDemand = budget.TotalTurns <= 0
            ? maxPopped
            : (int)Math.Ceiling(required / (double)budget.TotalTurns);

        return Math.Clamp(averageDemand, minPopped, maxPopped);
    }

    private bool IsPressureTurnPlan(
        ForwardFeatureBudget budget,
        ForwardObjectives objectives)
    {
        if (budget.TotalTurns >= Settings.MAX_SPINS)
            return true;

        var required = objectives.WinTargets.Values.Sum()
            + objectives.NearMissTargets.Values.Sum();
        if (required >= HighCollectionPressureThreshold())
            return true;

        if (budget.TotalTurns <= 0)
            return false;

        var averageDemand = required / (double)budget.TotalTurns;
        return averageDemand >= Settings.MixedPushCapacity(Settings.COLS);
    }

    private int HighCollectionPressureThreshold() =>
        Settings.ROWS * Settings.COLS
        + (Settings.MixedPushCapacity(Settings.COLS) * (Settings.BASE_SPINS - 1));

    private static ForwardTurnFrameResult Ok() =>
        new(ForwardTurnFrameStatus.Valid, "ok", null);

    private static ForwardTurnFrameResult Fail(
        ForwardTurnFrameStatus status,
        string detail) =>
        new(status, detail, null);
}
