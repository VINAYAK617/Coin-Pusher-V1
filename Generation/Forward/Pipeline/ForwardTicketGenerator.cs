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
    private readonly int _seed;

    internal ForwardTicketGenerator(int seed)
    {
        _seed = seed;
    }

    internal ForwardTicketGenerationResult Generate(IReadOnlyList<decimal>? prizeAmounts)
    {
        var build = new ForwardTicketBuilder(_seed).Build(prizeAmounts);
        if (!build.IsValid)
        {
            return Fail(
                ForwardTicketGenerationStatus.BuildFailed,
                $"{build.Status}: {build.Detail}",
                build);
        }

        var adapted = new ForwardGamePlanAdapter().Adapt(build);
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
            ticket = TicketSerializer.ToTicketObject(adapted.Plan);
            json = JsonConvert.SerializeObject(ticket, new JsonSerializerSettings
            {
                Formatting = Formatting.None,
                NullValueHandling = NullValueHandling.Ignore,
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
