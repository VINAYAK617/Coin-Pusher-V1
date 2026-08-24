namespace CoinPusherEngine;

using GameEngine;

public enum CoinPusherTicketGenerationAuditStatus
{
    Valid,
    MissingGenerationResult,
    GenerationNotValid,
    MissingTicket,
    MissingPlan,
}

public sealed class CoinPusherTicketGenerationAudit
{
    internal CoinPusherTicketGenerationAudit(
        int seed,
        int totalSpins,
        int winSymbolCount,
        int nonWinSymbolCount,
        int featureSpawnCount,
        int flushPusherCount,
        int extraGoFeatureCount,
        int wheelFeatureCount,
        int prizeUpgradeFeatureCount,
        int normalSpawnCount,
        int stackedNormalSpawnCount,
        IReadOnlyList<decimal> requestedPrizeAmounts,
        IReadOnlyList<decimal> coveredPrizeAmounts,
        IReadOnlyList<decimal> skippedPrizeAmounts)
    {
        Seed = seed;
        TotalSpins = totalSpins;
        WinSymbolCount = winSymbolCount;
        NonWinSymbolCount = nonWinSymbolCount;
        FeatureSpawnCount = featureSpawnCount;
        FlushPusherCount = flushPusherCount;
        ExtraGoFeatureCount = extraGoFeatureCount;
        WheelFeatureCount = wheelFeatureCount;
        PrizeUpgradeFeatureCount = prizeUpgradeFeatureCount;
        NormalSpawnCount = normalSpawnCount;
        StackedNormalSpawnCount = stackedNormalSpawnCount;
        RequestedPrizeAmounts = requestedPrizeAmounts;
        CoveredPrizeAmounts = coveredPrizeAmounts;
        SkippedPrizeAmounts = skippedPrizeAmounts;
    }

    public int Seed { get; }
    public int TotalSpins { get; }
    public int WinSymbolCount { get; }
    public int NonWinSymbolCount { get; }
    public int FeatureSpawnCount { get; }
    public int FlushPusherCount { get; }
    public int ExtraGoFeatureCount { get; }
    public int WheelFeatureCount { get; }
    public int PrizeUpgradeFeatureCount { get; }
    public int NormalSpawnCount { get; }
    public int StackedNormalSpawnCount { get; }
    public IReadOnlyList<decimal> RequestedPrizeAmounts { get; }
    public IReadOnlyList<decimal> CoveredPrizeAmounts { get; }
    public IReadOnlyList<decimal> SkippedPrizeAmounts { get; }
    public bool HasWin => WinSymbolCount > 0;
    public bool HasFeature => FeatureSpawnCount > 0 || FlushPusherCount > 0;
}

public sealed class CoinPusherTicketGenerationAuditResult
{
    internal CoinPusherTicketGenerationAuditResult(
        CoinPusherTicketGenerationAuditStatus status,
        string detail,
        CoinPusherTicketGenerationAudit? audit)
    {
        Status = status;
        Detail = detail;
        Audit = audit;
    }

    public CoinPusherTicketGenerationAuditStatus Status { get; }
    public string Detail { get; }
    public CoinPusherTicketGenerationAudit? Audit { get; }
    public bool IsValid => Status == CoinPusherTicketGenerationAuditStatus.Valid;
}

public sealed class CoinPusherTicketGenerationAuditor
{
    private readonly ICustomProfileSettings _settings;

    public CoinPusherTicketGenerationAuditor()
        : this(Settings)
    {
    }

    internal CoinPusherTicketGenerationAuditor(ICustomProfileSettings settings)
    {
        _settings = settings;
    }

    public CoinPusherTicketGenerationAuditResult Audit(CoinPusherTicketGenerationResult? result)
    {
        if (result == null)
            return Fail(CoinPusherTicketGenerationAuditStatus.MissingGenerationResult, "generation result is missing");
        if (!result.IsValid)
            return Fail(CoinPusherTicketGenerationAuditStatus.GenerationNotValid, $"{result.Status}: {result.Detail}");
        if (result.Ticket == null)
            return Fail(CoinPusherTicketGenerationAuditStatus.MissingTicket, "ticket DTO is missing");
        if (result.Plan == null)
            return Fail(CoinPusherTicketGenerationAuditStatus.MissingPlan, "verified GamePlan is missing");

        var featureSpawns = result.Ticket.Turns
            .SelectMany(turn => turn.Spawns)
            .Where(spawn => spawn.Feature != null)
            .ToArray();
        var normalSpawns = result.Ticket.Turns
            .SelectMany(turn => turn.Spawns)
            .Where(spawn => spawn.Feature == null)
            .ToArray();

        var audit = new CoinPusherTicketGenerationAudit(
            result.Seed,
            result.Ticket.WinInfo.TotalSpins,
            result.Ticket.WinInfo.WinSymbols.Length,
            result.Ticket.WinInfo.NonWinSymbols.Length,
            featureSpawns.Length,
            result.Ticket.Turns.Sum(turn => turn.Pushers.Count(pusher => pusher.FeatureId == _settings.F_FLUSH_ID)),
            featureSpawns.Count(spawn => spawn.Feature!.FeatureId == _settings.F_XSPIN),
            featureSpawns.Count(spawn => spawn.Feature!.FeatureId == _settings.F_WHEEL),
            featureSpawns.Count(spawn => spawn.Feature!.FeatureId == _settings.F_PRUP),
            normalSpawns.Length,
            normalSpawns.Count(spawn => spawn.Stack.HasValue && spawn.Stack.Value > 1),
            result.RequestedPrizeAmounts.ToArray(),
            result.CoveredPrizeAmounts.ToArray(),
            result.SkippedPrizeAmounts.ToArray());

        return new CoinPusherTicketGenerationAuditResult(
            CoinPusherTicketGenerationAuditStatus.Valid,
            "ok",
            audit);
    }

    private static CoinPusherTicketGenerationAuditResult Fail(
        CoinPusherTicketGenerationAuditStatus status,
        string detail) =>
        new(status, detail, null);
}
