namespace CoinPusherEngine;

public sealed class DefaultProfileSettings : ICustomProfileSettings
{
    private static readonly string[] FeatureIds =
    {
        "WHEEL",
        "FLUSH",
        "EXTRA_SPIN",
        "PRIZE_UPGRADE",
    };

    public static DefaultProfileSettings From(ICustomProfileSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        if (settings is DefaultProfileSettings concrete) return concrete;

        return new DefaultProfileSettings
        {
            ROWS = settings.ROWS,
            COLS = settings.COLS,
            MIN_PUSH = settings.MIN_PUSH,
            MAX_PUSH = settings.MAX_PUSH,
            FILL_CAP = settings.FILL_CAP,
            BASE_SPINS = settings.BASE_SPINS,
            MAX_SPINS = settings.MAX_SPINS,
            MAX_COIN_STACK = settings.MAX_COIN_STACK,
            MIN_WHEEL_STACK_VALUE = settings.MIN_WHEEL_STACK_VALUE,
            MAX_WHEEL_STACK_VALUE = settings.MAX_WHEEL_STACK_VALUE,
            NONWIN_MIN_TARGET = settings.NONWIN_MIN_TARGET,
            F_WHEEL = settings.F_WHEEL,
            F_XSPIN = settings.F_XSPIN,
            F_FLUSH_ID = settings.F_FLUSH_ID,
            F_PRUP = settings.F_PRUP,
            F_COIN = settings.F_COIN,
            MaxPlanAttempts = settings.MaxPlanAttempts,
            LocalRealizationAttempts = settings.LocalRealizationAttempts,
            WheelFeatureConfig = settings.WheelFeatureConfig,
            FlushFeatureConfig = settings.FlushFeatureConfig,
            ExtraSpinFeatureConfig = settings.ExtraSpinFeatureConfig,
            PrizeUpgradeFeatureConfig = settings.PrizeUpgradeFeatureConfig,
            PrizeLadderRows = settings.PrizeLadderRows,
            WinningRoundRules = settings.WinningRoundRules,
            PWheelStackValue1 = settings.PWheelStackValue1,
            PWheelStackValue2 = settings.PWheelStackValue2,
            PWheelRepeatOptional = settings.PWheelRepeatOptional,
            PWheelStackCollection = settings.PWheelStackCollection,
            PWheelPreferDenseTarget = settings.PWheelPreferDenseTarget,
            WExpBalanced = settings.WExpBalanced,
            WExpNearMiss = settings.WExpNearMiss,
            WExpFeature = settings.WExpFeature,
            WExpStack = settings.WExpStack,
            WExpLateWin = settings.WExpLateWin,
            POptionalFeatureTicket = settings.POptionalFeatureTicket,
            POptionalTicketWheel = settings.POptionalTicketWheel,
            POptionalTicketFlush = settings.POptionalTicketFlush,
            POptionalTicketPrizeUpgrade = settings.POptionalTicketPrizeUpgrade,
            PNoWinExtraGoOptional = settings.PNoWinExtraGoOptional,
            PWheelOptional = settings.PWheelOptional,
            PFlushOptional = settings.PFlushOptional,
            PNonWinWheel = settings.PNonWinWheel,
            PNonWinPrizeUpgrade = settings.PNonWinPrizeUpgrade,
            PWinLateCompletion = settings.PWinLateCompletion,
            WinLateTailSpins = settings.WinLateTailSpins,
            WinLateMinTail = settings.WinLateMinTail,
            MaxDeferredCollectionsPerTurn = settings.MaxDeferredCollectionsPerTurn,
            WinLateTailFraction = settings.WinLateTailFraction,
            NonWinTargetProfiles = settings.NonWinTargetProfiles,
            NonWinCountWeights = settings.NonWinCountWeights,
            WNonWinLow = settings.WNonWinLow,
            WNonWinMid = settings.WNonWinMid,
            WNonWinHigh = settings.WNonWinHigh,
            PFeatureRetriggerChain = settings.PFeatureRetriggerChain,
            PFeatureLatePlacement = settings.PFeatureLatePlacement,
            PFeatureSameTurn = settings.PFeatureSameTurn,
            WPusherLowPop = settings.WPusherLowPop,
            WPusherMidPop = settings.WPusherMidPop,
            WPusherHighPop = settings.WPusherHighPop,
            FeatureRetriggerBridgeIds = settings.FeatureRetriggerBridgeIds,
        };
    }

    public int ROWS { get; init; } = 5;
    public int COLS { get; init; } = 5;
    public int MIN_PUSH { get; init; } = 1;
    public int MAX_PUSH { get; init; } = 4;
    public int FILL_CAP { get; init; } = 20;

    public int BASE_SPINS { get; init; } = 5;
    public int MAX_SPINS { get; init; } = 10;
    public int MAX_COIN_STACK { get; init; } = 7;
    public int MAX_EXTRA_GO_PER_TURN => MAX_SPINS - BASE_SPINS;
    public int MIN_WHEEL_STACK_VALUE { get; init; } = 1;
    public int MAX_WHEEL_STACK_VALUE { get; init; } = 3;
    public int NONWIN_MIN_TARGET { get; init; } = 1;

    public int F_WHEEL { get; init; } = 11;
    public int F_XSPIN { get; init; } = 12;
    public int F_FLUSH_ID { get; init; } = 14;
    public int F_PRUP { get; init; } = 13;
    public int F_COIN { get; init; } = 1;

    public int MaxPlanAttempts { get; init; } = Int("COINPUSHER_MAX_PLAN_ATTEMPTS", 64, 1);
    public int LocalRealizationAttempts { get; init; } = Int("COINPUSHER_LOCAL_REALIZATION_ATTEMPTS", 32, 1);

    public (double P, int Max, int MinS, int MaxS, int Ord) WheelFeatureConfig { get; init; } =
        (
            Probability("COINPUSHER_FEATURE_WHEEL_P", 0.40),
            Int("COINPUSHER_FEATURE_WHEEL_MAX", 3, 0),
            Int("COINPUSHER_FEATURE_WHEEL_MIN_SPIN", 1, 0),
            Int("COINPUSHER_FEATURE_WHEEL_MAX_SPIN", 98, 1),
            Int("COINPUSHER_FEATURE_WHEEL_ORDER", 1, 0)
        );

    public (double P, int Max, int MinS, int MaxS, int Ord) FlushFeatureConfig { get; init; } =
        (
            Probability("COINPUSHER_FEATURE_FLUSH_P", 0.30),
            Int("COINPUSHER_FEATURE_FLUSH_MAX", 5, 0),
            Int("COINPUSHER_FEATURE_FLUSH_MIN_SPIN", 1, 0),
            Int("COINPUSHER_FEATURE_FLUSH_MAX_SPIN", 99, 1),
            Int("COINPUSHER_FEATURE_FLUSH_ORDER", 2, 0)
        );

    public (double P, int Max, int MinS, int MaxS, int Ord) ExtraSpinFeatureConfig { get; init; } =
        (
            Probability("COINPUSHER_FEATURE_EXTRA_SPIN_P", 0.20),
            Int("COINPUSHER_FEATURE_EXTRA_SPIN_MAX", 5, 0),
            Int("COINPUSHER_FEATURE_EXTRA_SPIN_MIN_SPIN", 1, 0),
            Int("COINPUSHER_FEATURE_EXTRA_SPIN_MAX_SPIN", 97, 1),
            Int("COINPUSHER_FEATURE_EXTRA_SPIN_ORDER", 3, 0)
        );

    public (double P, int Max, int MinS, int MaxS, int Ord) PrizeUpgradeFeatureConfig { get; init; } =
        (
            Probability("COINPUSHER_FEATURE_PRIZE_UPGRADE_P", 0.15),
            Int("COINPUSHER_FEATURE_PRIZE_UPGRADE_MAX", 4, 0),
            Int("COINPUSHER_FEATURE_PRIZE_UPGRADE_MIN_SPIN", 1, 0),
            Int("COINPUSHER_FEATURE_PRIZE_UPGRADE_MAX_SPIN", 97, 1),
            Int("COINPUSHER_FEATURE_PRIZE_UPGRADE_ORDER", 4, 0)
        );

    public IReadOnlyList<PrizeLadderRow> PrizeLadderRows { get; init; } =
        new[]
        {
            new PrizeLadderRow { Target = 20, Tiers = new decimal[] { 1, 2, 5 } },
            new PrizeLadderRow { Target = 20, Tiers = new decimal[] { 2, 4, 8 } },
            new PrizeLadderRow { Target = 20, Tiers = new decimal[] { 5, 10, 25 } },
            new PrizeLadderRow { Target = 25, Tiers = new decimal[] { 10, 20, 50 } },
            new PrizeLadderRow { Target = 25, Tiers = new decimal[] { 100, 200, 500 } },
            new PrizeLadderRow { Target = 30, Tiers = new decimal[] { 10000 } },
        };

    public IReadOnlyList<WinningRoundRule> WinningRoundRules { get; init; } =
        new[]
        {
            new WinningRoundRule
            {
                MinWinInclusive = 0m,
                MaxWinExclusive = 1m,
                ExtraGoCounts = new[] { 0, 1 },
            },
            new WinningRoundRule
            {
                MinWinInclusive = 1m,
                MaxWinExclusive = 3m,
                ExtraGoCounts = new[] { 0, 1 },
                MinWinningTurn = 4,
                MaxWinningTurn = 6,
            },
            new WinningRoundRule
            {
                MinWinInclusive = 3m,
                MaxWinExclusive = 5m,
                ExtraGoCounts = new[] { 1, 2 },
                MinWinningTurn = 6,
                MaxWinningTurn = 7,
            },
            new WinningRoundRule
            {
                MinWinInclusive = 5m,
                MaxWinExclusive = 10m,
                ExtraGoCounts = new[] { 1, 2 },
                MinWinningTurn = 6,
                MaxWinningTurn = 7,
            },
            new WinningRoundRule
            {
                MinWinInclusive = 10m,
                MaxWinExclusive = 50m,
                ExtraGoCounts = new[] { 2, 3 },
                MinWinningTurn = 7,
                MaxWinningTurn = 8,
            },
            new WinningRoundRule
            {
                MinWinInclusive = 50m,
                MaxWinExclusive = null,
                ExtraGoCounts = new[] { 3 },
                MinWinningTurn = 8,
                MaxWinningTurn = 8,
            },
        };

    public double PWheelStackValue1 { get; init; } = Probability("COINPUSHER_P_WHEEL_STACK_VALUE_1", 0.45);
    public double PWheelStackValue2 { get; init; } = Probability("COINPUSHER_P_WHEEL_STACK_VALUE_2", 0.35);
    public double PWheelRepeatOptional { get; init; } = Probability("COINPUSHER_P_WHEEL_REPEAT_OPTIONAL", 0.35);
    public double PWheelStackCollection { get; init; } = Probability("COINPUSHER_P_WHEEL_STACK_COLLECTION", 0.75);
    public double PWheelPreferDenseTarget { get; init; } = Probability("COINPUSHER_P_WHEEL_PREFER_DENSE_TARGET", 0.75);

    public double WExpBalanced { get; init; } = Weight("COINPUSHER_W_EXP_BALANCED", 0.30);
    public double WExpNearMiss { get; init; } = Weight("COINPUSHER_W_EXP_NEARMISS", 0.25);
    public double WExpFeature { get; init; } = Weight("COINPUSHER_W_EXP_FEATURE", 0.20);
    public double WExpStack { get; init; } = Weight("COINPUSHER_W_EXP_STACK", 0.15);
    public double WExpLateWin { get; init; } = Weight("COINPUSHER_W_EXP_LATEWIN", 0.10);

    public double POptionalFeatureTicket { get; init; } = Probability("COINPUSHER_P_OPTIONAL_FEATURE_TICKET", 1.0 / 30.0);
    public double POptionalTicketWheel { get; init; } = Probability("COINPUSHER_P_OPTIONAL_TICKET_WHEEL", 1.0 / 3.0);
    public double POptionalTicketFlush { get; init; } = Probability("COINPUSHER_P_OPTIONAL_TICKET_FLUSH", 1.0 / 3.0);
    public double POptionalTicketPrizeUpgrade { get; init; } = Probability("COINPUSHER_P_OPTIONAL_TICKET_PRIZE_UPGRADE", 1.0 / 4.0);
    public double PNoWinExtraGoOptional { get; init; } = Probability("COINPUSHER_P_NOWIN_EXTRA_GO_OPTIONAL", 0.13);

    public double PWheelOptional { get; init; } = Probability("COINPUSHER_P_WHEEL_OPTIONAL", 1.0);
    public double PFlushOptional { get; init; } = Probability("COINPUSHER_P_FLUSH_OPTIONAL", 0.40);
    public double PNonWinWheel { get; init; } = Probability("COINPUSHER_P_NONWIN_WHEEL", 0.18);
    public double PNonWinPrizeUpgrade { get; init; } = Probability("COINPUSHER_P_NONWIN_PRIZE_UPGRADE", 1.0);

    public double PWinLateCompletion { get; init; } = Probability("COINPUSHER_P_WIN_LATE_COMPLETION", 0.90);
    public int WinLateTailSpins { get; init; } = 2;
    public int WinLateMinTail { get; init; } = 2;
    public int MaxDeferredCollectionsPerTurn { get; init; } = 1;
    public double WinLateTailFraction { get; init; } = 0.20;

    public (double P, int Min, int Max, int MaxSymbols)[] NonWinTargetProfiles { get; init; } =
    {
        (0.20, 1, 5, 5),
        (0.35, 6, 10, 4),
        (0.30, 11, 15, 3),
        (0.15, 16, 24, 2),
    };

    public double[] NonWinCountWeights { get; init; } =
    {
        Weight("COINPUSHER_W_NONWIN_COUNT_1", 0.15),
        Weight("COINPUSHER_W_NONWIN_COUNT_2", 0.25),
        Weight("COINPUSHER_W_NONWIN_COUNT_3", 0.25),
        Weight("COINPUSHER_W_NONWIN_COUNT_4", 0.20),
        Weight("COINPUSHER_W_NONWIN_COUNT_5", 0.15),
    };

    public double WNonWinLow { get; init; } = Weight("COINPUSHER_W_NONWIN_LOW", 0.55);
    public double WNonWinMid { get; init; } = Weight("COINPUSHER_W_NONWIN_MID", 0.40);
    public double WNonWinHigh { get; init; } = Weight("COINPUSHER_W_NONWIN_HIGH", 0.05);

    public double PFeatureRetriggerChain { get; init; } = Probability("COINPUSHER_P_FEATURE_RETRIGGER_CHAIN", 0.25);
    public double PFeatureLatePlacement { get; init; } = Probability("COINPUSHER_P_FEATURE_LATE_PLACEMENT", 0.90);
    public double PFeatureSameTurn { get; init; } = Probability("COINPUSHER_P_FEATURE_SAME_TURN", 0.20);
    public double WPusherLowPop { get; init; } = Weight("COINPUSHER_W_PUSHER_LOW_POP", 0.25);
    public double WPusherMidPop { get; init; } = Weight("COINPUSHER_W_PUSHER_MID_POP", 0.50);
    public double WPusherHighPop { get; init; } = Weight("COINPUSHER_W_PUSHER_HIGH_POP", 0.25);
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

    public int SymbolFillCap(int sym)
    {
        if (sym >= 1 && sym <= PrizeLadderRows.Count)
            return PrizeLadderRows[sym - 1].Target;

        return FILL_CAP;
    }

    public bool IsFeat(int id) => id == F_WHEEL || id == F_XSPIN || id == F_PRUP;

    public IReadOnlyDictionary<string, (double P, int Max, int MinS, int MaxS, int Ord)> FeatureConfigs =>
        FeatureIds.ToDictionary(id => id, FeatureConfig);

    public IEnumerable<string> OrderedFeatureIds =>
        FeatureIds.OrderBy(id => FeatureConfig(id).Ord);

    public (double P, int Max, int MinS, int MaxS, int Ord) FeatureConfig(string id) =>
        id switch
        {
            "WHEEL" => WheelFeatureConfig,
            "FLUSH" => FlushFeatureConfig,
            "EXTRA_SPIN" => ExtraSpinFeatureConfig,
            "PRIZE_UPGRADE" => PrizeUpgradeFeatureConfig,
            _ => throw new ArgumentException($"Unknown feature config '{id}'", nameof(id)),
        };

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

    private static int Int(string envName, int fallback, int min, int max = int.MaxValue)
    {
        var raw = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return int.TryParse(raw, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;
    }
}
