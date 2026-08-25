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
    private readonly ICustomProfileSettings _settings;
    private readonly Random _rng;

    internal ForwardTurnShapePlanner(ICustomProfileSettings settings, Random rng)
    {
        _settings = settings;
        _rng = rng;
    }

    internal ForwardTurnShapePlanResult Plan(
        int minPoppedCells,
        int maxPoppedCells,
        IReadOnlySet<int>? flushColumns = null,
        IReadOnlySet<int>? blockedColumns = null,
        bool pressureMode = false,
        IReadOnlySet<long>? avoidedPushBags = null,
        IReadOnlyDictionary<long, int>? pushBagUseCounts = null,
        int? preferredPoppedCells = null)
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

        var preferredPopCount = preferredPoppedCells.HasValue
            ? ClosestLegalPopCount(legal, preferredPoppedCells.Value)
            : PickPreferredPopCount(legal, pressureMode);
        var capacityMatched = legal
            .Where(candidate => candidate.PoppedCellCount == preferredPopCount)
            .ToArray();
        var eligible = PreferLeastUsedPushBags(
            PreferFreshPushBags(capacityMatched, avoidedPushBags),
            pushBagUseCounts);
        var scored = eligible
            .Select(candidate => new ScoredCandidate(candidate, Score(candidate, pressureMode, preferredPopCount)))
            .ToArray();

        var bestScore = scored.Max(candidate => candidate.Score);
        var best = scored
            .Where(candidate => candidate.Score == bestScore)
            .OrderBy(_ => _rng.Next())
            .First()
            .Candidate;

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

    internal static long PushBagKey(ForwardTurnShape shape, ICustomProfileSettings settings) =>
        BuildPushBagKey(shape, settings);

    private IReadOnlyList<Candidate> PreferFreshPushBags(
        IReadOnlyList<Candidate> legal,
        IReadOnlySet<long>? avoidedPushBags)
    {
        if (avoidedPushBags == null || avoidedPushBags.Count == 0)
            return legal;

        var fresh = legal
            .Where(candidate => !avoidedPushBags.Contains(candidate.PushBagKey))
            .ToArray();

        return fresh.Length == 0
            ? legal
            : fresh;
    }

    private static IReadOnlyList<Candidate> PreferLeastUsedPushBags(
        IReadOnlyList<Candidate> legal,
        IReadOnlyDictionary<long, int>? pushBagUseCounts)
    {
        if (pushBagUseCounts == null || pushBagUseCounts.Count == 0)
            return legal;

        var minUse = legal.Min(candidate => pushBagUseCounts.GetValueOrDefault(candidate.PushBagKey));
        return legal
            .Where(candidate => pushBagUseCounts.GetValueOrDefault(candidate.PushBagKey) == minUse)
            .ToArray();
    }

    private static int ClosestLegalPopCount(IReadOnlyList<Candidate> legal, int preferred)
    {
        return legal
            .Select(candidate => candidate.PoppedCellCount)
            .Distinct()
            .OrderBy(count => Math.Abs(count - preferred))
            .ThenByDescending(count => count)
            .First();
    }

    private int PickPreferredPopCount(IReadOnlyList<Candidate> legal, bool pressureMode)
    {
        var counts = legal
            .Select(candidate => candidate.PoppedCellCount)
            .Distinct()
            .OrderBy(count => count)
            .ToArray();

        if (counts.Length == 1)
            return counts[0];

        var min = counts[0];
        var max = counts[counts.Length - 1];
        var span = max - min;
        var lower = pressureMode
            ? min + Math.Max(0, (int)Math.Round(span * 0.60))
            : MinimumUnpressuredPopCount(min, max);
        var upper = pressureMode
            ? max
            : min + Math.Max(0, (int)Math.Round(span * 0.80));

        lower = Math.Clamp(lower, min, max);
        upper = Math.Clamp(upper, lower, max);

        var window = counts
            .Where(count => count >= lower && count <= upper)
            .ToArray();
        if (window.Length == 0)
            window = counts;

        var third = Math.Max(1, (int)Math.Ceiling(window.Length / 3.0));
        var low = window.Take(third).ToArray();
        var mid = window.Skip(third).Take(third).ToArray();
        var high = window.Skip(third * 2).ToArray();

        var chosen = PickPopBucket(low, mid, high);
        return chosen[_rng.Next(chosen.Length)];
    }

    internal static int MinimumUnpressuredPopCount(int min, int max) =>
        min + Math.Max(0, (int)Math.Round((max - min) * 0.35));

    private int[] PickPopBucket(int[] low, int[] mid, int[] high)
    {
        var lowWeight = low.Length == 0 ? 0.0 : _settings.WPusherLowPop;
        var midWeight = mid.Length == 0 ? 0.0 : _settings.WPusherMidPop;
        var highWeight = high.Length == 0 ? 0.0 : _settings.WPusherHighPop;
        var total = lowWeight + midWeight + highWeight;
        if (total <= 0.0)
            return low.Length > 0 ? low : mid.Length > 0 ? mid : high;

        var roll = _rng.NextDouble() * total;
        if (roll < lowWeight) return low;
        roll -= lowWeight;
        if (roll < midWeight) return mid;
        return high;
    }

    private int Score(Candidate candidate, bool pressureMode, int preferredPopCount)
    {
        var score = 0;
        score += candidate.DistinctPushValues * 24;
        if (!candidate.IsSortedPattern) score += 35;
        if (candidate.IsAllSame) score -= 120;
        if (candidate.ContainsOne) score += 10;
        if (candidate.ContainsTwo) score += 12;
        if (candidate.ContainsThree) score += 12;
        if (candidate.ContainsFour) score += pressureMode ? 12 : 8;
        score -= candidate.FlushCount * 5;
        var capacityWeight = pressureMode ? 80 : 18;
        score -= Math.Abs(candidate.PoppedCellCount - preferredPopCount) * capacityWeight;
        return score;
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

    private static long BuildPushBagKey(ForwardTurnShape shape, ICustomProfileSettings settings)
    {
        var key = 0L;
        for (var push = settings.MIN_PUSH; push <= settings.MAX_PUSH; push++)
        {
            var count = shape.Pushers.Count(pusher =>
                !pusher.IsFlush(settings) && pusher.PushValue == push);
            key = (key * 8L) + count;
        }

        var flushCount = shape.Pushers.Count(pusher => pusher.IsFlush(settings));
        return (key * 8L) + flushCount;
    }

    private static ForwardTurnShapePlanResult Ok() =>
        new(ForwardTurnShapePlanStatus.Valid, "ok", null, 0, 0, false, false);

    private static ForwardTurnShapePlanResult Fail(ForwardTurnShapePlanStatus status, string detail) =>
        new(status, detail, null, 0, 0, false, false);

    private sealed class Candidate
    {
        internal Candidate(ForwardTurnShape shape, ICustomProfileSettings settings)
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
            PushBagKey = BuildPushBagKey(shape, settings);
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
        internal long PushBagKey { get; }
    }

    private readonly struct ScoredCandidate
    {
        internal ScoredCandidate(Candidate candidate, int score)
        {
            Candidate = candidate;
            Score = score;
        }

        internal Candidate Candidate { get; }
        internal int Score { get; }
    }
}
