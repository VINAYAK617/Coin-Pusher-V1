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
            .Select(frame => new ForwardFutureTurn(frame.Turn, frame.Shape))
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
    private readonly ICustomProfileSettings _settings;
    private readonly Random _rng;

    internal ForwardTurnFramePlanner(ICustomProfileSettings settings, int seed)
    {
        _settings = settings;
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
        for (var turn = 1; turn <= budget.TotalTurns; turn++)
        {
            var intents = intentPlan.ByTurn.TryGetValue(turn, out var turnIntents)
                ? turnIntents
                : Array.Empty<ForwardFeatureIntent>();
            var flushCount = intents.Count(intent => intent.Kind == ForwardTimedFeatureKind.Flush);
            if (flushCount > _settings.COLS)
            {
                return Fail(
                    ForwardTurnFrameStatus.TooManyFlushColumns,
                    $"turn {turn} has {flushCount} FLUSH intent(s), board has {_settings.COLS} column(s)");
            }

            var flushColumns = PickFlushColumns(flushCount);
            var shape = BuildShape(turn, budget, objectives, flushColumns, usedPushBags);
            if (!shape.IsValid)
            {
                return Fail(
                    ForwardTurnFrameStatus.ShapePlanningFailed,
                    $"turn {turn}: {shape.Detail}");
            }

            var pushBagKey = ForwardTurnShapePlanner.PushBagKey(shape.Shape!, _settings);
            usedPushBags[pushBagKey] = usedPushBags.GetValueOrDefault(pushBagKey) + 1;
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

        return Enumerable.Range(0, _settings.COLS)
            .OrderBy(_ => _rng.Next())
            .Take(count)
            .ToHashSet();
    }

    private ForwardTurnShapePlanResult BuildShape(
        int turn,
        ForwardFeatureBudget budget,
        ForwardObjectives? objectives,
        IReadOnlySet<int> flushColumns,
        IReadOnlyDictionary<long, int> usedPushBags)
    {
        var totalTurns = budget.TotalTurns;
        var normalColumns = _settings.COLS - flushColumns.Count;
        var minPopped = (flushColumns.Count * _settings.ROWS)
            + (normalColumns * _settings.MIN_PUSH);
        var maxPopped = (flushColumns.Count * _settings.ROWS)
            + (normalColumns * _settings.MAX_PUSH);
        var pressureMode = objectives != null
            && IsPressureTurnPlan(budget, objectives);
        var preferredPoppedCells = pressureMode && objectives != null
            ? PressurePreferredPoppedCells(budget, objectives, minPopped, maxPopped)
            : (int?)null;

        return new ForwardTurnShapePlanner(_settings, _rng).Plan(
            minPopped,
            maxPopped,
            flushColumns,
            blockedColumns: null,
            pressureMode,
            avoidedPushBags: null,
            pushBagUseCounts: usedPushBags,
            preferredPoppedCells);
    }

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
        if (budget.TotalTurns >= _settings.MAX_SPINS)
            return true;

        var required = objectives.WinTargets.Values.Sum()
            + objectives.NearMissTargets.Values.Sum();
        if (budget.TotalTurns <= 0)
            return false;

        var averageDemand = required / (double)budget.TotalTurns;
        return averageDemand >= _settings.MixedPushCapacity(_settings.COLS);
    }

    private static ForwardTurnFrameResult Ok() =>
        new(ForwardTurnFrameStatus.Valid, "ok", null);

    private static ForwardTurnFrameResult Fail(
        ForwardTurnFrameStatus status,
        string detail) =>
        new(status, detail, null);
}
