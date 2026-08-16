namespace CoinPusherEngine;

internal static class CapacityAnalyzer
{
    internal static int TotalCapacity(int totalSpins, int flushTokens, int wheelFireSpins)
    {
        _ = wheelFireSpins;
        var normalSpinCapacity = Settings.MixedPushCapacity(Settings.COLS);
        var baseCapacity = totalSpins * normalSpinCapacity;
        var flushSpinCapacity = Settings.ROWS + Settings.MixedPushCapacity(Settings.COLS - 1);
        var flushBonus = flushTokens * (flushSpinCapacity - normalSpinCapacity);
        return baseCapacity + flushBonus;
    }

    internal static int FillerBudget(
        int physWins,
        int totalSpins,
        int tokenLoad,
        int flushTokens = 0,
        int wheelFireSpins = 0) =>
        TotalCapacity(totalSpins, flushTokens, wheelFireSpins) - physWins - tokenLoad;

    internal static bool IsFeasible(
        int physWins,
        int totalSpins,
        int fillSymCount,
        int tokenLoad,
        int flushTokens = 0,
        int wheelFireSpins = 0)
    {
        var budget = FillerBudget(physWins, totalSpins, tokenLoad, flushTokens, wheelFireSpins);
        var maxFiller = fillSymCount * (Settings.FILL_CAP - 2);
        return budget >= 0 && budget <= maxFiller;
    }

    internal static bool IsFeasible(
        int physWins,
        int totalSpins,
        IReadOnlyList<int> fillSymbols,
        int tokenLoad,
        int flushTokens = 0,
        int wheelFireSpins = 0)
    {
        var budget = FillerBudget(physWins, totalSpins, tokenLoad, flushTokens, wheelFireSpins);
        var maxFiller = fillSymbols.Sum(sym => Settings.SymbolFillCap(sym) - 2);
        return budget >= 0 && budget <= maxFiller;
    }

    internal static int MinExtraSpins(
        int physWins,
        int fillSymCount,
        int tokenLoad = 0,
        int flushTokens = 0,
        int wheelFireSpins = 0)
    {
        var maxExtras = Settings.MAX_SPINS - Settings.BASE_SPINS;
        for (var extras = 0; extras <= maxExtras; extras++)
        {
            if (IsFeasible(
                    physWins,
                    Settings.BASE_SPINS + extras,
                    fillSymCount,
                    tokenLoad + extras,
                    flushTokens,
                    wheelFireSpins))
            {
                return extras;
            }
        }

        return -1;
    }

    internal static int PhysicalWins(IReadOnlyDictionary<int, int> targets, int wheelCount)
    {
        var ordered = targets.OrderByDescending(kv => kv.Value).ToArray();
        var total = 0;
        for (var i = 0; i < ordered.Length; i++)
        {
            var target = ordered[i].Value;
            if (i < wheelCount)
            {
                var stack = WMath.StackFromValue(WMath.BestN(target));
                var zone = WMath.CollectibleZone(target, stack);
                total += zone + Math.Max(0, target - zone * stack);
            }
            else
            {
                total += target;
            }
        }

        return total;
    }
}
