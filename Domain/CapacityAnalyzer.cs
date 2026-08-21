namespace CoinPusherEngine;

internal static class CapacityAnalyzer
{
    private const int MaxPublicWheelStackValue = 3;

    internal static int TotalCapacity(
        int totalSpins,
        int flushTokens,
        int wheelFireSpins,
        ICustomProfileSettings? settings = null)
    {
        settings ??= Settings;
        _ = wheelFireSpins;
        var normalSpinCapacity = settings.MixedPushCapacity(settings.COLS);
        var baseCapacity = totalSpins * normalSpinCapacity;
        var flushSpinCapacity = settings.ROWS + settings.MixedPushCapacity(settings.COLS - 1);
        var flushBonus = flushTokens * (flushSpinCapacity - normalSpinCapacity);
        return baseCapacity + flushBonus;
    }

    internal static int FillerBudget(
        int physWins,
        int totalSpins,
        int tokenLoad,
        int flushTokens = 0,
        int wheelFireSpins = 0,
        ICustomProfileSettings? settings = null) =>
        TotalCapacity(totalSpins, flushTokens, wheelFireSpins, settings) - physWins - tokenLoad;

    internal static bool IsFeasible(
        int physWins,
        int totalSpins,
        int fillSymCount,
        int tokenLoad,
        int flushTokens = 0,
        int wheelFireSpins = 0,
        ICustomProfileSettings? settings = null)
    {
        settings ??= Settings;
        var budget = FillerBudget(physWins, totalSpins, tokenLoad, flushTokens, wheelFireSpins, settings);
        var maxFiller = fillSymCount * (settings.FILL_CAP - 2);
        return budget >= 0 && budget <= maxFiller;
    }

    internal static bool IsFeasible(
        int physWins,
        int totalSpins,
        IReadOnlyList<int> fillSymbols,
        int tokenLoad,
        int flushTokens = 0,
        int wheelFireSpins = 0,
        ICustomProfileSettings? settings = null)
    {
        settings ??= Settings;
        var budget = FillerBudget(physWins, totalSpins, tokenLoad, flushTokens, wheelFireSpins, settings);
        var maxFiller = fillSymbols.Sum(sym => settings.SymbolFillCap(sym) - 2);
        return budget >= 0 && budget <= maxFiller;
    }

    internal static int MinExtraSpins(
        int physWins,
        int fillSymCount,
        int tokenLoad = 0,
        int flushTokens = 0,
        int wheelFireSpins = 0,
        ICustomProfileSettings? settings = null)
    {
        settings ??= Settings;
        var maxExtras = settings.MAX_SPINS - settings.BASE_SPINS;
        for (var extras = 0; extras <= maxExtras; extras++)
        {
            if (IsFeasible(
                    physWins,
                    settings.BASE_SPINS + extras,
                    fillSymCount,
                    tokenLoad + extras,
                    flushTokens,
                    wheelFireSpins,
                    settings))
            {
                return extras;
            }
        }

        return -1;
    }

    internal static int PhysicalWins(
        IReadOnlyDictionary<int, int> targets,
        int wheelCount,
        ICustomProfileSettings? settings = null)
    {
        settings ??= Settings;
        var ordered = targets.OrderByDescending(kv => kv.Value).ToArray();
        var total = 0;
        for (var i = 0; i < ordered.Length; i++)
        {
            var target = ordered[i].Value;
            if (i < wheelCount)
            {
                var stack = StackFromValue(BestStackValue(target, settings));
                var zone = CollectibleZone(target, stack, settings);
                total += zone + Math.Max(0, target - zone * stack);
            }
            else
            {
                total += target;
            }
        }

        return total;
    }

    private static int BestStackValue(int target, ICustomProfileSettings settings)
    {
        var bestValue = settings.MIN_WHEEL_STACK_VALUE;
        var bestPost = 0;
        var bestZone = 0;
        foreach (var value in ValidStackValues(target, settings))
        {
            var stack = StackFromValue(value);
            var zone = CollectibleZone(target, stack, settings);
            var post = zone * stack;
            if (post > bestPost || post == bestPost && zone > bestZone)
            {
                bestValue = value;
                bestPost = post;
                bestZone = zone;
            }
        }

        return bestPost > 0 ? bestValue : settings.MAX_WHEEL_STACK_VALUE;
    }

    private static IEnumerable<int> ValidStackValues(int target, ICustomProfileSettings settings)
    {
        var maxValue = Math.Min(
            Math.Min(settings.MAX_WHEEL_STACK_VALUE, settings.MAX_COIN_STACK - 1),
            MaxPublicWheelStackValue);
        for (var value = settings.MIN_WHEEL_STACK_VALUE; value <= maxValue; value++)
        {
            var stack = StackFromValue(value);
            var zone = CollectibleZone(target, stack, settings);
            var post = zone * stack;
            if (zone >= 1 && post <= target)
                yield return value;
        }
    }

    private static int StackFromValue(int wheelStackValue) => wheelStackValue + 1;

    private static int Zone(int target, int stack, ICustomProfileSettings settings) =>
        Math.Min(target / stack, settings.COLS - 1);

    private static int CollectibleZone(int target, int stack, ICustomProfileSettings settings) =>
        Math.Max(0, Zone(target, stack, settings) - 1);
}
