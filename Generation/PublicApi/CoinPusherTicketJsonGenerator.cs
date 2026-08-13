namespace CoinPusherEngine;

public enum CoinPusherTicketJsonGenerationStatus
{
    Valid,
    GuardRejected,
}

public sealed class CoinPusherTicketJsonGenerationResult
{
    internal CoinPusherTicketJsonGenerationResult(
        CoinPusherTicketJsonGenerationStatus status,
        string detail,
        CoinPusherTicketGenerationGuardResult guard,
        string? json)
    {
        Status = status;
        Detail = detail;
        Guard = guard;
        Json = json;
    }

    public CoinPusherTicketJsonGenerationStatus Status { get; }
    public string Detail { get; }
    public CoinPusherTicketGenerationGuardResult Guard { get; }
    public string? Json { get; }
    public bool IsValid => Status == CoinPusherTicketJsonGenerationStatus.Valid;
    public int Seed => Guard.Seed;
    public TicketSerializer.TicketDto? Ticket => IsValid ? Guard.Ticket : null;
    public GamePlan? Plan => IsValid ? Guard.Plan : null;
    public CoinPusherTicketGenerationAudit? Summary => IsValid ? Guard.Summary : null;
}

public sealed class CoinPusherTicketJsonGenerator
{
    private readonly CoinPusherTicketGenerationGuard _guard;

    public CoinPusherTicketJsonGenerator()
    {
        _guard = new CoinPusherTicketGenerationGuard();
    }

    public CoinPusherTicketJsonGenerationResult Generate(
        IReadOnlyList<decimal>? prizeAmounts,
        int? seed = null)
    {
        var guard = _guard.Generate(prizeAmounts, seed);
        if (!guard.IsValid || string.IsNullOrWhiteSpace(guard.Json))
        {
            return new CoinPusherTicketJsonGenerationResult(
                CoinPusherTicketJsonGenerationStatus.GuardRejected,
                $"{guard.Status}: {guard.Detail}",
                guard,
                null);
        }

        return new CoinPusherTicketJsonGenerationResult(
            CoinPusherTicketJsonGenerationStatus.Valid,
            "ok",
            guard,
            guard.Json);
    }

    public string GenerateJson(
        IReadOnlyList<decimal>? prizeAmounts,
        int? seed = null)
    {
        var result = Generate(prizeAmounts, seed);
        if (!result.IsValid)
            throw new InvalidOperationException(result.Detail);

        return result.Json!;
    }
}
