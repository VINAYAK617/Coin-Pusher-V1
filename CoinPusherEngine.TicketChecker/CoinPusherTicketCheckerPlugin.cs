namespace CoinPusherEngine;

using GameEngine;

public sealed class CoinPusherTicketCheckerPlugin
{
    private readonly TicketChecker _checker;

    public CoinPusherTicketCheckerPlugin(ICustomProfileSettings settings)
    {
        _checker = new TicketChecker(settings);
    }

    public TicketChecker.TicketCheckResult CheckJson(string json) =>
        _checker.CheckJson(json);

    public TicketChecker.TicketCheckResult CheckObject(Ticket? ticket) =>
        _checker.CheckObject(ticket);

    public TicketChecker.TicketCheckResult CheckObject(TicketSerializer.TicketDto? ticket) =>
        _checker.CheckObject(ticket);

    public TicketChecker.Report CheckTicket(Ticket? ticket) =>
        _checker.CheckTicket(ticket);

    public TicketChecker.Report CheckTicket(TicketSerializer.TicketDto? ticket) =>
        _checker.CheckTicket(ticket);
}
