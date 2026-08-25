using Neo.GameEngine.Core;

namespace GameEngine
{
    public class Startup : BaseStartup
    {
        public Startup()
        {
        }

        public Startup(int forceSeed) : base(forceSeed)
        {
        }

        /// <summary>
        /// TO DELETE WHEN PLATFORM CAN HANDLE PROFILES
        /// </summary>
        /// <param name="winTier"></param>
        /// <param name="cashWin"></param>
        /// <param name="currency"></param>
        /// <param name="versionNum"></param>
        /// <param name="individualPrizes"></param>
        /// <param name="indivdualPrizeSpecifiers"></param>
        /// <param name="stake"></param>
        /// <param name="tierRNG"></param>
        /// <param name="profileName"></param>
        /// <param name="privateState"></param>
        /// <returns></returns>
        public override string GenerateGame(int[] winTier, decimal[] cashWin, string currency, int versionNum, decimal[] individualPrizes, int[] indivdualPrizeSpecifiers, decimal? stake, double[] tierRNG, string privateState)
        {
            string profile = "ALW-Coin-Pusher.dll";

            return GenerateGame(winTier, cashWin, currency, versionNum, individualPrizes, indivdualPrizeSpecifiers, stake, tierRNG, profile, privateState);
        }

        /// <summary>
        /// Entry point to generate a ticket
        /// </summary>
        /// <param name="winTier"></param>
        /// <param name="cashWin"></param>
        /// <param name="currency"></param>
        /// <param name="versionNum"></param>
        /// <param name="individualPrizes"></param>
        /// <param name="indivdualPrizeSpecifiers"></param>
        /// <param name="stake"></param>
        /// <param name="tierRNG"></param>
        /// <param name="profileName"></param>
        /// <param name="privateState"></param>
        /// <returns></returns>
        public string GenerateGame(int[] winTier, decimal[] cashWin, string currency, int versionNum, decimal[] individualPrizes, int[] indivdualPrizeSpecifiers, decimal? stake, double[] tierRNG, string profileName, string privateState)
        {
            return Init(winTier, cashWin, currency, versionNum, individualPrizes, indivdualPrizeSpecifiers, stake, tierRNG, profileName, privateState);
        }
    }
}
