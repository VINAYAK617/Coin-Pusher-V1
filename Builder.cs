namespace CoinPusherEngine;

/// <summary>
/// Builds spin boards backwards (spin N → spin 1).
/// Pipeline per spin: RotCCW(next) → UndoPush → IsolateWheelSyms → FillZone → FillRest.
///
/// Token Slot Reservation:
/// A WHEEL token fires at spin F. Its spawn occupies a slot in spin F's Spawns dict.
/// Those spawns are positions null after simulating spin F's push+rotate, which are
/// positions in spin (F+1)'s board zone. So when building spin (F+1), one zone position
/// is reserved as filler (left null after FillZone; FillRest fills it). The Resolver
/// replaces it with the token. The Scheduler already reduced spin (F+1)'s alloc by 1.
///
/// Zone Capacity — exact match with Scheduler.Cap:
/// Scheduler.Cap allocates at most mixed free-column capacity plus FLUSH capacity,
/// minus reserved feature-token cells. Builder then creates varied 1/2/3 push values
/// with the same mixed-cap ceiling, so zone overflow is structurally impossible while
/// still avoiding rigged-looking flat push screens.
/// </summary>
internal sealed class Builder
{
    private readonly IReadOnlyList<WLock>          _locks;
    private readonly List<PlacedFeat>              _placed;
    private readonly List<string>                  _log;
    private readonly Random                        _rng;
    private readonly FillTracker                   _fillTracker;
    private readonly TicketExperienceProfile       _experienceProfile;

    // Decorative budget: how many extra (non-scheduled) occurrences of each win symbol
    // may additionally appear as ordinary board filler. Consumed directly from leftover
    // ZONE capacity in FillZone (see there) — never from arbitrary filler positions —
    // so every decorative placement is collected this same spin, by construction,
    // exactly like any other zone cell. There is no cross-spin survival question and
    // no risk of over- or under-counting.
    private readonly Dictionary<int, int> _decorBudget;
    private readonly Dictionary<int, int> _decorPlaced = new();

    internal Builder(IReadOnlyDictionary<int, int> targets, IReadOnlyList<WLock> locks,
                     List<PlacedFeat> placed, int[] fills, List<string> log, Random rng,
                     FillTracker fillTracker, Dictionary<int, int>? decorBudget = null,
                     TicketExperienceProfile experienceProfile = TicketExperienceProfile.Balanced)
    {
        _locks=locks; _placed=placed; _log=log; _rng=rng;
        _fillTracker=fillTracker;
        _experienceProfile = experienceProfile;
        _decorBudget = decorBudget != null ? new Dictionary<int, int>(decorBudget) : new Dictionary<int, int>();
    }

    internal List<SpinPlan> BuildAll(List<PlacedFeat> placed,
                                      List<Dictionary<int, int>> allocs,
                                      int totalSpins, int baseSpins)
    {
        var plans     = new List<SpinPlan>();
        var nextBoard = new Cell?[K.ROWS, K.COLS];

        for (int s = totalSpins; s >= 1; s--)
        {
            var sf    = placed.Where(f => f.Spin == s).ToList();
            var alloc = s <= allocs.Count ? allocs[s - 1] : new Dictionary<int, int>();

            // Count token reservations needed in THIS spin's zone (tokens firing at spin s-1)
            int reservedCount = placed.Count(p => p.Spin == s - 1
                                               && FeatReg.Has(p.Id)
                                               && FeatReg.Get(p.Id).HasToken);

            var (push, flush) = MakePushers(s, sf, alloc, reservedCount);
            var tokenReserved = TokenReservedPositions(s - 1, push, flush);
            var board = BuildBoard(nextBoard, plans, s, totalSpins, push, flush, alloc, tokenReserved);

            var plan = new SpinPlan
            {
                Spin=s, IsExtra=s>baseSpins,
                Board=board, Push=push, Flush=flush, Alloc=alloc,
            };
            foreach (var f in sf.Where(f => FeatReg.Has(f.Id) && FeatReg.Get(f.Id).HasToken))
                plan.Tokens.Add((f.Id, f.Col, MakeFP(f)));

            plans.Insert(0, plan);
            nextBoard = board;
        }

        return plans;
    }

    /// <summary>How many decorative occurrences were actually placed per symbol (always
    /// &lt;= the requested budget; Planner uses this to know the true final count).</summary>
    internal IReadOnlyDictionary<int, int> DecorativePlaced => _decorPlaced;

    // ── Token reservation ──────────────────────────────────────────────────
    private HashSet<(int r, int c)> TokenReservedPositions(int fireSpin, int[] push, bool[] flush)
    {
        var reserved = new HashSet<(int, int)>();
        var zoneSet  = Grid.ZoneSet(push, flush);

        foreach (var f in _placed.Where(p => p.Spin == fireSpin
                                          && FeatReg.Has(p.Id)
                                          && FeatReg.Get(p.Id).HasToken))
        {
            var preferred = (K.ROWS - 1, f.Col);
            if (zoneSet.Contains(preferred) && !reserved.Contains(preferred))
            { reserved.Add(preferred); continue; }

            bool placed = false;
            for (int r2 = K.ROWS - 1; r2 >= 0 && !placed; r2--)
            {
                var pos = (r2, K.COLS - 1);
                if (zoneSet.Contains(pos) && !reserved.Contains(pos))
                { reserved.Add(pos); placed = true; }
            }
            if (!placed)
            {
                var fallback = zoneSet
                    .Where(p => !reserved.Contains(p))
                    .OrderByDescending(p => p.c)
                    .Cast<(int r, int c)?>()
                    .FirstOrDefault();
                if (fallback.HasValue)
                    reserved.Add(fallback.Value);
            }
        }
        return reserved;
    }

    // ── Board construction ─────────────────────────────────────────────────
    private Cell?[,] BuildBoard(Cell?[,] next, IReadOnlyList<SpinPlan> futurePlans,
                                 int spinNum, int totalSpins,
                                 int[] push, bool[] flush,
                                 IReadOnlyDictionary<int, int> alloc,
                                 HashSet<(int, int)> tokenReserved)
    {
        var board = Grid.RotCCW(next);

        for (int col = 0; col < K.COLS; col++)
        {
            if (flush[col])
            {
                for (int r = 0; r < K.ROWS; r++)
                    board[r, col] = null;
            }
            else
            {
                int p = push[col];
                for (int r = 0; r < K.ROWS; r++)
                    board[r, col] = r + p < K.ROWS ? board[r + p, col]?.Clone() : null;
            }
        }

        var zoneSet = Grid.ZoneSet(push, flush);
        IsolateWheelSyms(board, spinNum, totalSpins, zoneSet);
        var carryStarts = PlanWheelCarryStarts(futurePlans, spinNum, totalSpins, push, flush);
        FillZone(board, spinNum, push, flush, alloc, tokenReserved, carryStarts.DelayedBySymbol);
        PlaceWheelCarryStarts(board, carryStarts);
        FillRest(board);
        return board;
    }

    private void IsolateWheelSyms(Cell?[,] board, int spinNum, int totalSpins, HashSet<(int, int)> zoneSet)
    {
        foreach (var lk in _locks)
        {
            int sym = lk.Sym;
            if (lk.FireSpin == spinNum)
            {
                for (int r = 0; r < K.ROWS; r++)
                {
                    for (int c = 0; c < K.COLS; c++)
                    {
                        var cell = board[r, c];
                        if (cell != null && !cell.IsFeat && cell.Sym == sym && zoneSet.Contains((r, c)))
                            board[r, c] = null;
                    }
                }
            }
            else if (lk.FireSpin + 1 == spinNum && spinNum < totalSpins)
            {
                var protectedPositions = WheelCarryStartPositions(spinNum, lk);
                for (int r = 0; r < K.ROWS; r++)
                {
                    for (int c = 0; c < K.COLS; c++)
                    {
                        var cell = board[r, c];
                        if (cell != null && !cell.IsFeat && cell.Sym == sym
                            && !zoneSet.Contains((r, c))
                            && !protectedPositions.Contains((r, c)))
                            board[r, c] = null;
                    }
                }
            }
        }
    }

    private sealed class WheelCarryStartPlan
    {
        internal Dictionary<int, int> DelayedBySymbol { get; } = new();
        internal List<((int r, int c) Pos, int Sym)> Cells { get; } = new();
    }

    private WheelCarryStartPlan PlanWheelCarryStarts(IReadOnlyList<SpinPlan> futurePlans,
                                                     int spinNum, int totalSpins,
                                                     int[] push, bool[] flush)
    {
        var result = new WheelCarryStartPlan();
        var locks = _locks.Where(lk => lk.FireSpin + 1 == spinNum).OrderBy(_ => _rng.Next()).ToList();
        if (locks.Count == 0) return result;

        var occupied = new HashSet<(int, int)>();
        foreach (var lk in locks)
        {
            var immediate = lk.CarrySlots.GetValueOrDefault(lk.FireSpin);
            var delayedWanted = DelayedWheelCarryCount(lk, immediate);
            foreach (var pos in DelayedCarryCandidates(spinNum, totalSpins, push, flush, futurePlans, occupied)
                         .Take(delayedWanted))
            {
                result.Cells.Add((pos, lk.Sym));
                result.DelayedBySymbol[lk.Sym] = result.DelayedBySymbol.GetValueOrDefault(lk.Sym) + 1;
                occupied.Add(pos);
            }

            foreach (var pos in PermanentResidueCandidates(spinNum, totalSpins, push, flush, futurePlans, occupied)
                         .Take(lk.PermanentResidue))
            {
                result.Cells.Add((pos, lk.Sym));
                occupied.Add(pos);
            }
        }

        return result;
    }

    private int DelayedWheelCarryCount(WLock lk, int immediate)
    {
        if (_locks.Count(other => other.Sym == lk.Sym) > 1)
            return 0;

        return immediate > 1 ? 1 : 0;
    }

    private static void PlaceWheelCarryStarts(Cell?[,] board, WheelCarryStartPlan plan)
    {
        foreach (var (pos, sym) in plan.Cells)
            board[pos.r, pos.c] = Grid.Norm(sym);
    }

    private HashSet<(int r, int c)> WheelCarryStartPositions(int spinNum, WLock lk)
    {
        if (lk.FireSpin + 1 != spinNum) return new HashSet<(int, int)>();

        // Dynamic carry starts are selected later in BuildBoard. Existing back-propagated
        // cells should not be cleared if they are already outside the next collection zone.
        return Enumerable.Range(0, K.ROWS)
            .SelectMany(r => Enumerable.Range(0, K.COLS).Select(c => (r, c)))
            .ToHashSet();
    }

    private IEnumerable<(int r, int c)> CarryStartCandidates(
        int startSpin,
        int collectSpin,
        int[] push,
        bool[] flush,
        IReadOnlyList<SpinPlan> futurePlans,
        HashSet<(int, int)> occupied)
    {
        return Enumerable.Range(0, K.ROWS)
            .SelectMany(r => Enumerable.Range(0, K.COLS).Select(c => (r, c)))
            .Where(pos => !occupied.Contains(pos))
            .Where(pos => CollectsExactlyAt(pos, startSpin, collectSpin, push, flush, futurePlans))
            .OrderBy(_ => _rng.Next());
    }

    private IEnumerable<(int r, int c)> DelayedCarryCandidates(
        int startSpin,
        int totalSpins,
        int[] push,
        bool[] flush,
        IReadOnlyList<SpinPlan> futurePlans,
        HashSet<(int, int)> occupied)
    {
        var positions = Enumerable.Range(0, K.ROWS)
            .SelectMany(r => Enumerable.Range(0, K.COLS).Select(c => (r, c)))
            .Where(pos => !occupied.Contains(pos))
            .OrderBy(_ => _rng.Next())
            .ToList();

        for (var collectSpin = startSpin + 1; collectSpin <= totalSpins; collectSpin++)
        {
            foreach (var pos in positions)
                if (CollectsExactlyAt(pos, startSpin, collectSpin, push, flush, futurePlans))
                    yield return pos;
        }
    }

    private IEnumerable<(int r, int c)> PermanentResidueCandidates(
        int startSpin,
        int totalSpins,
        int[] push,
        bool[] flush,
        IReadOnlyList<SpinPlan> futurePlans,
        HashSet<(int, int)> occupied)
    {
        return Enumerable.Range(0, K.ROWS)
            .SelectMany(r => Enumerable.Range(0, K.COLS).Select(c => (r, c)))
            .Where(pos => !occupied.Contains(pos))
            .Where(pos => !CollectsByEnd(pos, startSpin, totalSpins, push, flush, futurePlans))
            .OrderBy(_ => _rng.Next());
    }

    private static bool CollectsExactlyAt(
        (int r, int c) start,
        int startSpin,
        int collectSpin,
        int[] push,
        bool[] flush,
        IReadOnlyList<SpinPlan> futurePlans)
    {
        var pos = start;
        for (var spin = startSpin; spin <= collectSpin; spin++)
        {
            var (p, f) = spin == startSpin
                ? (push, flush)
                : PlanGeometry(futurePlans, spin);

            var inZone = f[pos.c] || pos.r >= K.ROWS - p[pos.c];
            if (spin == collectSpin) return inZone;
            if (inZone) return false;

            pos = AdvancePosition(pos, p[pos.c]);
            if (pos.r < 0) return false;
        }

        return false;
    }

    private static bool CollectsByEnd(
        (int r, int c) start,
        int startSpin,
        int totalSpins,
        int[] push,
        bool[] flush,
        IReadOnlyList<SpinPlan> futurePlans)
    {
        var pos = start;
        for (var spin = startSpin; spin <= totalSpins; spin++)
        {
            var (p, f) = spin == startSpin
                ? (push, flush)
                : PlanGeometry(futurePlans, spin);

            if (f[pos.c] || pos.r >= K.ROWS - p[pos.c]) return true;
            pos = AdvancePosition(pos, p[pos.c]);
            if (pos.r < 0) return false;
        }

        return false;
    }

    private static (int[] Push, bool[] Flush) PlanGeometry(IReadOnlyList<SpinPlan> plans, int spin)
    {
        var plan = plans.First(p => p.Spin == spin);
        return (plan.Push, plan.Flush);
    }

    private static (int r, int c) AdvancePosition((int r, int c) pos, int push)
    {
        var shiftedRow = pos.r + push;
        if (shiftedRow >= K.ROWS) return (-1, -1);
        return (pos.c, K.ROWS - 1 - shiftedRow);
    }

    private void FillZone(Cell?[,] board, int spinNum, int[] push, bool[] flush,
                           IReadOnlyDictionary<int, int> alloc,
                           HashSet<(int, int)> tokenReserved,
                           IReadOnlyDictionary<int, int> delayedWheelCounts)
    {
        var safe  = new List<(int r, int c)>();
        var spawn = new List<(int r, int c)>();

        for (int col = 0; col < K.COLS; col++)
        {
            IEnumerable<int> rows = flush[col]
                ? Enumerable.Range(0, K.ROWS)
                : Grid.ZoneRows(push[col]);
            foreach (int row in rows)
            {
                var pos = (row, col);
                if (tokenReserved.Contains(pos)) continue;
                (col == K.COLS - 1 ? spawn : safe).Add(pos);
            }
        }
        var positions = safe.Concat(spawn).ToList();

        foreach (var (r, c) in positions) board[r, c] = null;
        foreach (var pos in tokenReserved) board[pos.Item1, pos.Item2] = null;

        var effectiveAlloc = alloc.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        RemoveDelayedWheelCarryAlloc(effectiveAlloc, delayedWheelCounts);

        foreach (var (r, c) in PlaceAllocatedSymbols(board, positions, effectiveAlloc))
        {
            // Leftover zone capacity after the real, scheduled wins are placed — this
            // position is GUARANTEED collected THIS spin (it's inside the zone), so it's
            // the only place a "decorative" win-symbol filler can be placed with zero
            // risk of over- or under-counting: no rotation, no cross-spin survival
            // question, just an ordinary same-spin collection like any other zone cell.
            //
            // EXCEPTION: a symbol with a WHEEL lock fires by scanning the WHOLE board
            // for any cell matching its symbol and stacking it — a decorative cell would
            // get silently caught in that scan too, turning +1 into +stack. So decoration
            // for a symbol is only eligible at spins STRICTLY BEFORE every one of that
            // symbol's WHEEL fire spins: the cell is collected and gone well before the
            // WHEEL ever runs its scan, never coexisting with it.
            if (_decorBudget.Count > 0)
            {
                var eligible = _decorBudget
                    .Where(kv => kv.Value > 0 && IsDecorationEligible(kv.Key, spinNum))
                    .FirstOrDefault();
                if (eligible.Value > 0)
                {
                    _decorBudget[eligible.Key]--;
                    _decorPlaced[eligible.Key] = _decorPlaced.GetValueOrDefault(eligible.Key, 0) + 1;
                    board[r, c] = Grid.Norm(eligible.Key);
                    continue;
                }
            }
        }

    }

    private List<(int r, int c)> PlaceAllocatedSymbols(
        Cell?[,] board,
        IReadOnlyList<(int r, int c)> positions,
        IReadOnlyDictionary<int, int> alloc)
    {
        var remaining = alloc
            .Where(kv => kv.Value > 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        var unfilled = positions.ToList();
        if (remaining.Count == 0) return unfilled;

        var order = positions
            .OrderBy(_ => _rng.Next())
            .ThenBy(pos => pos.r)
            .ThenBy(pos => pos.c)
            .ToList();

        foreach (var pos in order)
        {
            if (remaining.Values.Sum() <= 0) break;

            var chosen = PickLeastPatternedSymbol(board, pos, remaining);
            board[pos.r, pos.c] = Grid.Norm(chosen);
            remaining[chosen]--;
            if (remaining[chosen] == 0) remaining.Remove(chosen);
            unfilled.Remove(pos);
        }

        if (remaining.Count > 0)
        {
            var overflow = remaining.Values.Sum();
            _log.Add($"  WARN: {overflow} zone wins overflow - should be structurally impossible");
        }

        return unfilled;
    }

    private int PickLeastPatternedSymbol(
        Cell?[,] board,
        (int r, int c) pos,
        IReadOnlyDictionary<int, int> remaining)
    {
        return remaining
            .Select(kv => new
            {
                Sym = kv.Key,
                NeighborMatches = AdjacentMatchCount(board, pos, kv.Key),
                ColumnLoad = CountSymbolInColumn(board, pos.c, kv.Key),
                Remaining = kv.Value,
                Jitter = _rng.Next(),
            })
            .OrderBy(x => x.NeighborMatches)
            .ThenBy(x => x.ColumnLoad)
            .ThenByDescending(x => x.Remaining)
            .ThenBy(x => x.Jitter)
            .First().Sym;
    }

    private static int AdjacentMatchCount(Cell?[,] board, (int r, int c) pos, int sym)
    {
        var count = 0;
        if (SameSymbol(board, pos.r, pos.c - 1, sym)) count++;
        if (SameSymbol(board, pos.r, pos.c + 1, sym)) count++;
        if (SameSymbol(board, pos.r - 1, pos.c, sym)) count++;
        if (SameSymbol(board, pos.r + 1, pos.c, sym)) count++;
        return count;
    }

    private static bool SameSymbol(Cell?[,] board, int row, int col, int sym) =>
        row >= 0 && row < K.ROWS
        && col >= 0 && col < K.COLS
        && board[row, col]?.IsFeat != true
        && board[row, col]?.Sym == sym;

    private static int CountSymbolInColumn(Cell?[,] board, int col, int sym)
    {
        var count = 0;
        for (var row = 0; row < K.ROWS; row++)
        {
            var cell = board[row, col];
            if (cell?.IsFeat != true && cell?.Sym == sym) count++;
        }
        return count;
    }

    /// <summary>
    /// A symbol is eligible for decorative placement at a given spin only if EVERY
    /// WHEEL lock for that symbol fires at a STRICTLY LATER spin — i.e. this decoration
    /// is guaranteed collected and gone before any WHEEL scan for this symbol ever runs,
    /// so it can never be caught by that scan's stacking. Symbols with no WHEEL lock at
    /// all are always eligible.
    /// </summary>
    private bool IsDecorationEligible(int sym, int spinNum) =>
        _locks.Where(lk => lk.Sym == sym).All(lk => spinNum < lk.FireSpin);

    /// <summary>
    /// Fills every position the board still has null after FillZone — by construction
    /// these are positions OUTSIDE this spin's own collection zone (FillZone fills every
    /// zone position, real win or decorative). Always ordinary filler: a non-zone
    /// position isn't collected this spin, so it can't safely carry a decorative win
    /// symbol — see FillZone for where decoration actually happens.
    /// </summary>
    private void FillRest(Cell?[,] board)
    {
        for (int r = 0; r < K.ROWS; r++)
        {
            for (int c = 0; c < K.COLS; c++)
            {
                if (board[r, c] == null)
                    board[r, c] = Grid.Norm(_fillTracker.Next());
            }
        }
    }

    private static void RemoveDelayedWheelCarryAlloc(
        Dictionary<int, int> alloc,
        IReadOnlyDictionary<int, int> delayedWheelCounts)
    {
        foreach (var (sym, count) in delayedWheelCounts)
        {
            if (!alloc.TryGetValue(sym, out var existing)) continue;
            var next = Math.Max(0, existing - count);
            if (next == 0) alloc.Remove(sym);
            else alloc[sym] = next;
        }
    }

    // ── Pusher calculation ─────────────────────────────────────────────────
    /// <summary>
    /// Builds mixed 1/2/3 pusher values. The target capacity is clamped to the same
    /// mixed-push ceiling used by Scheduler, plus any FLUSH capacity, so this stage
    /// cannot silently create a 4-row push or rely on flat 3/3/3/3/3 screens.
    /// </summary>
    private (int[] push, bool[] flush) MakePushers(int spinNum, List<PlacedFeat> sf,
                                                    IReadOnlyDictionary<int, int> alloc,
                                                    int reserved)
    {
        var flushCols = sf.Where(f => f.Id == "FLUSH").Select(f => f.Col).ToHashSet();
        bool isWheel  = _locks.Any(lk => lk.FireSpin == spinNum);
        int freeCols  = K.COLS - flushCols.Count;
        // Zone must hold both the allocated wins AND the reserved token slot(s)
        int total     = alloc.Values.Sum() + reserved;

        int needed = Math.Clamp(total - flushCols.Count * K.ROWS, freeCols * K.MIN_PUSH, freeCols * K.MAX_PUSH);

        int[] pv = MakeVariedPushValues(freeCols, needed, allowVisualLift: true);

        var push  = new int[K.COLS];
        var flush = new bool[K.COLS];
        int fi = 0;
        for (int col = 0; col < K.COLS; col++)
        {
            if (flushCols.Contains(col)) { push[col] = K.ROWS; flush[col] = true; }
            else push[col] = pv[fi++];
        }
        return (push, flush);
    }

    private int[] MakeVariedPushValues(int freeCols, int needed, bool allowVisualLift)
    {
        if (freeCols <= 0) return Array.Empty<int>();

        int minTotal = freeCols * K.MIN_PUSH;
        int maxTotal = allowVisualLift
            ? K.MixedPushCapacity(freeCols)
            : freeCols * K.MAX_PUSH;
        int targetTotal = Math.Clamp(needed, minTotal, maxTotal);

        if (allowVisualLift)
        {
            targetTotal = Math.Max(targetTotal, MinimumMixedPushTotal(freeCols));
            targetTotal = Math.Min(maxTotal, targetTotal + ProfilePushLift(maxTotal - targetTotal));
        }

        var best = BuildBalancedPushComposition(freeCols, targetTotal);
        return RandomizePushOrder(best);
    }

    private int ProfilePushLift(int headroom)
    {
        if (headroom <= 0) return 0;

        return _experienceProfile switch
        {
            TicketExperienceProfile.FeatureRich or TicketExperienceProfile.StackDrama =>
                _rng.Next(0, Math.Min(2, headroom) + 1),
            TicketExperienceProfile.NearMissHeavy when _rng.NextDouble() < 0.35 => 1,
            TicketExperienceProfile.LateWin when _rng.NextDouble() < 0.20 => 1,
            _ => 0,
        };
    }

    private static int MinimumMixedPushTotal(int freeCols)
    {
        if (freeCols >= 4)
        {
            return K.MIN_PUSH * freeCols + 6;
        }

        if (freeCols == 3)
        {
            return K.MIN_PUSH * freeCols + 3;
        }

        return freeCols == 2
            ? K.MIN_PUSH * freeCols + 1
            : K.MIN_PUSH * freeCols;
    }

    private int[] BuildBalancedPushComposition(int freeCols, int targetTotal)
    {
        var candidates = new List<int[]>();
        var current = new int[freeCols];
        CollectPushCompositions(0, targetTotal, current, candidates);
        if (candidates.Count == 0)
            return BuildFallbackPushComposition(freeCols, targetTotal);

        var bestScore = candidates.Max(PushShapeScore);
        var best = candidates
            .Where(candidate => PushShapeScore(candidate) == bestScore)
            .OrderBy(_ => _rng.Next())
            .First();
        return best;
    }

    private static void CollectPushCompositions(
        int index,
        int remaining,
        int[] current,
        List<int[]> candidates)
    {
        var left = current.Length - index;
        if (left == 0)
        {
            if (remaining == 0)
                candidates.Add(current.ToArray());
            return;
        }

        for (var value = K.MIN_PUSH; value <= K.MAX_PUSH; value++)
        {
            var nextRemaining = remaining - value;
            if (nextRemaining < (left - 1) * K.MIN_PUSH) continue;
            if (nextRemaining > (left - 1) * K.MAX_PUSH) continue;
            current[index] = value;
            CollectPushCompositions(index + 1, nextRemaining, current, candidates);
        }
    }

    private int[] BuildFallbackPushComposition(int freeCols, int targetTotal)
    {
        var values = Enumerable.Repeat(K.MIN_PUSH, freeCols).ToArray();
        var remaining = targetTotal - values.Sum();
        var index = 0;
        while (remaining > 0 && values.Any(value => value < K.MAX_PUSH))
        {
            if (values[index] < K.MAX_PUSH)
            {
                values[index]++;
                remaining--;
            }

            index = (index + 1) % values.Length;
        }
        return values;
    }

    private static int PushShapeScore(int[] values)
    {
        var distinct = values.Distinct().Count();
        var maxFrequency = values.GroupBy(v => v).Max(g => g.Count());
        var allValues = Enumerable.Range(K.MIN_PUSH, K.MAX_PUSH - K.MIN_PUSH + 1)
            .Count(value => values.Contains(value));
        var score = allValues * 500
            + distinct * 120
            + values.Count(v => v == 3) * 45
            + values.Count(v => v == 2) * 35
            + values.Count(v => v == 4) * 30
            + values.Count(v => v == 1) * 25
            - maxFrequency * 20
            - Math.Abs(values.Count(v => v == 3) - values.Count(v => v == 4)) * 15
            - Math.Abs(values.Count(v => v == 2) - values.Count(v => v == 3)) * 10;
        if (IsMonotonic(values)) score -= 200;
        return score;
    }

    private int[] RandomizePushOrder(int[] values)
    {
        if (values.Length <= 1) return values;

        var best = values.ToArray();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var shuffled = values.OrderBy(_ => _rng.Next()).ToArray();
            if (!IsMonotonic(shuffled))
                return shuffled;
            best = shuffled;
        }

        if (!IsMonotonic(best)) return best;
        for (var i = 1; i < best.Length - 1; i++)
        {
            var swapped = best.ToArray();
            (swapped[i], swapped[^1]) = (swapped[^1], swapped[i]);
            if (!IsMonotonic(swapped)) return swapped;
        }

        return best;
    }

    private static bool IsMonotonic(IReadOnlyList<int> values)
    {
        if (values.Distinct().Count() <= 1) return false;
        var nonDecreasing = true;
        var nonIncreasing = true;
        for (var i = 1; i < values.Count; i++)
        {
            nonDecreasing &= values[i] >= values[i - 1];
            nonIncreasing &= values[i] <= values[i - 1];
        }
        return nonDecreasing || nonIncreasing;
    }

    private static FP MakeFP(PlacedFeat f)
    {
        var fp = new FP { FeatId = f.Id };
        if (f.Id == "WHEEL")         { fp.WheelSym = f.WSym; fp.WheelStack = WMath.StackFromValue(f.WN); }
        if (f.Id == "PRIZE_UPGRADE") { fp.PrupSym  = f.PrupSym; fp.PrupTier = f.PrupTier; }
        return fp;
    }
}

