using GameEngine.NeoComboGeneratorConfig;
using System.Collections.Generic;

namespace Profile1.Configurations
{
    internal class MainGame : IMainGame
    {
        public int MAX_NB_PRIZES => 6;

        public int MAX_DUPLICATES => 1;

        public List<decimal> PRIZE_LIST => new List<decimal>
        {

        };

        public List<decimal> PRIZE_MULTIPLIERS => new()
        {

        };

        public int MAX_NB_MULTIPLIERS => 2;

        public List<decimal> GAME_MULTIPLIERS => new List<decimal>()
        {

        };
    }
}
