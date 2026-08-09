namespace CoinPusherEngine;

internal enum ForwardTimedFeatureKind
{
    Wheel,
    Flush,
    ExtraGo,
    PrizeUpgrade,
}

internal enum ForwardFeatureTimingStatus
{
    Valid,
    MissingBudget,
    InvalidTurnEnvelope,
    NoLegalTurn,
    ExtraGoTimelineInvalid,
    CountMismatch,
    FeatureOnFinalTurn,
}

internal readonly struct ForwardTimedFeature
{
    internal ForwardTimedFeature(ForwardTimedFeatureKind kind, int turn)
    {
        Kind = kind;
        Turn = turn;
    }

    internal ForwardTimedFeatureKind Kind { get; }
    internal int Turn { get; }
}

internal sealed class ForwardFeatureTiming
{
    internal ForwardFeatureTiming(
        int totalTurns,
        IReadOnlyList<ForwardTimedFeature> events)
    {
        TotalTurns = totalTurns;
        Events = events;
        ByTurn = events
            .GroupBy(feature => feature.Turn)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ForwardTimedFeature>)group
                    .OrderBy(feature => feature.Kind)
                    .ToArray());
    }

    internal int TotalTurns { get; }
    internal IReadOnlyList<ForwardTimedFeature> Events { get; }
    internal IReadOnlyDictionary<int, IReadOnlyList<ForwardTimedFeature>> ByTurn { get; }

    internal int Count(ForwardTimedFeatureKind kind) =>
        Events.Count(feature => feature.Kind == kind);

    internal IReadOnlyList<int> TurnsFor(ForwardTimedFeatureKind kind) =>
        Events
            .Where(feature => feature.Kind == kind)
            .Select(feature => feature.Turn)
            .OrderBy(turn => turn)
            .ToArray();
}

internal sealed class ForwardFeatureTimingResult
{
    internal ForwardFeatureTimingResult(
        ForwardFeatureTimingStatus status,
        string detail,
        ForwardFeatureTiming? timing)
    {
        Status = status;
        Detail = detail;
        Timing = timing;
    }

    internal ForwardFeatureTimingStatus Status { get; }
    internal string Detail { get; }
    internal ForwardFeatureTiming? Timing { get; }
    internal bool IsValid => Status == ForwardFeatureTimingStatus.Valid;
}

internal sealed class ForwardFeatureTimingPlanner
{
    private readonly Settings _settings;
    private readonly Random _rng;

    internal ForwardFeatureTimingPlanner(Settings settings, int seed)
    {
        _settings = settings;
        _rng = new Random(seed);
    }

    internal ForwardFeatureTimingResult Plan(ForwardFeatureBudget? budget)
    {
        if (budget == null)
            return Fail(ForwardFeatureTimingStatus.MissingBudget, "feature budget is missing");
        if (budget.BaseTurns != _settings.BASE_SPINS
            || budget.TotalTurns < budget.BaseTurns
            || budget.TotalTurns > _settings.MAX_SPINS
            || budget.TotalTurns != budget.BaseTurns + budget.ExtraGoCount)
        {
            return Fail(
                ForwardFeatureTimingStatus.InvalidTurnEnvelope,
                $"base={budget.BaseTurns}, total={budget.TotalTurns}, extraGo={budget.ExtraGoCount}");
        }

        var events = new List<ForwardTimedFeature>();

        var extra = ScheduleExtraGo(budget);
        if (!extra.IsValid) return extra.Result!;
        events.AddRange(extra.Events.Select(turn => new ForwardTimedFeature(ForwardTimedFeatureKind.ExtraGo, turn)));

        var wheel = AddSimpleFeatures(events, ForwardTimedFeatureKind.Wheel, budget.WheelCount, budget.TotalTurns, _settings.WheelFeatureConfig);
        if (!wheel.IsValid) return wheel;

        var flush = AddSimpleFeatures(events, ForwardTimedFeatureKind.Flush, budget.FlushCount, budget.TotalTurns, _settings.FlushFeatureConfig);
        if (!flush.IsValid) return flush;

        var prizeUpgrade = AddSimpleFeatures(
            events,
            ForwardTimedFeatureKind.PrizeUpgrade,
            budget.PrizeUpgradeCount,
            budget.TotalTurns,
            _settings.PrizeUpgradeFeatureConfig);
        if (!prizeUpgrade.IsValid) return prizeUpgrade;

        var finalCheck = ValidateFinalSchedule(events, budget);
        if (!finalCheck.IsValid) return finalCheck;

        return new ForwardFeatureTimingResult(
            ForwardFeatureTimingStatus.Valid,
            "ok",
            new ForwardFeatureTiming(
                budget.TotalTurns,
                events
                    .OrderBy(feature => feature.Turn)
                    .ThenBy(feature => feature.Kind)
                    .ToArray()));
    }

    private ExtraGoScheduleResult ScheduleExtraGo(ForwardFeatureBudget budget)
    {
        if (budget.ExtraGoCount == 0)
        {
            var baseOnly = ValidateExtraGoTimeline(budget, Array.Empty<int>());
            return baseOnly.IsValid
                ? ExtraGoScheduleResult.Ok(Array.Empty<int>())
                : ExtraGoScheduleResult.Fail(baseOnly);
        }

        var late = _rng.NextDouble() < _settings.PFeatureLatePlacement;
        var primary = TryExtraGoTurns(budget, late);
        if (primary.IsValid) return primary;

        var fallback = TryExtraGoTurns(budget, !late);
        return fallback.IsValid ? fallback : primary;
    }

    private ExtraGoScheduleResult TryExtraGoTurns(ForwardFeatureBudget budget, bool late)
    {
        var turns = late
            ? LateExtraGoTurns(budget).ToArray()
            : EarlyExtraGoTurns(budget).ToArray();

        if (turns.Length != budget.ExtraGoCount)
        {
            return ExtraGoScheduleResult.Fail(Fail(
                ForwardFeatureTimingStatus.NoLegalTurn,
                $"could not schedule {budget.ExtraGoCount} Extra Go feature(s) inside configured spin window"));
        }

        var timeline = ValidateExtraGoTimeline(budget, turns);
        return timeline.IsValid
            ? ExtraGoScheduleResult.Ok(turns)
            : ExtraGoScheduleResult.Fail(timeline);
    }

    private IEnumerable<int> LateExtraGoTurns(ForwardFeatureBudget budget)
    {
        var turns = new List<int>();
        for (var i = 0; i < budget.ExtraGoCount; i++)
        {
            var turn = budget.BaseTurns + i;
            if (!LegalExtraGoTurn(budget, turn))
                return Array.Empty<int>();
            turns.Add(turn);
        }

        return turns;
    }

    private IEnumerable<int> EarlyExtraGoTurns(ForwardFeatureBudget budget)
    {
        var candidates = Enumerable.Range(1, budget.BaseTurns)
            .Where(turn => LegalExtraGoTurn(budget, turn))
            .OrderBy(_ => _rng.Next())
            .Take(budget.ExtraGoCount)
            .OrderBy(turn => turn)
            .ToArray();

        foreach (var turn in candidates)
            yield return turn;
    }

    private bool LegalExtraGoTurn(ForwardFeatureBudget budget, int turn) =>
        turn >= Math.Max(1, _settings.ExtraSpinFeatureConfig.MinS)
        && turn <= Math.Min(_settings.ExtraSpinFeatureConfig.MaxS, budget.TotalTurns - 1)
        && turn < budget.TotalTurns;

    private ForwardFeatureTimingResult ValidateExtraGoTimeline(
        ForwardFeatureBudget budget,
        IReadOnlyCollection<int> extraGoTurns)
    {
        var extraByTurn = extraGoTurns
            .GroupBy(turn => turn)
            .ToDictionary(group => group.Key, group => group.Count());
        var ledger = new ForwardExtraSpinLedger(budget.TotalTurns, _settings);

        for (var turn = 1; turn <= budget.TotalTurns; turn++)
        {
            var begin = ledger.BeginTurn(turn);
            if (!begin.IsValid)
                return Fail(ForwardFeatureTimingStatus.ExtraGoTimelineInvalid, begin.Detail);

            var award = ledger.AwardExtraGo(turn, extraByTurn.GetValueOrDefault(turn));
            if (!award.IsValid)
                return Fail(ForwardFeatureTimingStatus.ExtraGoTimelineInvalid, award.Detail);
        }

        var final = ledger.ValidateFinal();
        return final.IsValid
            ? Ok()
            : Fail(ForwardFeatureTimingStatus.ExtraGoTimelineInvalid, final.Detail);
    }

    private ForwardFeatureTimingResult AddSimpleFeatures(
        List<ForwardTimedFeature> events,
        ForwardTimedFeatureKind kind,
        int count,
        int totalTurns,
        (double P, int Max, int MinS, int MaxS, int Ord) config)
    {
        for (var i = 0; i < count; i++)
        {
            var turn = PickSimpleFeatureTurn(events, kind, totalTurns, config);
            if (turn <= 0)
            {
                return Fail(
                    ForwardFeatureTimingStatus.NoLegalTurn,
                    $"no legal turn for {kind} feature #{i + 1}");
            }

            events.Add(new ForwardTimedFeature(kind, turn));
        }

        return Ok();
    }

    private int PickSimpleFeatureTurn(
        IReadOnlyList<ForwardTimedFeature> events,
        ForwardTimedFeatureKind kind,
        int totalTurns,
        (double P, int Max, int MinS, int MaxS, int Ord) config)
    {
        var maxTurn = Math.Min(config.MaxS, totalTurns - 1);
        var minTurn = Math.Max(1, config.MinS);
        if (minTurn > maxTurn) return 0;

        var usedTurns = kind == ForwardTimedFeatureKind.PrizeUpgrade
            ? events
                .Where(feature => feature.Kind == ForwardTimedFeatureKind.PrizeUpgrade)
                .Select(feature => feature.Turn)
                .ToHashSet()
            : new HashSet<int>();
        var legalTurns = Enumerable.Range(minTurn, maxTurn - minTurn + 1)
            .Where(turn => !usedTurns.Contains(turn))
            .ToArray();
        if (legalTurns.Length == 0) return 0;

        var useLate = _rng.NextDouble() < _settings.PFeatureLatePlacement;
        if (useLate)
        {
            var tailStart = Math.Max(minTurn, maxTurn - Math.Max(1, _settings.WinLateTailSpins) + 1);
            var lateTurns = legalTurns.Where(turn => turn >= tailStart).ToArray();
            if (lateTurns.Length > 0)
                return lateTurns[_rng.Next(lateTurns.Length)];
        }

        return legalTurns[_rng.Next(legalTurns.Length)];
    }

    private ForwardFeatureTimingResult ValidateFinalSchedule(
        IReadOnlyList<ForwardTimedFeature> events,
        ForwardFeatureBudget budget)
    {
        var finalFeature = events.FirstOrDefault(feature => feature.Turn >= budget.TotalTurns);
        if (finalFeature.Turn > 0)
        {
            return Fail(
                ForwardFeatureTimingStatus.FeatureOnFinalTurn,
                $"{finalFeature.Kind} scheduled on final turn {budget.TotalTurns}");
        }

        if (events.Count(feature => feature.Kind == ForwardTimedFeatureKind.Wheel) != budget.WheelCount
            || events.Count(feature => feature.Kind == ForwardTimedFeatureKind.Flush) != budget.FlushCount
            || events.Count(feature => feature.Kind == ForwardTimedFeatureKind.ExtraGo) != budget.ExtraGoCount
            || events.Count(feature => feature.Kind == ForwardTimedFeatureKind.PrizeUpgrade) != budget.PrizeUpgradeCount)
        {
            return Fail(ForwardFeatureTimingStatus.CountMismatch, "timed feature counts do not match budget");
        }

        return Ok();
    }

    private static ForwardFeatureTimingResult Ok() =>
        new(ForwardFeatureTimingStatus.Valid, "ok", null);

    private static ForwardFeatureTimingResult Fail(ForwardFeatureTimingStatus status, string detail) =>
        new(status, detail, null);

    private sealed class ExtraGoScheduleResult
    {
        private ExtraGoScheduleResult(
            IReadOnlyList<int> events,
            ForwardFeatureTimingResult? result)
        {
            Events = events;
            Result = result;
        }

        internal IReadOnlyList<int> Events { get; }
        internal ForwardFeatureTimingResult? Result { get; }
        internal bool IsValid => Result == null;

        internal static ExtraGoScheduleResult Ok(IReadOnlyList<int> events) =>
            new(events, null);

        internal static ExtraGoScheduleResult Fail(ForwardFeatureTimingResult result) =>
            new(Array.Empty<int>(), result);
    }
}
