using CoinPusherEngine;
using Neo.ComboGenerator.Core.Interfaces;

namespace GameEngine
{
    public interface ICustomProfileSettings : ISettings
    {
        int ROWS { get; }
        int COLS { get; }
        int MIN_PUSH { get; }
        int MAX_PUSH { get; }
        int FILL_CAP { get; }

        int BASE_SPINS { get; }
        int MAX_SPINS { get; }
        int MAX_COIN_STACK { get; }
        int MIN_WHEEL_STACK_VALUE { get; }
        int MAX_WHEEL_STACK_VALUE { get; }
        int NONWIN_MIN_TARGET { get; }

        int F_WHEEL { get; }
        int F_XSPIN { get; }
        int F_FLUSH_ID { get; }
        int F_PRUP { get; }
        int F_COIN { get; }

        int MaxPlanAttempts { get; }
        int LocalRealizationAttempts { get; }

        (double P, int Max, int MinS, int MaxS, int Ord) WheelFeatureConfig { get; }
        (double P, int Max, int MinS, int MaxS, int Ord) FlushFeatureConfig { get; }
        (double P, int Max, int MinS, int MaxS, int Ord) ExtraSpinFeatureConfig { get; }
        (double P, int Max, int MinS, int MaxS, int Ord) PrizeUpgradeFeatureConfig { get; }

        IReadOnlyList<PrizeLadderRow> PrizeLadderRows { get; }
        IReadOnlyList<WinningRoundRule> WinningRoundRules { get; }
        IReadOnlyList<PpsPrizeCombination> PpsCombinations { get; }
        IReadOnlyList<PpsSpinRule> PpsSpinRules { get; }

        double PWheelStackValue1 { get; }
        double PWheelStackValue2 { get; }
        double PWheelRepeatOptional { get; }
        double PWheelStackCollection { get; }
        double PWheelPreferDenseTarget { get; }

        double WExpBalanced { get; }
        double WExpNearMiss { get; }
        double WExpFeature { get; }
        double WExpStack { get; }
        double WExpLateWin { get; }

        double POptionalFeatureTicket { get; }
        double POptionalTicketWheel { get; }
        double POptionalTicketFlush { get; }
        double POptionalTicketPrizeUpgrade { get; }
        double PNoWinExtraGoOptional { get; }

        double PWheelOptional { get; }
        double PFlushOptional { get; }
        double PNonWinWheel { get; }
        double PNonWinPrizeUpgrade { get; }

        double PWinLateCompletion { get; }
        int WinLateTailSpins { get; }
        int WinLateMinTail { get; }
        int MaxDeferredCollectionsPerTurn { get; }
        double WinLateTailFraction { get; }

        (double P, int Min, int Max, int MaxSymbols)[] NonWinTargetProfiles { get; }
        double[] NonWinCountWeights { get; }

        double WNonWinLow { get; }
        double WNonWinMid { get; }
        double WNonWinHigh { get; }

        double PFeatureRetriggerChain { get; }
        double PFeatureLatePlacement { get; }
        double PFeatureSameTurn { get; }

        double WPusherLowPop { get; }
        double WPusherMidPop { get; }
        double WPusherHighPop { get; }

        int[] FeatureRetriggerBridgeIds { get; }
        
    }
}
