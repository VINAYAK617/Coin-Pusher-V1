namespace CoinPusherEngine;

public sealed class CoinPusherTicketCheckerPlugin
{
    public TicketChecker.TicketCheckResult CheckJson(string json) =>
        TicketChecker.CheckJson(json);

    public TicketChecker.TicketCheckResult CheckObject(Ticket? ticket) =>
        TicketChecker.CheckObject(ticket);

    public TicketChecker.TicketCheckResult CheckObject(TicketSerializer.TicketDto? ticket) =>
        TicketChecker.CheckObject(ticket);

    public TicketChecker.Report CheckTicket(Ticket? ticket) =>
        TicketChecker.CheckTicket(ticket);

    public TicketChecker.Report CheckTicket(TicketSerializer.TicketDto? ticket) =>
        TicketChecker.CheckTicket(ticket);
}
