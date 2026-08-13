namespace CoinPusherEngine;

public static class AlwMoneyMachinePps
{
    public static GameEngine.DefaultCoinPusherSettings CreateSettings() =>
        new()
        {
            MAX_SPINS = 8,
            PrizeLadderRows = AlwMoneyMachinePps.PrizeLadderRows,
            PpsCombinations = AlwMoneyMachinePps.Combinations,
            PpsSpinRules = AlwMoneyMachinePps.SpinRules,
            ExtraSpinFeatureConfig = (0.20, 3, 1, 97, 3),
            PrizeUpgradeFeatureConfig = (0.15, 6, 1, 97, 4),
        };

    public static IReadOnlyList<PrizeLadderRow> PrizeLadderRows { get; } =
        new[]
        {
            new PrizeLadderRow { Target = 20, Tiers = new decimal[] { 3, 5, 10 } },
            new PrizeLadderRow { Target = 20, Tiers = new decimal[] { 5, 10, 20 } },
            new PrizeLadderRow { Target = 20, Tiers = new decimal[] { 10, 20, 50 } },
            new PrizeLadderRow { Target = 25, Tiers = new decimal[] { 50, 100, 250 } },
            new PrizeLadderRow { Target = 25, Tiers = new decimal[] { 500, 1000, 5000 } },
            new PrizeLadderRow { Target = 30, Tiers = new decimal[] { 250000 } },
        };

    public static IReadOnlyList<PpsSpinRule> SpinRules { get; } =
        new[]
        {
            R(0, 1, 0, 1, null, null),
            R(1, 3, 0, 1, 4, 6),
            R(3, 5, 1, 2, 6, 7),
            R(5, 10, 1, 2, 6, 7),
            R(10, 50, 2, 3, 7, 8),
            R(50, null, 3, 3, 8, 8),
        };

    public static IReadOnlyList<PpsPrizeCombination> Combinations { get; } =
        new[]
        {
            C(1, 250000m, P(6, 0)),
            C(2, 5000m, P(5, 2)),
            C(3, 1000m, P(5, 1)),
            C(4, 500m, P(5, 0)),
            C(5, 250m, P(4, 2)),
            C(6, 150m, P(1, 2), P(3, 1), P(2, 2), P(4, 1)),
            C(7, 150m, P(3, 2), P(4, 1)),
            C(8, 120m, P(2, 0), P(1, 1), P(3, 0), P(4, 1)),
            C(9, 120m, P(3, 0), P(2, 1), P(4, 1)),
            C(10, 115m, P(1, 1), P(3, 0), P(4, 1)),
            C(11, 100m, P(1, 2), P(3, 1), P(2, 2), P(4, 0)),
            C(12, 100m, P(4, 0), P(3, 2)),
            C(13, 90m, P(3, 0), P(1, 2), P(2, 2), P(4, 0)),
            C(14, 90m, P(3, 1), P(2, 2), P(4, 0)),
            C(15, 85m, P(2, 0), P(1, 2), P(3, 1), P(4, 0)),
            C(16, 85m, P(1, 1), P(2, 1), P(3, 1), P(4, 0)),
            C(17, 80m, P(3, 0), P(2, 1), P(1, 2), P(4, 0)),
            C(18, 80m, P(3, 0), P(2, 2), P(4, 0)),
            C(19, 75m, P(2, 0), P(3, 0), P(1, 2), P(4, 0)),
            C(20, 75m, P(2, 0), P(3, 1), P(4, 0)),
            C(21, 75m, P(1, 1), P(2, 2), P(4, 0)),
            C(22, 70m, P(3, 0), P(1, 2), P(4, 0)),
            C(23, 70m, P(3, 1), P(4, 0)),
            C(24, 68m, P(1, 0), P(2, 0), P(3, 0), P(4, 0)),
            C(25, 65m, P(2, 0), P(3, 0), P(4, 0)),
            C(26, 65m, P(1, 1), P(3, 0), P(4, 0)),
            C(27, 63m, P(1, 0), P(3, 0), P(4, 0)),
            C(28, 63m, P(1, 0), P(2, 1), P(4, 0)),
            C(29, 60m, P(2, 0), P(1, 1), P(4, 0)),
            C(30, 60m, P(2, 0), P(1, 1), P(3, 2)),
            C(31, 60m, P(3, 0), P(4, 0)),
            C(32, 58m, P(1, 0), P(2, 0), P(4, 0)),
            C(33, 55m, P(2, 0), P(4, 0)),
            C(34, 55m, P(1, 1), P(4, 0)),
            C(35, 53m, P(1, 0), P(4, 0)),
            C(36, 53m, P(1, 0), P(3, 2)),
            C(37, 50m, P(1, 2), P(3, 1), P(2, 2)),
            C(38, 50m, P(3, 2)),
            C(39, 50m, P(4, 0)),
            C(40, 45m, P(1, 1), P(3, 1), P(2, 2)),
            C(41, 43m, P(1, 0), P(3, 1), P(2, 2)),
            C(42, 40m, P(3, 0), P(1, 2), P(2, 2)),
            C(43, 40m, P(3, 1), P(2, 2)),
            C(44, 35m, P(2, 0), P(1, 2), P(3, 1)),
            C(45, 35m, P(1, 1), P(3, 0), P(2, 2)),
            C(46, 33m, P(1, 0), P(3, 0), P(2, 2)),
            C(47, 33m, P(1, 0), P(2, 1), P(3, 1)),
            C(48, 30m, P(2, 0), P(1, 1), P(3, 1)),
            C(49, 30m, P(3, 0), P(2, 2)),
            C(50, 30m, P(1, 2), P(3, 1)),
            C(51, 28m, P(1, 0), P(2, 0), P(3, 1)),
            C(52, 25m, P(2, 0), P(3, 0), P(1, 2)),
            C(53, 25m, P(1, 1), P(3, 0), P(2, 1)),
            C(54, 25m, P(2, 0), P(3, 1)),
            C(55, 25m, P(1, 1), P(2, 2)),
            C(56, 23m, P(1, 0), P(3, 0), P(2, 1)),
            C(57, 23m, P(1, 0), P(3, 1)),
            C(58, 20m, P(2, 0), P(1, 1), P(3, 0)),
            C(59, 20m, P(3, 0), P(1, 2)),
            C(60, 20m, P(3, 1)),
            C(61, 20m, P(2, 2)),
            C(62, 18m, P(1, 0), P(2, 0), P(3, 0)),
            C(63, 15m, P(2, 0), P(3, 0)),
            C(64, 15m, P(2, 0), P(1, 2)),
            C(65, 15m, P(1, 1), P(3, 0)),
            C(66, 15m, P(1, 1), P(2, 1)),
            C(67, 13m, P(1, 0), P(3, 0)),
            C(68, 13m, P(1, 0), P(2, 1)),
            C(69, 10m, P(2, 0), P(1, 1)),
            C(70, 10m, P(2, 1)),
            C(71, 10m, P(1, 2)),
            C(72, 10m, P(3, 0)),
            C(73, 8m, P(1, 0), P(2, 0)),
            C(74, 5m, P(1, 1)),
            C(75, 5m, P(2, 0)),
            C(76, 3m, P(1, 0)),
        };

    private static PpsPrizeComponent P(int symbolId, int tier) =>
        new() { SymbolId = symbolId, Tier = tier };

    private static PpsPrizeCombination C(
        int id,
        decimal totalPrize,
        params PpsPrizeComponent[] components) =>
        new()
        {
            Id = id,
            TotalPrize = totalPrize,
            Components = components,
        };

    private static PpsSpinRule R(
        decimal minWinInclusive,
        decimal? maxWinExclusive,
        int minExtraGo,
        int maxExtraGo,
        int? minWinningTurn,
        int? maxWinningTurn) =>
        new()
        {
            MinWinInclusive = minWinInclusive,
            MaxWinExclusive = maxWinExclusive,
            MinExtraGo = minExtraGo,
            MaxExtraGo = maxExtraGo,
            MinWinningTurn = minWinningTurn,
            MaxWinningTurn = maxWinningTurn,
        };
}
