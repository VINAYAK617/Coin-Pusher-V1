using CoinPusherEngine;

namespace CoinPusherEngine.VolumeAudit;

internal static class VolumeAuditInputs
{
    internal static MathInput Build(int index, int seed, Random rng)
    {
        var rows = StandardRows();
        if (index % 10 == 0)
            return new LadderCombinator(rows, seed).Bundle(Array.Empty<decimal>()).Input;

        if (index % 25 == 0)
        {
            return new LadderCombinator(rows, seed)
                .Bundle(new decimal[] { 1, 2, 5, 10, 100, 10000 })
                .Input;
        }

        if (index % 7 == 0)
        {
            var prizeUpgradeCount = rng.Next(1, 3);
            return new MathInput
            {
                Targets = new Dictionary<int, int>
                {
                    [1] = 20,
                    [2] = 20,
                    [3] = 20,
                    [4] = 25,
                },
                BaseSpins = 5,
                Required = new Dictionary<string, int>
                {
                    ["EXTRA_SPIN"] = rng.Next(1, 4),
                    ["WHEEL"] = rng.Next(1, 3),
                    ["PRIZE_UPGRADE"] = prizeUpgradeCount,
                },
                PrizeTiers = Enumerable.Range(1, prizeUpgradeCount).ToDictionary(sym => sym, _ => 1),
                PrizeValues = PrizeValues(6, tiers: 3),
                MaxSym = 7,
            };
        }

        var knownBundles = new[]
        {
            new decimal[] { 1 },
            new decimal[] { 2 },
            new decimal[] { 5 },
            new decimal[] { 10 },
            new decimal[] { 100 },
            new decimal[] { 1, 2, 5 },
            new decimal[] { 1, 2, 5, 10 },
            new decimal[] { 1, 2, 5, 10, 100, 10000 },
        };
        var prizes = knownBundles[rng.Next(knownBundles.Length)];
        return new LadderCombinator(rows, seed).Bundle(prizes).Input;
    }

    private static Dictionary<int, IReadOnlyDictionary<int, decimal>> PrizeValues(int maxSym, int tiers)
    {
        var values = new Dictionary<int, IReadOnlyDictionary<int, decimal>>();
        for (var sym = 1; sym <= maxSym; sym++)
        {
            values[sym] = Enumerable.Range(0, tiers)
                .ToDictionary(tier => tier, tier => (decimal)((sym * 10) + tier));
        }

        return values;
    }

    private static IReadOnlyList<PrizeLadderRow> StandardRows() =>
        new[]
        {
            new PrizeLadderRow { Target = 20, Tiers = new decimal[] { 1, 2, 5 } },
            new PrizeLadderRow { Target = 20, Tiers = new decimal[] { 2, 5, 10 } },
            new PrizeLadderRow { Target = 20, Tiers = new decimal[] { 5, 10, 25 } },
            new PrizeLadderRow { Target = 25, Tiers = new decimal[] { 10, 25, 100 } },
            new PrizeLadderRow { Target = 25, Tiers = new decimal[] { 100, 250, 1000 } },
            new PrizeLadderRow { Target = 30, Tiers = new decimal[] { 10000 } },
        };
}
