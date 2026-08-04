namespace CoinPusherEngine;

public sealed class Settings
{
    public static Settings Default { get; } = new();

    public int ROWS { get; init; } = 5;
    public int COLS { get; init; } = 5;
    public int MIN_PUSH { get; init; } = 1;
    public int MAX_PUSH { get; init; } = 4;
    public int FILL_CAP { get; init; } = 20;

    public int BASE_SPINS { get; init; } = 5;
    public int MAX_SPINS { get; init; } = 12;
    public int MAX_COIN_STACK { get; init; } = 7;
    public int MAX_EXTRA_GO_PER_TURN => MAX_SPINS - BASE_SPINS;
    public int MIN_WHEEL_STACK_VALUE { get; init; } = 1;
    public int MAX_WHEEL_STACK_VALUE { get; init; } = 3;
    public int NONWIN_MIN_TARGET { get; init; } = 10;

    public int F_WHEEL { get; init; } = 11;
    public int F_XSPIN { get; init; } = 12;
    public int F_FLUSH_ID { get; init; } = 14;
    public int F_PRUP { get; init; } = 13;
    public int F_COIN { get; init; } = 1;

    public double PWheelStackValue1 { get; init; } = Probability("COINPUSHER_P_WHEEL_STACK_VALUE_1", 0.45);
    public double PWheelStackValue2 { get; init; } = Probability("COINPUSHER_P_WHEEL_STACK_VALUE_2", 0.35);
    public double PWheelRepeatOptional { get; init; } = Probability("COINPUSHER_P_WHEEL_REPEAT_OPTIONAL", 0.35);

    public double WExpBalanced { get; init; } = Weight("COINPUSHER_W_EXP_BALANCED", 0.30);
    public double WExpNearMiss { get; init; } = Weight("COINPUSHER_W_EXP_NEARMISS", 0.25);
    public double WExpFeature { get; init; } = Weight("COINPUSHER_W_EXP_FEATURE", 0.20);
    public double WExpStack { get; init; } = Weight("COINPUSHER_W_EXP_STACK", 0.15);
    public double WExpLateWin { get; init; } = Weight("COINPUSHER_W_EXP_LATEWIN", 0.10);

    public double POptionalFeatureTicket { get; init; } = Probability("COINPUSHER_P_OPTIONAL_FEATURE_TICKET", 1.0 / 30.0);
    public double POptionalTicketWheel { get; init; } = Probability("COINPUSHER_P_OPTIONAL_TICKET_WHEEL", 1.0 / 3.0);
    public double POptionalTicketFlush { get; init; } = Probability("COINPUSHER_P_OPTIONAL_TICKET_FLUSH", 1.0 / 3.0);
    public double POptionalTicketPrizeUpgrade { get; init; } = Probability("COINPUSHER_P_OPTIONAL_TICKET_PRIZE_UPGRADE", 1.0 / 4.0);
    public double PNoWinExtraGoOptional { get; init; } = Probability("COINPUSHER_P_NOWIN_EXTRA_GO_OPTIONAL", 1.0 / 30.0);

    public double PWheelOptional { get; init; } = Probability("COINPUSHER_P_WHEEL_OPTIONAL", 0.30);
    public double PFlushOptional { get; init; } = Probability("COINPUSHER_P_FLUSH_OPTIONAL", 0.35);
    public double PNonWinWheel { get; init; } = Probability("COINPUSHER_P_NONWIN_WHEEL", 0.0);
    public double PNonWinPrizeUpgrade { get; init; } = Probability("COINPUSHER_P_NONWIN_PRIZE_UPGRADE", 1.0);

    public double PWinLateCompletion { get; init; } = Probability("COINPUSHER_P_WIN_LATE_COMPLETION", 0.90);
    public int WinLateTailSpins { get; init; } = 2;
    public int WinLateMinTail { get; init; } = 2;
    public double WinLateTailFraction { get; init; } = 0.20;

    public (double P, int Min, int Max, int MaxSymbols)[] NonWinTargetProfiles { get; init; } =
    {
        (0.05, 0, 0, 0),
        (0.95, 10, 19, 5),
    };

    public double[] NonWinCountWeights { get; init; } =
    {
        Weight("COINPUSHER_W_NONWIN_COUNT_1", 0.15),
        Weight("COINPUSHER_W_NONWIN_COUNT_2", 0.25),
        Weight("COINPUSHER_W_NONWIN_COUNT_3", 0.25),
        Weight("COINPUSHER_W_NONWIN_COUNT_4", 0.20),
        Weight("COINPUSHER_W_NONWIN_COUNT_5", 0.15),
    };

    public double WNonWinLow { get; init; } = Weight("COINPUSHER_W_NONWIN_LOW", 0.40);
    public double WNonWinMid { get; init; } = Weight("COINPUSHER_W_NONWIN_MID", 0.40);
    public double WNonWinHigh { get; init; } = Weight("COINPUSHER_W_NONWIN_HIGH", 0.20);

    public double PFeatureRetriggerChain { get; init; } = Probability("COINPUSHER_P_FEATURE_RETRIGGER_CHAIN", 0.25);
    public int[] FeatureRetriggerBridgeIds { get; init; } =
    {
        12,
        11,
        13,
    };

    public int MixedPushCapacity(int freeCols)
    {
        if (freeCols <= 0) return 0;
        if (freeCols >= 3)
            return (MAX_PUSH * (freeCols - 2)) + MIN_PUSH + (MIN_PUSH + 1);
        if (freeCols == 2)
            return MAX_PUSH + MIN_PUSH;
        return MAX_PUSH;
    }

    public int SymbolFillCap(int sym) => sym >= 5 ? 25 : FILL_CAP;

    public bool IsFeat(int id) => id == F_WHEEL || id == F_XSPIN || id == F_PRUP;

    private static double Probability(string envName, double fallback)
    {
        var raw = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return double.TryParse(raw, out var value)
            ? Math.Clamp(value, 0.0, 1.0)
            : fallback;
    }

    private static double Weight(string envName, double fallback)
    {
        var raw = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return double.TryParse(raw, out var value)
            ? Math.Max(0.0, value)
            : fallback;
    }
}
