namespace CoinPusherEngine;

/// <summary>
/// Distributes per-symbol win counts across spins using Earliest-Deadline-First (EDF).
///
/// ZONE CAPACITY — the key invariant:
/// Cap() allocates at most mixed free-column capacity plus FLUSH capacity, minus reserved
/// token cells. Builder.MakePushers uses the same ceiling when creating varied 1/2/3
/// pusher values, so the scheduled allocation and realized board geometry stay aligned.
///
/// TOKEN RESERVATION:
/// A token firing at spin F needs one filler slot in spin F's Spawns dict, covering
/// positions in spin (F+1)'s zone. So slot index F (0-based, = spin F+1's alloc) has its
/// capacity reduced by 1 per token firing at spin F.
/// </summary>
internal sealed class Scheduler
{
    private readonly IReadOnlyDictionary<int, int> _targets;
    private readonly List<PlacedFeat>              _placed;
    private readonly IReadOnlyList<WLock>          _locks;
    private readonly List<string>                  _log;
    private readonly HashSet<int>                  _winSyms;
    private readonly int                           _finalAnchorSym;

    internal Scheduler(IReadOnlyDictionary<int, int> targets,
                       List<PlacedFeat> placed, IReadOnlyList<WLock> locks, List<string> log,
                       IEnumerable<int>? winSyms = null)
    {
        _targets=targets; _placed=placed; _locks=locks; _log=log;
        _winSyms = winSyms != null ? new HashSet<int>(winSyms) : targets.Keys.ToHashSet();
        _finalAnchorSym = _winSyms.OrderBy(sym => sym).FirstOrDefault();
    }

    internal List<Dictionary<int, int>> Schedule(int totalSpins)
    {
        var tokenReserve = new Dictionary<int, int>();
        foreach (var f in _placed.Where(f => FeatReg.Has(f.Id) && FeatReg.Get(f.Id).HasToken))
            tokenReserve[f.Spin] = tokenReserve.GetValueOrDefault(f.Spin, 0) + 1;
        ResolveWheelCarrySlots(totalSpins, tokenReserve);

        var slots = Enumerable.Range(0, totalSpins).Select(_ => new Dictionary<int, int>()).ToList();

        foreach (var lk in _locks)
        {
            foreach (var (slot, cells) in lk.CarrySlots)
                Add(slots[slot], lk.Sym, cells);
        }

        // EDF: distribute remaining wins, respecting exact zone capacity ceiling
        foreach (var (sym, target) in _targets)
        {
            var myLocks = _locks.Where(lk => lk.Sym == sym).OrderBy(lk => lk.FireSpin).ToList();

            int effectivePost = myLocks.Sum(lk =>
            {
                if (lk.FireSpin >= totalSpins) return 0;
                return lk.CarrySlots.Values.Sum() * lk.Stack;
            });

            int remaining = Math.Max(0, target - effectivePost);
            int lastFireSpin = myLocks.Count > 0 ? myLocks[^1].FireSpin : 0;
            int deadline      = myLocks.Count > 0 ? myLocks[0].FireSpin - 1 : totalSpins;
            var lateSlots = myLocks.Count == 0 && ShouldUseLateCompletion(sym, target, totalSpins)
                ? LateSlots(totalSpins, lastFireSpin).ToHashSet()
                : new HashSet<int>();

            if (lateSlots.Count > 0 && remaining > 0)
            {
                var tailWanted = Math.Min(remaining, LateTailCount(target));
                var tailLeft = FillAcrossSlots(sym, tailWanted, lateSlots, slots, tokenReserve);
                remaining -= tailWanted - tailLeft;
            }

            // Pass 1: fill pre-deadline spins (normal EDF — before the symbol's first WHEEL fires).
            // Spread across the least-loaded eligible spins instead of filling spin 1 to capacity.
            remaining = FillAcrossSlots(sym, remaining,
                Enumerable.Range(0, deadline).Where(s => !lateSlots.Contains(s)),
                slots, tokenReserve);

            // Pass 2: post-deadline fallback. The symbol can still be collected normally
            // (unstacked) in spins after its WHEEL(s) fire — scan forward from just after
            // the last WHEEL's own zone slot to the second-to-last spin. Skips:
            //   • slot[deadline]..slot[lastFireSpin]: the WHEEL fire spin(s) and their own
            //     zone slots (already populated by the lock's Add call above; IsolateWheelSyms
            //     in Builder also actively clears this symbol from those zone positions).
            if (remaining > 0)
            {
                var fallbackCandidates = myLocks.Count > 0
                    ? Enumerable.Range(lastFireSpin + 1, Math.Max(0, totalSpins - (lastFireSpin + 1)))
                    : Enumerable.Range(0, totalSpins);
                remaining = FillAcrossSlots(sym, remaining,
                    fallbackCandidates,
                    slots, tokenReserve);
            }

            if (remaining > 0)
            {
                _log.Add($"  WARN: {remaining} unplaced allocations sym={sym} -> forced last slot");
                Add(slots[totalSpins - 1], sym, remaining);
            }
        }

        return slots;
    }

    private void ResolveWheelCarrySlots(
        int totalSpins,
        IReadOnlyDictionary<int, int> tokenReserve)
    {
        foreach (var lk in _locks)
        {
            lk.CarrySlots.Clear();
            lk.PermanentResidue = 0;
        }

        var immediate = _locks.ToDictionary(lk => lk, lk => lk.FireSpin < totalSpins ? lk.Zone : 0);

        foreach (var group in _locks.Where(lk => lk.FireSpin < totalSpins).GroupBy(lk => lk.FireSpin))
        {
            var reserveLeft = tokenReserve.GetValueOrDefault(group.Key);
            if (reserveLeft <= 0) continue;

            // Token reservations consume cells from the shared next-spin WHEEL zone.
            // Subtract them once per slot, not once per WHEEL lock.
            foreach (var lk in group.OrderBy(lk => lk.Zone).ThenByDescending(lk => lk.Sym))
            {
                if (reserveLeft <= 0) break;
                var take = Math.Min(immediate[lk], reserveLeft);
                immediate[lk] -= take;
                reserveLeft -= take;
            }
        }

        var used = new Dictionary<int, int>();
        foreach (var lk in _locks.Where(lk => lk.FireSpin < totalSpins)
                                 .OrderBy(lk => lk.FireSpin)
                                 .ThenBy(lk => lk.Sym))
        {
            var cellsLeft = immediate.GetValueOrDefault(lk);
            if (cellsLeft <= 0) continue;

            cellsLeft = AddWheelCarry(lk, lk.FireSpin, cellsLeft, cellsLeft, used, tokenReserve, totalSpins);

            lk.PermanentResidue = lk.FireSpin + 1 < totalSpins ? Math.Min(3, Math.Max(1, lk.Zone)) : 0;
        }
    }

    private int AddWheelCarry(
        WLock lk,
        int slot,
        int wanted,
        int left,
        Dictionary<int, int> used,
        IReadOnlyDictionary<int, int> tokenReserve,
        int totalSpins)
    {
        if (wanted <= 0 || left <= 0 || slot < 0 || slot >= totalSpins) return left;

        var room = Math.Max(0, CapacityCeiling(slot, tokenReserve) - used.GetValueOrDefault(slot));
        var take = Math.Min(Math.Min(wanted, left), room);
        if (take <= 0) return left;

        lk.CarrySlots[slot] = lk.CarrySlots.GetValueOrDefault(slot) + take;
        used[slot] = used.GetValueOrDefault(slot) + take;
        return left - take;
    }

    /// <summary>
    /// Hard ceiling for slot s (= spin s+1's alloc):
    ///   maxZone = mixed free-column capacity + flushCols*ROWS - reserved
    ///   cap     = maxZone - already
    /// WHEEL value is handled by PhysicalWins/stack compression, not by allowing
    /// flat 15-cell raw push screens.
    /// </summary>
    private int Cap(int s, List<Dictionary<int, int>> slots, Dictionary<int, int> tokenReserve)
    {
        int spinNum   = s + 1;
        int flushCols = _placed.Count(f => f.Id == "FLUSH" && f.Spin == spinNum);
        int freeCols  = K.COLS - flushCols;
        int flushCap  = flushCols * K.ROWS;
        int reserved  = tokenReserve.GetValueOrDefault(s, 0);
        int already   = slots[s].Values.Sum();

        int ceiling = CapacityCeiling(s, tokenReserve);

        return Math.Max(0, ceiling - already);
    }

    private int CapacityCeiling(int s, IReadOnlyDictionary<int, int> tokenReserve)
    {
        int spinNum = s + 1;
        int flushCols = _placed.Count(f => f.Id == "FLUSH" && f.Spin == spinNum);
        int freeCols = K.COLS - flushCols;
        int flushCap = flushCols * K.ROWS;
        int reserved = tokenReserve.GetValueOrDefault(s, 0);

        return K.MixedPushCapacity(freeCols) + flushCap - reserved;
    }

    private int FillAcrossSlots(int sym, int remaining, IEnumerable<int> candidates,
                                List<Dictionary<int, int>> slots, Dictionary<int, int> tokenReserve)
    {
        var eligible = candidates.Distinct().Where(s => s >= 0 && s < slots.Count).ToList();
        while (remaining > 0)
        {
            var best = eligible
                .Select(s => (Slot: s, Cap: Cap(s, slots, tokenReserve), Load: slots[s].Values.Sum()))
                .Where(x => x.Cap > 0)
                .OrderBy(x => x.Load)
                .ThenBy(x => SlotTieBreak(sym, x.Slot))
                .FirstOrDefault();

            if (best.Cap <= 0) break;

            Add(slots[best.Slot], sym, 1);
            remaining--;
        }

        return remaining;
    }

    private static void Add(Dictionary<int, int> d, int k, int v)
    {
        if (v <= 0) return;

        d.TryGetValue(k, out int ex);
        d[k] = ex + v;
    }

    private bool ShouldUseLateCompletion(int sym, int target, int totalSpins)
    {
        if (!_winSyms.Contains(sym) || totalSpins <= 2) return false;
        if (sym == _finalAnchorSym) return true;
        return UnitHash(sym, target, totalSpins, _placed.Count) < K.P_WIN_LATE_COMPLETION;
    }

    private static int LateTailCount(int target) =>
        Math.Max(K.WIN_LATE_MIN_TAIL, (int)Math.Ceiling(target * K.WIN_LATE_TAIL_FRACTION));

    private static IEnumerable<int> LateSlots(int totalSpins, int lastFireSpin)
    {
        var firstLate = Math.Max(0, totalSpins - K.WIN_LATE_TAIL_SPINS);
        for (int slot = firstLate; slot < totalSpins; slot++)
        {
            if (slot > lastFireSpin) yield return slot;
        }
    }

    private static int SlotTieBreak(int sym, int slot)
    {
        unchecked
        {
            var hash = 17;
            hash = hash * 31 + sym;
            hash = hash * 31 + slot;
            return hash & 0x7fffffff;
        }
    }

    private static double UnitHash(int a, int b, int c, int d)
    {
        unchecked
        {
            var hash = 23;
            hash = hash * 31 + a;
            hash = hash * 31 + b;
            hash = hash * 31 + c;
            hash = hash * 31 + d;
            return (hash & 0x7fffffff) / (double)int.MaxValue;
        }
    }
}
