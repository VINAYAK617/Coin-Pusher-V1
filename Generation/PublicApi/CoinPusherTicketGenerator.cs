namespace CoinPusherEngine;

public enum CoinPusherTicketGenerationStatus
{
    Valid,
    InvalidRequest,
    GenerationFailed,
}

public sealed class CoinPusherTicketGenerationResult
{
    internal CoinPusherTicketGenerationResult(
        CoinPusherTicketGenerationStatus status,
        string detail,
        int seed,
        string? json,
        TicketSerializer.TicketDto? ticket,
        GamePlan? plan,
        IReadOnlyList<decimal> requestedPrizeAmounts,
        IReadOnlyList<decimal> coveredPrizeAmounts,
        IReadOnlyList<decimal> skippedPrizeAmounts)
    {
        Status = status;
        Detail = detail;
        Seed = seed;
        Json = json;
        Ticket = ticket;
        Plan = plan;
        RequestedPrizeAmounts = requestedPrizeAmounts;
        CoveredPrizeAmounts = coveredPrizeAmounts;
        SkippedPrizeAmounts = skippedPrizeAmounts;
    }

    public CoinPusherTicketGenerationStatus Status { get; }
    public string Detail { get; }
    public int Seed { get; }
    public string? Json { get; }
    public TicketSerializer.TicketDto? Ticket { get; }
    public GamePlan? Plan { get; }
    public IReadOnlyList<decimal> RequestedPrizeAmounts { get; }
    public IReadOnlyList<decimal> CoveredPrizeAmounts { get; }
    public IReadOnlyList<decimal> SkippedPrizeAmounts { get; }
    public bool IsValid => Status == CoinPusherTicketGenerationStatus.Valid;
}

public sealed class CoinPusherTicketGenerator
{
    private static readonly Random SeedRng = new();
    private static readonly object SeedLock = new();

    private readonly CoinPusherTicketGenerationRequestValidator _requestValidator;

    public CoinPusherTicketGenerator()
    {
        _requestValidator = new CoinPusherTicketGenerationRequestValidator();
    }

    public CoinPusherTicketGenerationResult Generate(
        IReadOnlyList<decimal>? prizeAmounts,
        int? seed = null)
    {
        var request = _requestValidator.Validate(prizeAmounts);
        if (!request.IsValid)
        {
            return new CoinPusherTicketGenerationResult(
                CoinPusherTicketGenerationStatus.InvalidRequest,
                $"{request.Status}: {request.Detail}",
                seed ?? 0,
                null,
                null,
                null,
                prizeAmounts?.ToArray() ?? Array.Empty<decimal>(),
                Array.Empty<decimal>(),
                Array.Empty<decimal>());
        }

        var actualSeed = seed ?? NextSeed();
        var requestedPrizeAmounts = prizeAmounts!.ToArray();
        var maxAttempts = Settings.MaxPlanAttempts;
        ForwardTicketGenerationResult? lastGeneration = null;
        string? lastValidationDetail = null;
        var lastSeed = actualSeed;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var attemptSeed = SeedForAttempt(actualSeed, attempt);
            lastSeed = attemptSeed;
            var generated = new ForwardTicketGenerator(attemptSeed).Generate(requestedPrizeAmounts);
            lastGeneration = generated;
            if (!generated.IsValid)
                continue;

            var validation = new CoinPusherTicketGenerationValidator().Validate(requestedPrizeAmounts, generated);
            if (!validation.IsValid)
            {
                lastValidationDetail = $"{validation.Status}: {validation.Detail}";
                continue;
            }

            return new CoinPusherTicketGenerationResult(
                CoinPusherTicketGenerationStatus.Valid,
                "ok",
                attemptSeed,
                generated.Json,
                generated.Ticket,
                generated.AdaptedPlan?.Plan,
                requestedPrizeAmounts,
                generated.Build?.MathInput?.Bundle?.Covered.ToArray() ?? Array.Empty<decimal>(),
                generated.Build?.MathInput?.Bundle?.Skipped.ToArray() ?? Array.Empty<decimal>());
        }

        var detail = lastGeneration == null
            ? "no generation attempt was executed"
            : lastGeneration.IsValid
                ? lastValidationDetail ?? "generation validation failed"
                : $"{lastGeneration.Status}: {lastGeneration.Detail}";

        return new CoinPusherTicketGenerationResult(
            CoinPusherTicketGenerationStatus.GenerationFailed,
            detail,
            lastSeed,
            null,
            null,
            null,
            requestedPrizeAmounts,
            lastGeneration?.Build?.MathInput?.Bundle?.Covered.ToArray() ?? Array.Empty<decimal>(),
            lastGeneration?.Build?.MathInput?.Bundle?.Skipped.ToArray() ?? Array.Empty<decimal>());
    }

    private static int NextSeed()
    {
        lock (SeedLock) return SeedRng.Next(1, int.MaxValue);
    }

    private static int SeedForAttempt(int seed, int attempt)
    {
        if (attempt == 0) return seed;

        unchecked
        {
            var next = seed;
            next = (next * 397) ^ attempt;
            next ^= 0x6d2b79f5;
            return next == int.MinValue ? 0 : Math.Abs(next);
        }
    }
}
