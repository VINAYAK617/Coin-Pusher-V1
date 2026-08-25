using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace UIGameEngine
{
    internal class ServerConfig
    {
        internal class SubWin
        {

            [JsonProperty("individualPrizeSpecifier")]
            public int IndividualPrizeSpecifier { get; set; }

            [JsonProperty("winRatio")]
            public double WinRatio { get; set; }
        }

        internal class Version
        {

            [JsonProperty("versionNum")]
            public int VersionNum { get; set; }

            [JsonProperty("frequency")]
            public int Frequency { get; set; }

            [JsonProperty("subWins")]
            public List<SubWin> SubWins { get; set; }
        }

        internal class Tier
        {

            [JsonProperty("winTier")]
            public int WinTier { get; set; }

            [JsonProperty("ticketCount")]
            public int TicketCount { get; set; }

            [JsonProperty("versions")]
            public List<Version> Versions { get; set; }
        }

        [JsonProperty("gameID")]
        public int GameID { get; set; }

        [JsonProperty("gameName")]
        public string GameName { get; set; }

        [JsonProperty("poolSize")]
        public int PoolSize { get; set; }

        [JsonProperty("tiers")]
        public List<Tier> Tiers { get; set; }

        /// <summary>
        /// Load a server config as a json string and turn it into an object
        /// </summary>
        /// <param name="jsonString">json string.</param>
        /// <returns></returns>
        public static ServerConfig Load(string jsonString)
        {
            ServerConfig serverConfig;

            try
            {
                serverConfig = JsonConvert.DeserializeObject<ServerConfig>(jsonString);
            }
            catch (Exception)
            {

                serverConfig = null;
            }

            return serverConfig;
        }
    }

}

