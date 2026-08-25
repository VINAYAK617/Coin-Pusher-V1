using CoinPusherEngine;
using GameEngine;
using Neo.ComboGenerator.Core.Interfaces;
using System;

namespace TicketChecker
{
    public static class Checker
    {
        public delegate void ErrorFoundCallback(string errorString);

        public static ErrorFoundCallback? ShowErrorCallback { get; set; }

        // Returns true when an error is found, preserving the framework plugin contract.
        public static bool CheckTicket(string jsonTicket, ISettings settings)
        {
            try
            {
                return CheckTicket(Ticket.Load(jsonTicket), settings);
            }
            catch (Exception ex)
            {
                ShowErrorCallback?.Invoke($"Ticket could not be loaded: {ex.Message}");
                return true;
            }
        }

        public static bool CheckTicket(Ticket ticket, ISettings settings)
        {
            if (settings is not ICustomProfileSettings gameSettings)
            {
                ShowErrorCallback?.Invoke("Profile settings do not implement ICustomProfileSettings.");
                return true;
            }

            var errors = CPTicketChecker.CheckTicket(gameSettings, ticket).ToResult().Errors;
            foreach (var error in errors)
                ShowErrorCallback?.Invoke(error);

            return errors.Count > 0;
        }
    }
}
