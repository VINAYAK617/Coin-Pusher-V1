using CoinPusherEngine;
using Neo.GameEngine.Core;
using Neo.GameEngine.Core.Interfaces;
using Neo.GameEngine.Core.Models.Engine;
using Neo.GameEngine.Core.Models.Platform;
using System;

namespace GameEngine
{
    public class Engine : BaseEngine
    {
        internal static ICustomProfileSettings Settings { get; private set; } = null!;

        public Engine() : base(0)
        {
        }

        public Engine(int forceSeed) : base(forceSeed)
        {
        }

        protected override ITicket GenerateTicket(EngineParameters parameters, FreeGame freeGame)
        {
            Settings = (ICustomProfileSettings)parameters.Profile.Settings;

            var tierNumber = parameters.WinTiers[0];
            var generator = new CoinPusherTicketJsonGenerator(Settings);
            var result = tierNumber == 77
                ? generator.Generate(Array.Empty<decimal>(), _random.Next())
                : generator.GeneratePpsCombination(tierNumber, _random.Next());

            if (!result.IsValid || result.Ticket == null)
            {
                throw new InvalidOperationException(
                    $"Coin Pusher generation failed for tier {tierNumber}: {result.Detail}");
            }

            return new Ticket { GameData = result.Ticket };
        }
    }
}
