namespace CoinPusherEngine;

internal enum ForwardTurnShapePlanStatus
{
    Valid,
    InvalidBudget,
    InvalidFlushColumn,
    InvalidBlockedColumn,
    NoLegalShape,
}

internal sealed class ForwardTurnShapePlanResult
{
    internal ForwardTurnShapePlanResult(
        ForwardTurnShapePlanStatus status,
        string detail,
        ForwardTurnShape? shape,
        int poppedCellCount,
        int distinctPushValues,
        bool isSortedPattern,
        bool isAllSame)
    {
        Status = status;
        Detail = detail;
        Shape = shape;
        PoppedCellCount = poppedCellCount;
        DistinctPushValues = distinctPushValues;
        IsSortedPattern = isSortedPattern;
        IsAllSame = isAllSame;
    }

    internal ForwardTurnShapePlanStatus Status { get; }
    internal string Detail { get; }
    internal ForwardTurnShape? Shape { get; }
    internal int PoppedCellCount { get; }
    internal int DistinctPushValues { get; }
    internal bool IsSortedPattern { get; }
    internal bool IsAllSame { get; }
    internal bool IsValid => Status == ForwardTurnShapePlanStatus.Valid;
}

internal sealed class ForwardTurnShapePlanner
{
    private readonly Settings _settings;
    private readonly Random _rng;

    internal ForwardTurnShapePlanner(Settings settings, Random rng)
    {
        _settings = settings;
        _rng = rng;
    }

    internal ForwardTurnShapePlanResult Plan(
        int minPoppedCells,
        int maxPoppedCells,
        IReadOnlySet<int>? flushColumns = null,
        IReadOnlySet<int>? blockedColumns = null,
        bool pressureMode = false)
    {
        if (minPoppedCells < 0 || maxPoppedCells < minPoppedCells)
        {
            return Fail(
                ForwardTurnShapePlanStatus.InvalidBudget,
                $"invalid pop budget min={minPoppedCells}, max={maxPoppedCells}");
        }

        var flush = flushColumns ?? new HashSet<int>();
        var blocked = blockedColumns ?? new HashSet<int>();

        var columnCheck = ValidateColumns(flush, ForwardTurnShapePlanStatus.InvalidFlushColumn);
        if (columnCheck.Status != ForwardTurnShapePlanStatus.Valid) return columnCheck;
        columnCheck = ValidateColumns(blocked, ForwardTurnShapePlanStatus.InvalidBlockedColumn);
        if (columnCheck.Status != ForwardTurnShapePlanStatus.Valid) return columnCheck;

        if (flush.Overlaps(blocked))
        {
            return Fail(
                ForwardTurnShapePlanStatus.InvalidBlockedColumn,
                "a column cannot be both flushed and blocked");
        }

        var candidates = new List<Candidate>();
        BuildCandidates(0, new ForwardPusher[_settings.COLS], flush, blocked, candidates);

        var legal = candidates
            .Where(candidate => candidate.PoppedCellCount >= minPoppedCells)
            .Where(candidate => candidate.PoppedCellCount <= maxPoppedCells)
            .ToList();

        if (legal.Count == 0)
        {
            return Fail(
                ForwardTurnShapePlanStatus.NoLegalShape,
                $"no legal turn shape for pop budget {minPoppedCells}..{maxPoppedCells}");
        }

        var bestScore = legal.Max(candidate => Score(candidate, pressureMode));
        var best = legal
            .Where(candidate => Score(candidate, pressureMode) == bestScore)
            .OrderBy(_ => _rng.Next())
            .First();

        return new ForwardTurnShapePlanResult(
            ForwardTurnShapePlanStatus.Valid,
            "ok",
            best.Shape,
            best.PoppedCellCount,
            best.DistinctPushValues,
            best.IsSortedPattern,
            best.IsAllSame);
    }

    private void BuildCandidates(
        int col,
        ForwardPusher[] current,
        IReadOnlySet<int> flushColumns,
        IReadOnlySet<int> blockedColumns,
        List<Candidate> candidates)
    {
        if (col >= _settings.COLS)
        {
            var (shape, check) = ForwardTurnShape.TryCreate(current, _settings);
            if (check.IsValid)
                candidates.Add(new Candidate(shape!, _settings));
            return;
        }

        if (blockedColumns.Contains(col))
        {
            current[col] = new ForwardPusher(_settings.MIN_PUSH);
            BuildCandidates(col + 1, current, flushColumns, blockedColumns, candidates);
            return;
        }

        if (flushColumns.Contains(col))
        {
            current[col] = new ForwardPusher(_settings.ROWS, _settings.F_FLUSH_ID);
            BuildCandidates(col + 1, current, flushColumns, blockedColumns, candidates);
            return;
        }

        for (var push = _settings.MIN_PUSH; push <= _settings.MAX_PUSH; push++)
        {
            current[col] = new ForwardPusher(push);
            BuildCandidates(col + 1, current, flushColumns, blockedColumns, candidates);
        }
    }

    private int Score(Candidate candidate, bool pressureMode)
    {
        var score = 0;
        score += candidate.DistinctPushValues * 100;
        if (!candidate.IsSortedPattern) score += 60;
        if (!candidate.IsAllSame) score += 40;
        if (candidate.ContainsOne) score += 15;
        if (candidate.ContainsTwo) score += 20;
        if (candidate.ContainsThree) score += 25;
        if (candidate.ContainsFour) score += pressureMode ? 15 : 10;
        score -= candidate.FlushCount * 5;
        var capacityWeight = pressureMode ? 80 : 15;
        score -= Math.Abs(candidate.PoppedCellCount - PreferredPopCount(candidate.PoppedCellCount, pressureMode)) * capacityWeight;
        return score;
    }

    private int PreferredPopCount(int actual, bool pressureMode)
    {
        _ = actual;
        return pressureMode
            ? _settings.COLS * 4
            : (_settings.COLS * 3) - 1;
    }

    private ForwardTurnShapePlanResult ValidateColumns(
        IReadOnlySet<int> columns,
        ForwardTurnShapePlanStatus failureStatus)
    {
        foreach (var col in columns)
        {
            if (col < 0 || col >= _settings.COLS)
                return Fail(failureStatus, $"column {col} outside 0..{_settings.COLS - 1}");
        }

        return Ok();
    }

    private static bool SortedPattern(IReadOnlyList<int> values)
    {
        var ascending = true;
        var descending = true;
        for (var i = 1; i < values.Count; i++)
        {
            if (values[i] < values[i - 1]) ascending = false;
            if (values[i] > values[i - 1]) descending = false;
        }

        return ascending || descending;
    }

    private static ForwardTurnShapePlanResult Ok() =>
        new(ForwardTurnShapePlanStatus.Valid, "ok", null, 0, 0, false, false);

    private static ForwardTurnShapePlanResult Fail(ForwardTurnShapePlanStatus status, string detail) =>
        new(status, detail, null, 0, 0, false, false);

    private sealed class Candidate
    {
        internal Candidate(ForwardTurnShape shape, Settings settings)
        {
            Shape = shape;
            PoppedCellCount = shape.PoppedCellCount;
            var normalPushes = shape.Pushers
                .Where(pusher => !pusher.IsFlush(settings))
                .Select(pusher => pusher.PushValue)
                .ToArray();
            DistinctPushValues = normalPushes.Distinct().Count();
            IsSortedPattern = normalPushes.Length > 1 && SortedPattern(normalPushes);
            IsAllSame = normalPushes.Length > 1 && normalPushes.Distinct().Count() == 1;
            ContainsOne = normalPushes.Contains(1);
            ContainsTwo = normalPushes.Contains(2);
            ContainsThree = normalPushes.Contains(3);
            ContainsFour = normalPushes.Contains(4);
            FlushCount = shape.Pushers.Count(pusher => pusher.IsFlush(settings));
        }

        internal ForwardTurnShape Shape { get; }
        internal int PoppedCellCount { get; }
        internal int DistinctPushValues { get; }
        internal bool IsSortedPattern { get; }
        internal bool IsAllSame { get; }
        internal bool ContainsOne { get; }
        internal bool ContainsTwo { get; }
        internal bool ContainsThree { get; }
        internal bool ContainsFour { get; }
        internal int FlushCount { get; }
    }
}
