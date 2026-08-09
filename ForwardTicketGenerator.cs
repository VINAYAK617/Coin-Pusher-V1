using Newtonsoft.Json;

namespace CoinPusherEngine;

internal enum ForwardTicketGenerationStatus
{
    Valid,
    BuildFailed,
    AdaptFailed,
    SerializationFailed,
    TicketCheckFailed,
}

internal sealed class ForwardTicketGenerationResult
{
    internal ForwardTicketGenerationResult(
        ForwardTicketGenerationStatus status,
        string detail,
        ForwardTicketBuildResult? build,
        ForwardGamePlanAdapterResult? adaptedPlan,
        TicketSerializer.TicketDto? ticket,
        string? json,
        TicketChecker.Report? checkerReport)
    {
        Status = status;
        Detail = detail;
        Build = build;
        AdaptedPlan = adaptedPlan;
        Ticket = ticket;
        Json = json;
        CheckerReport = checkerReport;
    }

    internal ForwardTicketGenerationStatus Status { get; }
    internal string Detail { get; }
    internal ForwardTicketBuildResult? Build { get; }
    internal ForwardGamePlanAdapterResult? AdaptedPlan { get; }
    internal TicketSerializer.TicketDto? Ticket { get; }
    internal string? Json { get; }
    internal TicketChecker.Report? CheckerReport { get; }
    internal bool IsValid => Status == ForwardTicketGenerationStatus.Valid;
}

internal sealed class ForwardTicketGenerator
{
    private readonly Settings _settings;
    private readonly int _seed;

    internal ForwardTicketGenerator(Settings settings, int seed)
    {
        _settings = settings;
        _seed = seed;
    }

    internal ForwardTicketGenerationResult Generate(IReadOnlyList<decimal>? prizeAmounts)
    {
        var build = new ForwardTicketBuilder(_settings, _seed).Build(prizeAmounts);
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
                DefaultValueHandling = DefaultValueHandling.Ignore,
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

        var report = TicketChecker.CheckTicket(ticket);
        if (!report.IsValid)
        {
            return new ForwardTicketGenerationResult(
                ForwardTicketGenerationStatus.TicketCheckFailed,
                FirstFailure(report),
                build,
                adapted,
                ticket,
                json,
                report);
        }

        return new ForwardTicketGenerationResult(
            ForwardTicketGenerationStatus.Valid,
            "ok",
            build,
            adapted,
            ticket,
            json,
            report);
    }

    private static string FirstFailure(TicketChecker.Report report)
    {
        var first = report.Checks.FirstOrDefault(check => check.Result == TicketChecker.Status.Fail);
        return first == null
            ? "ticket checker failed without a failing check item"
            : $"{first.Category}/{first.Name}: {first.Detail}";
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
            null,
            null);
}
