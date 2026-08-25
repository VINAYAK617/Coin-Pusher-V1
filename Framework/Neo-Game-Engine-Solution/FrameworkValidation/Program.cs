using CoinPusherEngine;
using GameEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Profile1;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TicketChecker;

namespace FrameworkValidation
{
    internal static class Program
    {
        private static readonly int[] ExpectedTicketCounts =
        {
            1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
            1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
            1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
            1, 2, 2, 1, 2, 1, 1, 1, 1, 3,
            3, 2, 2, 2, 2, 3, 2, 2, 2, 1,
            5, 1, 2, 1, 1, 4, 4, 2, 2, 2,
            2, 1, 1, 1, 1, 1, 4, 3, 10, 9,
            8, 9, 38, 35, 34, 73,
            80,
        };

        private static int Main()
        {
            var settings = new Settings();
            var profile = new Profile();
            var failures = new List<string>();
            var tickets = new List<JObject>();

            Checker.ShowErrorCallback = message => failures.Add(message);
            for (var tier = 1; tier <= 77; tier++)
            {
                var resource = profile.GetOfflineTickets(tier);
                if (string.IsNullOrWhiteSpace(resource))
                {
                    failures.Add($"Tier{tier}: embedded tickets resource is missing");
                    continue;
                }

                var array = JObject.Parse(resource)["tickets"] as JArray;
                var expectedCount = ExpectedTicketCounts[tier - 1];
                if (array?.Count != expectedCount)
                {
                    failures.Add($"Tier{tier}: found {array?.Count ?? 0} tickets, expected {expectedCount}");
                    continue;
                }

                foreach (var token in array.OfType<JObject>())
                {
                    var ticketJson = token.ToString(Formatting.None);
                    var ticket = Ticket.Load(ticketJson);
                    if (ticket.Parameters.TierNumber != tier)
                        failures.Add($"Tier{tier}: ticket declares TierNumber={ticket.Parameters.TierNumber}");
                    if (ticket.Parameters.IndividualPrizes == null
                        || ticket.Parameters.IndividualPrizes.Length != 0
                        || ticket.Parameters.IndivdualPrizeSpecifiers == null
                        || ticket.Parameters.IndivdualPrizeSpecifiers.Length != 0)
                        failures.Add($"Tier{tier}: framework prize arrays must be present and empty");
                    if (Checker.CheckTicket(ticket, settings))
                        failures.Add($"Tier{tier}: TicketChecker rejected a fixed ticket");
                    tickets.Add((JObject)token.DeepClone());
                }
            }

            if (tickets.Count != 400)
                failures.Add($"fixed catalog contains {tickets.Count} tickets, expected 400");

            if (failures.Count == 0)
                RunDynamicPpsChecks(settings, failures);

            if (failures.Count == 0)
                RunMutationChecks(tickets, settings, failures);

            if (failures.Count > 0)
            {
                foreach (var failure in failures.Take(25))
                    Console.Error.WriteLine(failure);
                return 1;
            }

            Console.WriteLine("framework-validation: tickets=400 tiers=77 dynamicPps=76 mutations=4 failures=0");
            return 0;
        }

        private static void RunDynamicPpsChecks(Settings settings, ICollection<string> failures)
        {
            var settingsProperty = typeof(GameEngine.Engine).GetProperty(
                "Settings",
                BindingFlags.Static | BindingFlags.NonPublic);
            settingsProperty?.SetValue(null, settings);

            var generator = new CoinPusherTicketJsonGenerator();
            foreach (var combination in settings.PpsCombinations.OrderBy(item => item.Id))
            {
                var result = generator.GeneratePpsCombination(
                    combination.Id,
                    seed: 20260825 + combination.Id);
                if (!result.IsValid || result.Ticket == null)
                {
                    failures.Add($"PPS #{combination.Id}: dynamic fallback failed: {result.Detail}");
                    continue;
                }

                var expectedSymbols = combination.Components
                    .Select(item => item.SymbolId)
                    .OrderBy(id => id);
                var actualSymbols = result.Ticket.WinInfo.WinSymbols
                    .Select(item => item.Id)
                    .OrderBy(id => id);
                if (!actualSymbols.SequenceEqual(expectedSymbols))
                    failures.Add($"PPS #{combination.Id}: dynamic fallback selected the wrong symbols");

                var actualTiers = result.Ticket.WinInfo.PrizeTiers
                    .ToDictionary(item => item.SymId, item => item.Tier);
                if (combination.Components.Any(item =>
                        actualTiers.GetValueOrDefault(item.SymbolId) != item.Tier))
                    failures.Add($"PPS #{combination.Id}: dynamic fallback selected the wrong prize tier");

                var report = CPTicketChecker.CheckTicket(settings, result.Ticket);
                if (!report.IsValid)
                    failures.Add($"PPS #{combination.Id}: dynamic fallback failed TicketChecker");
            }
        }

        private static void RunMutationChecks(
            IReadOnlyList<JObject> tickets,
            Settings settings,
            ICollection<string> failures)
        {
            Checker.ShowErrorCallback = _ => { };
            ExpectRejected("wrong PPS tier", Mutate(tickets[0], root =>
                root["game"]!["publicState"]!["game"]!["Parameters"]!["TierNumber"] = 2));

            ExpectRejected("missing spawn", Mutate(tickets[0], root =>
                ((JArray)root["game"]!["publicState"]!["game"]!["GameData"]!["Turns"]![0]!["Spawns"]!).RemoveAt(0)));

            ExpectRejected("invalid spawn position", Mutate(tickets[0], root =>
                root["game"]!["publicState"]!["game"]!["GameData"]!["Turns"]![0]!["Spawns"]![0]!["Pos"] = -1));

            ExpectRejected("stack above maximum", Mutate(tickets[0], root =>
                root["game"]!["publicState"]!["game"]!["GameData"]!["Turns"]![0]!["Spawns"]![0]!["Stack"] = 8));

            void ExpectRejected(string name, string json)
            {
                if (!Checker.CheckTicket(json, settings))
                    failures.Add($"mutation '{name}' passed TicketChecker");
            }
        }

        private static string Mutate(JObject source, Action<JObject> mutation)
        {
            var clone = (JObject)source.DeepClone();
            mutation(clone);
            return clone.ToString(Formatting.None);
        }
    }
}
