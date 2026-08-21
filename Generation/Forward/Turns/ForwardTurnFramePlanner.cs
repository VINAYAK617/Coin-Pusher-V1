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
        var capacityDirected = objectives != null
            && RequiresCapacityDirectedShapes(budget, intentPlan, objectives);
        var capacityTargets = capacityDirected
            ? BuildCapacityTargets(budget, intentPlan, objectives!)
            : new Dictionary<int, int>();
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
            var shape = BuildShape(
                turn,
                budget,
                objectives,
                flushColumns,
                usedPushBags,
                capacityTargets.TryGetValue(turn, out var capacityTarget)
                    ? capacityTarget
                    : (int?)null);
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
        IReadOnlyDictionary<long, int> usedPushBags,
        int? preferredPoppedCells)
    {
        var totalTurns = budget.TotalTurns;
        var normalColumns = _settings.COLS - flushColumns.Count;
        var minPopped = (flushColumns.Count * _settings.ROWS)
            + (normalColumns * _settings.MIN_PUSH);
        var maxPopped = (flushColumns.Count * _settings.ROWS)
            + (normalColumns * _settings.MAX_PUSH);
        var pressureMode = objectives != null
            && IsPressureTurnPlan(budget, objectives);

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

    private Dictionary<int, int> BuildCapacityTargets(
        ForwardFeatureBudget budget,
        ForwardFeatureIntentPlan intentPlan,
        ForwardObjectives objectives)
    {
        var bounds = Enumerable.Range(1, budget.TotalTurns)
            .ToDictionary(turn => turn, turn => TurnPopBounds(turn, intentPlan));
        var required = RawCollectionTarget(objectives, intentPlan);
        var safeExtraCapacity = SafeExtraCollectionCapacity(objectives);
        var unpressuredFloor = bounds.Values.Sum(bound =>
            ForwardTurnShapePlanner.MinimumUnpressuredPopCount(bound.Min, bound.Max));
        var deadlineFloor = WinningDeadlineCollectionFloor(bounds, intentPlan, objectives);
        var preferredTotal = Math.Max(required, Math.Max(unpressuredFloor, deadlineFloor));
        preferredTotal = Math.Min(preferredTotal, required + safeExtraCapacity);
        var remaining = Math.Clamp(
            preferredTotal,
            bounds.Values.Sum(bound => bound.Min),
            bounds.Values.Sum(bound => bound.Max));
        var targets = new Dictionary<int, int>();
        var useCounts = new Dictionary<int, int>();

        // Solve the exact total while limiting repeated pop totals. Shape selection
        // then randomizes among the legal mixed pusher bags for each chosen total.
        if (TryBuildCapacityTargets(
                turn: 1,
                remaining,
                budget.TotalTurns,
                bounds,
                useCounts,
                targets))
        {
            ShiftCapacityIntoWinningWindow(targets, bounds, intentPlan, objectives);
            return targets;
        }

        targets.Clear();
        useCounts.Clear();

        for (var turn = 1; turn <= budget.TotalTurns; turn++)
        {
            var current = bounds[turn];
            var future = bounds
                .Where(entry => entry.Key > turn)
                .Select(entry => entry.Value)
                .ToArray();
            var futureMin = future.Sum(bound => bound.Min);
            var futureMax = future.Sum(bound => bound.Max);
            var low = Math.Max(current.Min, remaining - futureMax);
            var high = Math.Min(current.Max, remaining - futureMin);
            var turnsLeft = budget.TotalTurns - turn + 1;
            var average = remaining / (double)turnsLeft;
            var candidates = Enumerable.Range(low, high - low + 1).ToArray();
            var minimumUse = candidates.Min(candidate => useCounts.GetValueOrDefault(candidate));
            var leastUsed = candidates
                .Where(candidate => useCounts.GetValueOrDefault(candidate) == minimumUse)
                .ToArray();
            var minimumDistance = leastUsed.Min(candidate => Math.Abs(candidate - average));
            var closest = leastUsed
                .Where(candidate => Math.Abs(candidate - average) == minimumDistance)
                .OrderBy(_ => _rng.Next())
                .ToArray();
            var selected = closest[0];

            targets[turn] = selected;
            useCounts[selected] = useCounts.GetValueOrDefault(selected) + 1;
            remaining -= selected;
        }

        ShiftCapacityIntoWinningWindow(targets, bounds, intentPlan, objectives);
        return targets;
    }

    private void ShiftCapacityIntoWinningWindow(
        Dictionary<int, int> targets,
        IReadOnlyDictionary<int, (int Min, int Max)> bounds,
        ForwardFeatureIntentPlan intentPlan,
        ForwardObjectives objectives)
    {
        var completionTurn = objectives.WinningRoundPlan?.WinningCompletionTurn;
        if (!completionTurn.HasValue || objectives.WinTargets.Count == 0)
            return;

        var requiredWheelBonus = intentPlan.Intents
            .Where(intent => intent.Kind == ForwardTimedFeatureKind.Wheel)
            .Where(intent => intent.IsCapacityRequiredWheel)
            .Where(intent => intent.WheelSymbol.HasValue
                && objectives.WinTargets.ContainsKey(intent.WheelSymbol.Value))
            .Sum(intent => Math.Max(0, intent.PlannedWheelCollectionBonus));
        var requiredByDeadline = Math.Max(0, objectives.WinTargets.Values.Sum() - requiredWheelBonus);
        var capacityByDeadline = targets
            .Where(entry => entry.Key <= completionTurn.Value)
            .Sum(entry => entry.Value);
        var deficit = requiredByDeadline - capacityByDeadline;

        while (deficit > 0)
        {
            var receiver = targets
                .Where(entry => entry.Key <= completionTurn.Value)
                .Where(entry => entry.Value < bounds[entry.Key].Max)
                .OrderBy(entry => entry.Value)
                .ThenBy(_ => _rng.Next())
                .Select(entry => entry.Key)
                .FirstOrDefault();
            var donor = targets
                .Where(entry => entry.Key > completionTurn.Value)
                .Where(entry => entry.Value > bounds[entry.Key].Min)
                .OrderByDescending(entry => entry.Value)
                .ThenBy(_ => _rng.Next())
                .Select(entry => entry.Key)
                .FirstOrDefault();
            if (receiver == 0 || donor == 0)
                return;

            targets[receiver]++;
            targets[donor]--;
            deficit--;
        }
    }

    private bool TryBuildCapacityTargets(
        int turn,
        int remaining,
        int totalTurns,
        IReadOnlyDictionary<int, (int Min, int Max)> bounds,
        Dictionary<int, int> useCounts,
        Dictionary<int, int> targets)
    {
        if (turn > totalTurns)
            return remaining == 0;

        var current = bounds[turn];
        var future = bounds
            .Where(entry => entry.Key > turn)
            .Select(entry => entry.Value)
            .ToArray();
        var low = Math.Max(current.Min, remaining - future.Sum(bound => bound.Max));
        var high = Math.Min(current.Max, remaining - future.Sum(bound => bound.Min));
        if (low > high) return false;

        var average = remaining / (double)(totalTurns - turn + 1);
        var candidates = Enumerable.Range(low, high - low + 1)
            .Where(candidate => useCounts.GetValueOrDefault(candidate) < 2)
            .Select(candidate => new
            {
                Value = candidate,
                UseCount = useCounts.GetValueOrDefault(candidate),
                Distance = Math.Abs(candidate - average),
                TieBreak = _rng.Next(),
            })
            .OrderBy(candidate => candidate.UseCount)
            .ThenBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.TieBreak)
            .ToArray();

        foreach (var candidate in candidates)
        {
            targets[turn] = candidate.Value;
            useCounts[candidate.Value] = candidate.UseCount + 1;
            if (TryBuildCapacityTargets(
                    turn + 1,
                    remaining - candidate.Value,
                    totalTurns,
                    bounds,
                    useCounts,
                    targets))
            {
                return true;
            }

            if (candidate.UseCount == 0)
                useCounts.Remove(candidate.Value);
            else
                useCounts[candidate.Value] = candidate.UseCount;
            targets.Remove(turn);
        }

        return false;
    }

    private (int Min, int Max) TurnPopBounds(
        int turn,
        ForwardFeatureIntentPlan intentPlan)
    {
        var flushCount = intentPlan.ByTurn.TryGetValue(turn, out var intents)
            ? intents.Count(intent => intent.Kind == ForwardTimedFeatureKind.Flush)
            : 0;
        var normalColumns = _settings.COLS - flushCount;
        return (
            (flushCount * _settings.ROWS) + (normalColumns * _settings.MIN_PUSH),
            (flushCount * _settings.ROWS) + (normalColumns * _settings.MAX_PUSH));
    }

    private bool RequiresCapacityDirectedShapes(
        ForwardFeatureBudget budget,
        ForwardFeatureIntentPlan intentPlan,
        ForwardObjectives objectives)
    {
        if (objectives.WinningRoundPlan?.WinningCompletionTurn != null)
            return true;

        var required = RawCollectionTarget(objectives, intentPlan);
        var unpressuredFloor = 0;

        for (var turn = 1; turn <= budget.TotalTurns; turn++)
        {
            var flushCount = intentPlan.ByTurn.TryGetValue(turn, out var intents)
                ? intents.Count(intent => intent.Kind == ForwardTimedFeatureKind.Flush)
                : 0;
            var normalColumns = _settings.COLS - flushCount;
            var minPopped = (flushCount * _settings.ROWS)
                + (normalColumns * _settings.MIN_PUSH);
            var maxPopped = (flushCount * _settings.ROWS)
                + (normalColumns * _settings.MAX_PUSH);
            unpressuredFloor += ForwardTurnShapePlanner.MinimumUnpressuredPopCount(
                minPopped,
                maxPopped);
        }

        return required > unpressuredFloor;
    }

    private static int RawCollectionTarget(
        ForwardObjectives objectives,
        ForwardFeatureIntentPlan intentPlan)
    {
        var exactTargets = objectives.WinTargets.Values.Sum()
            + objectives.NearMissTargets.Values.Sum();
        var plannedWheelBonus = intentPlan.Intents
            .Where(intent => intent.Kind == ForwardTimedFeatureKind.Wheel && intent.IsCapacityRequiredWheel)
            .Sum(intent => Math.Max(0, intent.PlannedWheelCollectionBonus));

        return Math.Max(0, exactTargets - plannedWheelBonus);
    }

    private static int WinningDeadlineCollectionFloor(
        IReadOnlyDictionary<int, (int Min, int Max)> bounds,
        ForwardFeatureIntentPlan intentPlan,
        ForwardObjectives objectives)
    {
        var completionTurn = objectives.WinningRoundPlan?.WinningCompletionTurn;
        if (!completionTurn.HasValue)
            return 0;

        var requiredWheelBonus = intentPlan.Intents
            .Where(intent => intent.Kind == ForwardTimedFeatureKind.Wheel)
            .Where(intent => intent.IsCapacityRequiredWheel)
            .Where(intent => intent.WheelSymbol.HasValue
                && objectives.WinTargets.ContainsKey(intent.WheelSymbol.Value))
            .Sum(intent => Math.Max(0, intent.PlannedWheelCollectionBonus));
        var winningRaw = Math.Max(0, objectives.WinTargets.Values.Sum() - requiredWheelBonus);
        var mandatoryAfterDeadline = bounds
            .Where(entry => entry.Key > completionTurn.Value)
            .Sum(entry => entry.Value.Min);
        return winningRaw + mandatoryAfterDeadline;
    }

    private int SafeExtraCollectionCapacity(ForwardObjectives objectives)
    {
        return objectives.FillSymbols
            .Where(symbol => !objectives.WinTargets.ContainsKey(symbol))
            .Distinct()
            .Sum(symbol =>
            {
                var minimum = objectives.NearMissTargets.GetValueOrDefault(symbol);
                var limit = SymbolLedger.NonWinningCollectionLimit(
                    symbol,
                    objectives.NearMissTargets.ContainsKey(symbol) ? minimum : (int?)null,
                    _settings);
                return Math.Max(0, (limit - 1) - minimum);
            });
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
