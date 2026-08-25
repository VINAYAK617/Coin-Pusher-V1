namespace CoinPusherEngine;

internal static class WMath
{
    private const int MaxPublicWheelStackValue = 3;

    // Best public WheelStackValue that maximizes safe WHEEL contribution
    // without exceeding the target. Public value N means collected stack 1 + N.
    internal static int BestStackValue(int target)
    {
        int bestValue = Settings.MIN_WHEEL_STACK_VALUE, bestPost = 0, bestZone = 0;
        foreach (var value in ValidStackValues(target))
        {
            int stack = StackFromValue(value);
            int zone  = CollectibleZone(target, stack);
            int post = zone * stack;
            if (post > bestPost || (post == bestPost && zone > bestZone))
            {
                bestValue = value;
                bestPost = post;
                bestZone = zone;
            }
        }
        return bestPost > 0 ? bestValue : Settings.MAX_WHEEL_STACK_VALUE;
    }

    internal static int BestN(int target) => BestStackValue(target);

    internal static IEnumerable<int> ValidStackValues(int target)
    {
        var maxValue = Math.Min(Math.Min(Settings.MAX_WHEEL_STACK_VALUE, Settings.MAX_COIN_STACK - 1), MaxPublicWheelStackValue);
        for (int value = Settings.MIN_WHEEL_STACK_VALUE; value <= maxValue; value++)
        {
            int stack = StackFromValue(value);
            int zone = CollectibleZone(target, stack);
            int post = zone * stack;
            if (zone >= 1 && post <= target)
            {
                yield return value;
            }
        }
    }

    internal static int StackFromValue(int wheelStackValue) => wheelStackValue + 1;

    internal static int Zone(int target, int stack) =>
        Math.Min(target / stack, Settings.COLS - 1);

    internal static int CollectibleZone(int target, int stack) =>
        Math.Max(0, Zone(target, stack) - 1);

    internal static WLock MakeLock(int sym, int target, int fireSpin, int n)
    {
        int stack = StackFromValue(n);
        int zone  = Zone(target, stack);
        int post  = CollectibleZone(target, stack) * stack;
        return new WLock { Sym=sym, FireSpin=fireSpin, Stack=stack,
                           Zone=zone, Pre=Math.Max(0, target-post), Post=post };
    }

    internal static (WLock lk1, WLock lk2) MakeMultiLock(
        int sym, int total, int spin1, int n1, int spin2, int n2, int t1)
    {
        int t2 = total - t1;
        int s1=StackFromValue(n1), z1=Zone(t1,s1);
        int s2=StackFromValue(n2), z2=Zone(t2,s2);
        int p1=CollectibleZone(t1,s1)*s1;
        int p2=CollectibleZone(t2,s2)*s2;
        var lk1 = new WLock { Sym=sym, FireSpin=spin1, Stack=s1, Zone=z1,
                               Pre=Math.Max(0,t1-p1), Post=p1 };
        var lk2 = new WLock { Sym=sym, FireSpin=spin2, Stack=s2, Zone=z2, Pre=0, Post=p2 };
        return (lk1, lk2);
    }

    // EDF feasibility: can newPre pre-wins fit in spins 1..newSpin-1?
    internal static bool EdfOk(int newPre, int newSpin,
        IEnumerable<PlacedFeat> existing,
        IReadOnlyDictionary<int, int> targets,
        bool isMulti)
    {
        var tasks  = new List<(int demand, int deadline)>();
        var wSpins = new HashSet<int> { newSpin };

        if (!isMulti && newPre > 0)
            tasks.Add((newPre, newSpin - 1));

        foreach (var f in existing.Where(f => f.Id == "WHEEL" && f.WSym != 0))
        {
            wSpins.Add(f.Spin);
            int t   = targets.GetValueOrDefault(f.WSym, 0);
            int st  = StackFromValue(f.WN);
            int z   = CollectibleZone(t, st);
            int pre = Math.Max(0, t - z * st);
            if (pre > 0) tasks.Add((pre, f.Spin - 1));
        }

        if (tasks.Any(t => t.demand > 0 && t.deadline <= 0))
            return false;

        tasks.Sort((a, b) => a.deadline.CompareTo(b.deadline));
        int cap=0, dem=0, ti=0;
        int maxDl = tasks.Count > 0 ? tasks.Max(x => x.deadline) : 0;
        for (int d = 1; d <= maxDl; d++)
        {
            cap += wSpins.Contains(d)
                ? Settings.COLS * Settings.MIN_PUSH
                : Settings.MixedPushCapacity(Settings.COLS);
            while (ti < tasks.Count && tasks[ti].deadline <= d) dem += tasks[ti++].demand;
            if (dem > cap) return false;
        }
        return true;
    }
}
