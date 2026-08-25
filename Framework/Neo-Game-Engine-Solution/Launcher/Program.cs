using GameEngine;
using Neo.ComboGenerator.Core;
using Neo.GameEngine.Core.Models.Enums;
using System;
using System.Linq;
using TicketChecker;

namespace Launcher
{
    internal static class Program
    {
        private const string ProfileName = "ALW-Coin-Pusher.dll";

        private static void Main(string[] args)
        {
            var tierNumber = args.Length > 0 ? int.Parse(args[0]) : 77;
            BaseProfile profile = new Profile1.Profile();

            Checker.ShowErrorCallback = Console.Error.WriteLine;
            var ticket = RunEngine(
                stake: 1m,
                tierNumber,
                profile,
                forceSeed: null,
                checkTicket: true);

            Console.WriteLine(ticket);
        }

        private static string RunEngine(
            decimal stake,
            int tierNumber,
            BaseProfile profile,
            int? forceSeed,
            bool checkTicket)
        {
            var tier = profile.Settings.Tiers.Single(item => item.TierNumber == tierNumber);
            var winTiers = new[] { tierNumber, 0 };
            var cashWins = new[] { tier.StakeMultiplier * stake, 0m };
            var engine = forceSeed.HasValue ? new Startup(forceSeed.Value) : new Startup();

            var ticket = engine.GenerateGame(
                winTiers,
                cashWins,
                "$",
                versionNum: 1,
                Array.Empty<decimal>(),
                Array.Empty<int>(),
                stake,
                Array.Empty<double>(),
                ProfileName,
                GetFakePrivateState());

            if (checkTicket && Checker.CheckTicket(ticket, profile.Settings))
                throw new InvalidOperationException("Generated ticket failed TicketChecker validation.");

            return ticket;
        }

        private static string GetFakePrivateState() =>
            "{\"publicState\":{\"action\":\"" + PlatformAction.START + "\"},\"privateState\":{}}";
    }
}
