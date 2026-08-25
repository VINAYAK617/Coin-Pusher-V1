namespace CoinPusherEngine;

internal enum ForwardFeatureFireIntegrationStatus
{
    Valid,
    MissingBoardState,
    BoardFeatureFireFailed,
}

internal sealed class ForwardFeatureFireIntegrationResult
{
    internal ForwardFeatureFireIntegrationResult(
        ForwardFeatureFireIntegrationStatus status,
        string detail,
        IReadOnlyList<ForwardFeatureFireEvent> events,
        int extraGoAwardCount,
        int wheelFireCount,
        int prizeUpgradeFireCount)
    {
        Status = status;
        Detail = detail;
        Events = events;
        ExtraGoAwardCount = extraGoAwardCount;
        WheelFireCount = wheelFireCount;
        PrizeUpgradeFireCount = prizeUpgradeFireCount;
    }

    internal ForwardFeatureFireIntegrationStatus Status { get; }
    internal string Detail { get; }
    internal IReadOnlyList<ForwardFeatureFireEvent> Events { get; }
    internal int ExtraGoAwardCount { get; }
    internal int WheelFireCount { get; }
    internal int PrizeUpgradeFireCount { get; }
    internal bool IsValid => Status == ForwardFeatureFireIntegrationStatus.Valid;
}

internal sealed class ForwardFeatureFireIntegrator
{
    private readonly ICustomProfileSettings _settings;
    private readonly ForwardFeatureExecutor _executor;

    internal ForwardFeatureFireIntegrator(ICustomProfileSettings settings)
    {
        _settings = settings;
        _executor = new ForwardFeatureExecutor(settings);
    }

    internal ForwardFeatureFireIntegrationResult FireCurrentBoard(ForwardBoardState? boardState)
    {
        if (boardState == null)
        {
            return Fail(
                ForwardFeatureFireIntegrationStatus.MissingBoardState,
                "board state is missing");
        }

        var fired = boardState.ApplyFeatureFire(_executor);
        if (!fired.IsValid)
        {
            return Fail(
                ForwardFeatureFireIntegrationStatus.BoardFeatureFireFailed,
                fired.Detail,
                fired.Events);
        }

        var events = fired.Events.ToArray();
        return new ForwardFeatureFireIntegrationResult(
            ForwardFeatureFireIntegrationStatus.Valid,
            "ok",
            events,
            events.Sum(e => e.ExtraGoAward),
            events.Count(e => e.FeatureSymbol == _settings.F_WHEEL),
            events.Count(e => e.FeatureSymbol == _settings.F_PRUP));
    }

    private static ForwardFeatureFireIntegrationResult Fail(
        ForwardFeatureFireIntegrationStatus status,
        string detail,
        IReadOnlyList<ForwardFeatureFireEvent>? events = null) =>
        new(
            status,
            detail,
            events ?? Array.Empty<ForwardFeatureFireEvent>(),
            events?.Sum(e => e.ExtraGoAward) ?? 0,
            0,
            0);
}
