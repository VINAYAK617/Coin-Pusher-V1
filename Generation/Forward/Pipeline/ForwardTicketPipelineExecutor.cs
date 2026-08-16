namespace CoinPusherEngine;

internal enum ForwardTicketPipelineStatus
{
    Valid,
    MissingObjectives,
    MissingFramePlan,
    FramePlanInvalid,
    StartingBoardFailed,
    TurnCycleFailed,
    TurnRecordFailed,
    FeatureCapacityMismatch,
    SymbolFinalInvalid,
    ExtraSpinFinalInvalid,
    PrizeUpgradeFinalInvalid,
    ActualCollectionMismatch,
}

internal sealed class ForwardTicketPipelinePlan
{
    internal ForwardTicketPipelinePlan(
        Cell?[,] startingBoard,
        IReadOnlyList<ForwardRecordedTurn> turns,
        IReadOnlyDictionary<int, int> plannedCollected,
        IReadOnlyDictionary<int, int> actualCollected,
        Cell?[,] finalBoard,
        ForwardStartingBoardResult startingBoardStats)
    {
        StartingBoard = startingBoard;
        Turns = turns;
        PlannedCollected = plannedCollected;
        ActualCollected = actualCollected;
        FinalBoard = finalBoard;
        StartingBoardStats = startingBoardStats;
    }

    internal Cell?[,] StartingBoard { get; }
    internal IReadOnlyList<ForwardRecordedTurn> Turns { get; }
    internal IReadOnlyDictionary<int, int> PlannedCollected { get; }
    internal IReadOnlyDictionary<int, int> ActualCollected { get; }
    internal Cell?[,] FinalBoard { get; }
    internal ForwardStartingBoardResult StartingBoardStats { get; }
}

internal sealed class ForwardTicketPipelineResult
{
    internal ForwardTicketPipelineResult(
        ForwardTicketPipelineStatus status,
        string detail,
        ForwardTicketPipelinePlan? plan)
    {
        Status = status;
        Detail = detail;
        Plan = plan;
    }

    internal ForwardTicketPipelineStatus Status { get; }
    internal string Detail { get; }
    internal ForwardTicketPipelinePlan? Plan { get; }
    internal bool IsValid => Status == ForwardTicketPipelineStatus.Valid;
}

internal sealed class ForwardTicketPipelineExecutor
{
    private readonly ICustomProfileSettings _settings;
    private readonly int _seed;

    internal ForwardTicketPipelineExecutor(ICustomProfileSettings settings, int seed)
    {
        _settings = settings;
        _seed = seed;
    }

    internal ForwardTicketPipelineResult Execute(
        ForwardObjectives? objectives,
        ForwardTurnFramePlan? framePlan)
    {
        if (objectives == null)
            return Fail(ForwardTicketPipelineStatus.MissingObjectives, "forward objectives are missing");
        if (framePlan == null)
            return Fail(ForwardTicketPipelineStatus.MissingFramePlan, "turn frame plan is missing");

        var framePlanCheck = ValidateFramePlan(framePlan);
        if (framePlanCheck != null)
            return Fail(ForwardTicketPipelineStatus.FramePlanInvalid, framePlanCheck);

        var symbolLedger = new SymbolLedger(
            objectives.WinTargets,
            objectives.NearMissTargets,
            objectives.MaxSymbol,
            _settings);
        var extraSpinLedger = new ForwardExtraSpinLedger(framePlan.TotalTurns, _settings);
        var prizeUpgradeLedger = BuildPrizeUpgradeLedger(objectives);

        var startingBoard = new ForwardStartingBoardPlanner(_settings, SeedFor("start", 0)).Plan(
            objectives,
            framePlan,
            symbolLedger);
        if (!startingBoard.IsValid || startingBoard.Board == null)
        {
            return Fail(
                ForwardTicketPipelineStatus.StartingBoardFailed,
                $"{startingBoard.Status}: {startingBoard.Detail}");
        }

        var boardState = new ForwardBoardState(startingBoard.Board, _settings);
        var actualCollected = new Dictionary<int, int>();
        var turns = new List<ForwardRecordedTurn>(framePlan.Frames.Count);
        var remainingCapacity = BuildFeatureCapacity(framePlan);
        var recorder = new ForwardTurnRecorder(_settings);

        foreach (var frame in framePlan.Frames.OrderBy(frame => frame.Turn))
        {
            var cycle = new ForwardTurnCycleExecutor(_settings, SeedFor("turn", frame.Turn)).ExecuteAndAdvance(
                frame,
                framePlan.TotalTurns,
                boardState,
                objectives,
                framePlan.FutureTurnsAfter(frame.Turn),
                remainingCapacity,
                symbolLedger,
                extraSpinLedger,
                prizeUpgradeLedger);
            if (!cycle.IsValid)
            {
                return Fail(
                    ForwardTicketPipelineStatus.TurnCycleFailed,
                    $"turn {frame.Turn}: {cycle.Status}: {cycle.Detail}");
            }

            AddCollected(actualCollected, cycle.Realization!.Collected);

            var record = recorder.Record(frame, cycle.Realization);
            if (!record.IsValid || record.Turn == null)
            {
                return Fail(
                    ForwardTicketPipelineStatus.TurnRecordFailed,
                    $"turn {frame.Turn}: {record.Status}: {record.Detail}");
            }

            turns.Add(record.Turn);

            var capacity = ConsumeFeatureCapacity(frame, remainingCapacity);
            if (capacity != null)
                return Fail(ForwardTicketPipelineStatus.FeatureCapacityMismatch, capacity);
        }

        var remainingFeature = remainingCapacity.FirstOrDefault(kv => kv.Value != 0);
        if (remainingFeature.Value != 0)
        {
            return Fail(
                ForwardTicketPipelineStatus.FeatureCapacityMismatch,
                $"feature {remainingFeature.Key} has {remainingFeature.Value} unconsumed scheduled occurrence(s)");
        }

        var symbolFinal = symbolLedger.ValidateFinal();
        if (symbolFinal.Count > 0)
        {
            return Fail(
                ForwardTicketPipelineStatus.SymbolFinalInvalid,
                symbolFinal[0].Detail);
        }

        var extraFinal = extraSpinLedger.ValidateFinal();
        if (!extraFinal.IsValid)
        {
            return Fail(
                ForwardTicketPipelineStatus.ExtraSpinFinalInvalid,
                extraFinal.Detail);
        }

        var prizeFinal = prizeUpgradeLedger.ValidateFinal();
        if (prizeFinal.Count > 0)
        {
            return Fail(
                ForwardTicketPipelineStatus.PrizeUpgradeFinalInvalid,
                prizeFinal[0].Detail);
        }

        var actualMismatch = AuditActualCollections(
            actualCollected,
            symbolLedger.Collected,
            objectives.MaxSymbol);
        if (actualMismatch != null)
            return Fail(ForwardTicketPipelineStatus.ActualCollectionMismatch, actualMismatch);

        return new ForwardTicketPipelineResult(
            ForwardTicketPipelineStatus.Valid,
            "ok",
            new ForwardTicketPipelinePlan(
                CloneBoard(startingBoard.Board),
                turns.ToArray(),
                symbolLedger.Collected.ToDictionary(kv => kv.Key, kv => kv.Value),
                actualCollected.ToDictionary(kv => kv.Key, kv => kv.Value),
                boardState.Snapshot(),
                startingBoard));
    }

    private ForwardPrizeUpgradeLedger BuildPrizeUpgradeLedger(ForwardObjectives objectives)
    {
        var targetTiers = objectives.PrizeTiers
            .Concat(objectives.NonWinPrizeTiers)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        return new ForwardPrizeUpgradeLedger(
            targetTiers,
            objectives.PrizeValues,
            objectives.MaxSymbol,
            _settings);
    }

    private string? ValidateFramePlan(ForwardTurnFramePlan framePlan)
    {
        if (framePlan.TotalTurns <= 0)
            return $"TotalTurns={framePlan.TotalTurns} must be positive";

        if (framePlan.Frames.Count != framePlan.TotalTurns)
            return $"frame count={framePlan.Frames.Count}, TotalTurns={framePlan.TotalTurns}";

        var seen = new HashSet<int>();
        foreach (var frame in framePlan.Frames)
        {
            if (frame.Turn <= 0 || frame.Turn > framePlan.TotalTurns)
                return $"frame turn {frame.Turn} is outside 1..{framePlan.TotalTurns}";

            if (!seen.Add(frame.Turn))
                return $"frame turn {frame.Turn} appears more than once";

            if (frame.Turn == framePlan.TotalTurns
                && frame.FeatureIntents.Any(intent => intent.IsBoardFeature))
            {
                return $"final turn {frame.Turn} contains a board feature";
            }
        }

        for (var turn = 1; turn <= framePlan.TotalTurns; turn++)
        {
            if (!seen.Contains(turn))
                return $"frame turn {turn} is missing";
        }

        return null;
    }

    private Dictionary<ForwardFeatureKind, int> BuildFeatureCapacity(ForwardTurnFramePlan framePlan) =>
        new()
        {
            [ForwardFeatureKind.Wheel] = Count(framePlan, ForwardTimedFeatureKind.Wheel),
            [ForwardFeatureKind.ExtraGo] = Count(framePlan, ForwardTimedFeatureKind.ExtraGo),
            [ForwardFeatureKind.PrizeUpgrade] = Count(framePlan, ForwardTimedFeatureKind.PrizeUpgrade),
        };

    private static int Count(ForwardTurnFramePlan framePlan, ForwardTimedFeatureKind kind) =>
        framePlan.Frames.Sum(frame => frame.FeatureIntents.Count(intent => intent.Kind == kind));

    private string? ConsumeFeatureCapacity(
        ForwardTurnFrame frame,
        Dictionary<ForwardFeatureKind, int> remainingCapacity)
    {
        foreach (var group in frame.FeatureIntents
                     .Select(ToFeatureKind)
                     .Where(kind => kind != null)
                     .GroupBy(kind => kind!.Value))
        {
            if (!remainingCapacity.TryGetValue(group.Key, out var remaining))
                return $"turn {frame.Turn}: capacity missing for {group.Key}";

            var next = remaining - group.Count();
            if (next < 0)
                return $"turn {frame.Turn}: consumed {group.Count()} {group.Key}, remaining before turn was {remaining}";

            remainingCapacity[group.Key] = next;
        }

        return null;
    }

    private static ForwardFeatureKind? ToFeatureKind(ForwardFeatureIntent intent) =>
        intent.Kind switch
        {
            ForwardTimedFeatureKind.Wheel => ForwardFeatureKind.Wheel,
            ForwardTimedFeatureKind.ExtraGo => ForwardFeatureKind.ExtraGo,
            ForwardTimedFeatureKind.PrizeUpgrade => ForwardFeatureKind.PrizeUpgrade,
            _ => null,
        };

    private static void AddCollected(
        Dictionary<int, int> total,
        IReadOnlyDictionary<int, int> turnCollected)
    {
        foreach (var (symbol, count) in turnCollected)
            total[symbol] = total.GetValueOrDefault(symbol) + count;
    }

    internal string? AuditActualCollections(
        IReadOnlyDictionary<int, int> actual,
        IReadOnlyDictionary<int, int> planned,
        int maxSymbol)
    {
        for (var symbol = 1; symbol <= maxSymbol; symbol++)
        {
            var actualCount = actual.GetValueOrDefault(symbol);
            var plannedCount = planned.GetValueOrDefault(symbol);
            if (actualCount != plannedCount)
                return $"symbol {symbol} actual collected={actualCount}, planned collected={plannedCount}";
        }

        var unknownActual = actual.Keys.FirstOrDefault(symbol => symbol < 1 || symbol > maxSymbol);
        if (unknownActual != 0)
            return $"actual collected contains unknown symbol {unknownActual}";

        return null;
    }

    private Cell?[,] CloneBoard(Cell?[,] board)
    {
        var clone = new Cell?[_settings.ROWS, _settings.COLS];
        for (var row = 0; row < _settings.ROWS; row++)
        {
            for (var col = 0; col < _settings.COLS; col++)
                clone[row, col] = board[row, col]?.Clone();
        }

        return clone;
    }

    private int SeedFor(string scope, int turn)
    {
        unchecked
        {
            var hash = _seed;
            foreach (var ch in scope)
                hash = (hash * 397) ^ ch;
            hash = (hash * 397) ^ turn;
            return hash == int.MinValue ? 0 : Math.Abs(hash);
        }
    }

    private static ForwardTicketPipelineResult Fail(
        ForwardTicketPipelineStatus status,
        string detail) =>
        new(status, detail, null);
}
