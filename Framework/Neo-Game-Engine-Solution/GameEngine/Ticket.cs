using Neo.GameEngine.Core.Engine;
using Neo.GameEngine.Core.Interfaces;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using static CoinPusherEngine.TicketSerializer;

namespace GameEngine
{
    public class Ticket : ITicket
    {
        public ITicketParameters Parameters { get; set; } = new TicketParameters();
        public TicketDto GameData { get; set; } = null!;

        /// <summary>
        /// Load a ticket as a json string and turn it into an object
        /// </summary>
        /// <param name="jsonString">json string.</param>
        /// <returns></returns>
        public static Ticket Load(string jsonString)
        {
            var root = JObject.Parse(jsonString);
            var game = root["game"]?["publicState"]?["game"]
                ?? throw new JsonException("Ticket JSON is missing game.publicState.game.");

            return game.ToObject<Ticket>()
                ?? throw new JsonException("Ticket JSON could not be mapped to GameEngine.Ticket.");
        }

        /// <summary>
        /// Get the status of a ticket before to load it.
        /// </summary>
        /// <param name="jsonString"></param>
        /// <returns></returns>
        public static string GetTicketStatusFromJsonString(string jsonString)
        {
            JObject jsonObject;

            try
            {
                jsonObject = JObject.Parse(jsonString);
            }
            catch (Exception)
            {
                return "Could not load the ticket";
            }

            return jsonObject["error"]?.ToString() ?? "Could not read ticket status. Ticket not loaded.";
        }
    }
}
