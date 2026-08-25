using Newtonsoft.Json;

namespace CoinPusherEngine;

internal enum ForwardTicketGenerationStatus
{
    Valid,
    BuildFailed,
    AdaptFailed,
    SerializationFailed,
}

internal sealed class ForwardTicketGenerationResult
{
    internal ForwardTicketGenerationResult(
        ForwardTicketGenerationStatus status,
        string detail,
        ForwardTicketBuildResult? build,
        ForwardGamePlanAdapterResult? adaptedPlan,
        TicketSerializer.TicketDto? ticket,
        string? json)
    {
        Status = status;
        Detail = detail;
        Build = build;
        AdaptedPlan = adaptedPlan;
        Ticket = ticket;
        Json = json;
    }

    internal ForwardTicketGenerationStatus Status { get; }
    internal string Detail { get; }
    internal ForwardTicketBuildResult? Build { get; }
    internal ForwardGamePlanAdapterResult? AdaptedPlan { get; }
    internal TicketSerializer.TicketDto? Ticket { get; }
    internal string? Json { get; }
    internal bool IsValid => Status == ForwardTicketGenerationStatus.Valid;
}

internal sealed class ForwardTicketGenerator
{
    private readonly ICustomProfileSettings _settings;
    private readonly int _seed;

    internal ForwardTicketGenerator(ICustomProfileSettings settings, int seed)
    {
        _settings = settings;
        _seed = seed;
    }

    internal ForwardTicketGenerationResult Generate(
        IReadOnlyList<decimal>? prizeAmounts,
        int? exactPpsCombinationId = null)
    {
        var build = new ForwardTicketBuilder(_settings, _seed).Build(
            prizeAmounts,
            exactPpsCombinationId);
        if (!build.IsValid)
        {
            return Fail(
                ForwardTicketGenerationStatus.BuildFailed,
                $"{build.Status}: {build.Detail}",
                build);
        }

        var adapted = new ForwardGamePlanAdapter(_settings).Adapt(build);
        if (!adapted.IsValid || adapted.Plan == null)
        {
            return Fail(
                ForwardTicketGenerationStatus.AdaptFailed,
                $"{adapted.Status}: {adapted.Detail}",
                build,
                adapted);
        }

        TicketSerializer.TicketDto ticket;
        string json;
        try
        {
            ticket = TicketSerializer.ToTicketObject(adapted.Plan, _settings);
            json = JsonConvert.SerializeObject(ticket, new JsonSerializerSettings
            {
                Formatting = Formatting.None,
                NullValueHandling = NullValueHandling.Ignore,
                // Pos=0 is a valid board position and must never be omitted.
                DefaultValueHandling = DefaultValueHandling.Include,
            });
        }
        catch (Exception ex)
        {
            return Fail(
                ForwardTicketGenerationStatus.SerializationFailed,
                ex.Message,
                build,
                adapted);
        }

        return new ForwardTicketGenerationResult(
            ForwardTicketGenerationStatus.Valid,
            "ok",
            build,
            adapted,
            ticket,
            json);
    }

    private static ForwardTicketGenerationResult Fail(
        ForwardTicketGenerationStatus status,
        string detail,
        ForwardTicketBuildResult? build = null,
        ForwardGamePlanAdapterResult? adaptedPlan = null) =>
        new(
            status,
            detail,
            build,
            adaptedPlan,
            null,
            null);
}
