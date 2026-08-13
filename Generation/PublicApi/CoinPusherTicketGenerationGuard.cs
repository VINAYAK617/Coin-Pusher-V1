namespace CoinPusherEngine;

public enum CoinPusherTicketGenerationGuardStatus
{
    Valid,
    GenerationRejected,
    AuditRejected,
    MissingJson,
}

public sealed class CoinPusherTicketGenerationGuardResult
{
    internal CoinPusherTicketGenerationGuardResult(
        CoinPusherTicketGenerationGuardStatus status,
        string detail,
        CoinPusherTicketGenerationResult generation,
        CoinPusherTicketGenerationAuditResult? audit)
    {
        Status = status;
        Detail = detail;
        Generation = generation;
        Audit = audit;
    }

    public CoinPusherTicketGenerationGuardStatus Status { get; }
    public string Detail { get; }
    public CoinPusherTicketGenerationResult Generation { get; }
    public CoinPusherTicketGenerationAuditResult? Audit { get; }
    public bool IsValid => Status == CoinPusherTicketGenerationGuardStatus.Valid;
    public int Seed => Generation.Seed;
    public string? Json => IsValid ? Generation.Json : null;
    public TicketSerializer.TicketDto? Ticket => IsValid ? Generation.Ticket : null;
    public GamePlan? Plan => IsValid ? Generation.Plan : null;
    public CoinPusherTicketGenerationAudit? Summary => IsValid ? Audit?.Audit : null;
}

public sealed class CoinPusherTicketGenerationGuard
{
    private readonly CoinPusherTicketGenerator _generator;
    private readonly CoinPusherTicketGenerationAuditor _auditor;

    public CoinPusherTicketGenerationGuard()
    {
        _generator = new CoinPusherTicketGenerator();
        _auditor = new CoinPusherTicketGenerationAuditor();
    }

    public CoinPusherTicketGenerationGuardResult Generate(
        IReadOnlyList<decimal>? prizeAmounts,
        int? seed = null)
    {
        var generation = _generator.Generate(prizeAmounts, seed);
        if (!generation.IsValid)
        {
            return new CoinPusherTicketGenerationGuardResult(
                CoinPusherTicketGenerationGuardStatus.GenerationRejected,
                $"{generation.Status}: {generation.Detail}",
                generation,
                null);
        }

        if (string.IsNullOrWhiteSpace(generation.Json))
        {
            return new CoinPusherTicketGenerationGuardResult(
                CoinPusherTicketGenerationGuardStatus.MissingJson,
                "generation completed without usable JSON",
                generation,
                null);
        }

        var audit = _auditor.Audit(generation);
        if (!audit.IsValid)
        {
            return new CoinPusherTicketGenerationGuardResult(
                CoinPusherTicketGenerationGuardStatus.AuditRejected,
                $"{audit.Status}: {audit.Detail}",
                generation,
                audit);
        }

        return new CoinPusherTicketGenerationGuardResult(
            CoinPusherTicketGenerationGuardStatus.Valid,
            "ok",
            generation,
            audit);
    }
}
