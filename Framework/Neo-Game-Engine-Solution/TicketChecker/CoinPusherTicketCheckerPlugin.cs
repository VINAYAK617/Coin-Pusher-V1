using CoinPusherEngine;
using GameEngine;

namespace TicketChecker
{

    public sealed class CoinPusherTicketCheckerPlugin
    {
        public CPTicketChecker.TicketCheckResult CheckJson(string json, ICustomProfileSettings settings) =>
            CPTicketChecker.CheckJson(settings, json);

        public CPTicketChecker.TicketCheckResult CheckObject(Ticket? ticket, ICustomProfileSettings settings) =>
            CPTicketChecker.CheckObject(settings, ticket);

        public CPTicketChecker.TicketCheckResult CheckObject(TicketSerializer.TicketDto? ticket, ICustomProfileSettings settings) =>
            CPTicketChecker.CheckObject(settings, ticket);

        public CPTicketChecker.Report CheckTicket(Ticket? ticket, ICustomProfileSettings settings) =>
            CPTicketChecker.CheckTicket(settings, ticket);

        public CPTicketChecker.Report CheckTicket(TicketSerializer.TicketDto? ticket, ICustomProfileSettings settings) =>
            CPTicketChecker.CheckTicket(settings, ticket);
    }
}
