namespace CoinPusherEngine;

/// <summary>
/// Hard board and symbol constants. Gameplay tuning belongs in Settings.
/// </summary>
internal static class K
{
    internal const int ROWS = 5;
    internal const int COLS = 5;
    internal const int MIN_PUSH = 1;
    internal const int MAX_PUSH = 4;
    internal const int FILL_CAP = 20;

    internal static int MixedPushCapacity(int freeCols)
    {
        if (freeCols <= 0) return 0;
        if (freeCols >= 3)
            return (MAX_PUSH * (freeCols - 2)) + MIN_PUSH + (MIN_PUSH + 1);
        if (freeCols == 2)
            return MAX_PUSH + MIN_PUSH;
        return MAX_PUSH;
    }

    internal const int BASE_SPINS = 5;
    internal const int MAX_SPINS = 12;
    internal const int MAX_COIN_STACK = 7;
    internal const int MAX_EXTRA_GO_PER_TURN = MAX_SPINS - BASE_SPINS;
    internal const int MIN_WHEEL_STACK_VALUE = 1;
    internal const int MAX_WHEEL_STACK_VALUE = 3;

    internal const int NONWIN_MIN_TARGET = 10;

    internal static int SymbolFillCap(int sym) => sym >= 5 ? 25 : FILL_CAP;

    internal const int F_WHEEL = 11;
    internal const int F_XSPIN = 12;
    internal const int F_FLUSH_ID = 14;
    internal const int F_PRUP = 13;
    internal const int F_COIN = 1;

    internal static bool IsFeat(int id) => id is F_WHEEL or F_XSPIN or F_PRUP;
}
