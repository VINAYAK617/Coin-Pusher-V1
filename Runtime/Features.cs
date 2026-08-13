namespace CoinPusherEngine;

// ── Base ──────────────────────────────────────────────────────────────────────
internal abstract class Feat
{
    internal abstract string   Id      { get; }
    internal abstract int      FeatSym { get; }
    internal virtual  bool     HasToken => true;
    internal abstract PlacedFeat? TryPlace(PlaceCtx ctx);
    internal abstract void        Fire(FireCtx ctx);
    internal virtual  IEnumerable<Cell> Collect(FireCtx ctx) => Enumerable.Empty<Cell>();
}

// ── WHEEL ─────────────────────────────────────────────────────────────────────
internal sealed class WheelFeat : Feat
{
    private const string FeatureId = "WHEEL";

    internal override string Id      => FeatureId;
    internal override int    FeatSym => Settings.F_WHEEL;

    internal override PlacedFeat? TryPlace(PlaceCtx ctx)
    {
        int spin = ctx.Spin, col = ctx.Col;
        // ctx.MaxSpin already reflects the true placement ceiling computed by Placer
        // (BaseSpins + any EXTRA_SPIN awards already decided) — trust it directly
        // rather than re-deriving a stale ceiling from BaseSpins alone, which would
        // wrongly exclude bonus spins added by EXTRA_SPIN.
        int maxSpin = ctx.MaxSpin;
        if (spin < ctx.MinSpin || spin >= maxSpin || col >= Settings.COLS - 1) return null;
        if (ctx.Used.Contains((spin, col))) return null;

        int sym = PickSym(ctx);
        if (sym == 0) return null;
        if (!ctx.Input.Targets.TryGetValue(sym, out int tgt) || tgt <= 0) return null;

        int n = PickStackValue(tgt, ctx.Rng, ctx.ExperienceProfile, Settings);
        int stack = WMath.StackFromValue(n), zone = Math.Max(1, WMath.Zone(tgt, stack));

        bool isMulti = ctx.Done.Any(f => f.Id == FeatureId && f.WSym == sym);
        if (isMulti)
        {
            if (maxSpin < 5) return null;
            int last = ctx.Done.Where(f => f.Id == FeatureId && f.WSym == sym).Max(f => f.Spin);
            if (spin <= last + 1) return null;
        }

        int collectibleZone = WMath.CollectibleZone(tgt, stack);
        if (!WMath.EdfOk(Math.Max(0, tgt - collectibleZone * stack), spin, ctx.Done, ctx.Input.Targets, isMulti))
            return null;

        // Don't overflow a spin's zone with multiple WHEELs
        int existZone = ctx.Done
            .Where(f => f.Id == FeatureId && f.Spin == spin)
                .Sum(f => ctx.Input.Targets.TryGetValue(f.WSym, out int ft)
                    ? Math.Max(1, WMath.Zone(ft, WMath.StackFromValue(f.WN)))
                    : 0);
        if (existZone + zone > Settings.COLS - 1) return null;

        return new PlacedFeat { Id=FeatureId, Spin=spin, Col=col, WSym=sym, WN=n };
    }

    internal override void Fire(FireCtx ctx)
    {
        int sym = ctx.Fp.WheelSym, st = ctx.Fp.WheelStack;
        if (sym == 0 || st <= 1) return;
        for (int r = 0; r < Settings.ROWS; r++)
        {
            for (int c = 0; c < Settings.COLS; c++)
            {
                var cell = ctx.Board[r, c];
                if (cell != null && !cell.IsFeat && cell.Sym == sym)
                    cell.Stack = Math.Min(Settings.MAX_COIN_STACK, cell.Stack + st - 1);
            }
        }
    }

    private static int PickSym(PlaceCtx ctx)
    {
        var order = ctx.Input.WheelSymOrder?.Where(s => ctx.Input.Targets.ContainsKey(s)).ToList()
                 ?? ctx.Input.Targets.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
        var usedCount = ctx.Done.Where(f => f.Id == FeatureId && f.WSym != 0)
                            .GroupBy(f => f.WSym).ToDictionary(g => g.Key, g => g.Count());

        if (ctx.Input.WheelSymOrder != null && ctx.Input.WheelSymOrder.Count > 0)
        {
            var desired = order
                .GroupBy(s => s)
                .ToDictionary(g => g.Key, g => g.Count());
            var nextPlanned = order.FirstOrDefault(sym => usedCount.GetValueOrDefault(sym) < desired[sym]);
            if (nextPlanned != 0) return nextPlanned;
        }

        var fresh = order.Distinct().Where(s => !usedCount.ContainsKey(s)).ToList();
        if (fresh.Count > 0) return fresh[0];
        var once = usedCount.Where(kv => kv.Value==1).Select(kv => kv.Key).ToList();
        if (once.Count > 0 && ctx.Rng.NextDouble() < 0.15) return once[ctx.Rng.Next(once.Count)];
        return 0;
    }

    private static int PickStackValue(int target, Random rng, TicketExperienceProfile profile, GameEngine.ICustomProfileSettings settings)
    {
        var candidates = WMath.ValidStackValues(target).ToArray();
        if (candidates.Length == 0) return WMath.BestN(target);

        var weighted = candidates
            .Select(value => (Value: value, Weight: StackValueWeight(value, profile, settings)))
            .Where(item => item.Weight > 0)
            .ToArray();
        if (weighted.Length == 0) return candidates[rng.Next(candidates.Length)];

        var total = weighted.Sum(item => item.Weight);
        var roll = rng.NextDouble() * total;
        var acc = 0.0;
        foreach (var item in weighted)
        {
            acc += item.Weight;
            if (roll <= acc) return item.Value;
        }

        return weighted[^1].Value;
    }

    private static double StackValueWeight(int value, TicketExperienceProfile profile, GameEngine.ICustomProfileSettings settings)
    {
        var baseWeight = value switch
        {
            1 => settings.PWheelStackValue1,
            2 => settings.PWheelStackValue2,
            _ => Math.Max(0.0, 1.0 - settings.PWheelStackValue1 - settings.PWheelStackValue2),
        };

        return profile switch
        {
            TicketExperienceProfile.StackDrama when value == 3 => baseWeight * 1.80,
            TicketExperienceProfile.StackDrama when value == 1 => baseWeight * 0.75,
            TicketExperienceProfile.NearMissHeavy when value == 1 => baseWeight * 1.25,
            TicketExperienceProfile.FeatureRich when value == 2 => baseWeight * 1.20,
            _ => baseWeight,
        };
    }
}

// ── FLUSH ─────────────────────────────────────────────────────────────────────
internal sealed class FlushFeat : Feat
{
    private const string FeatureId = "FLUSH";

    internal override string Id       => FeatureId;
    internal override int    FeatSym  => Settings.F_COIN;
    internal override bool   HasToken => false;

    internal override PlacedFeat? TryPlace(PlaceCtx ctx)
    {
        if (ctx.Spin < ctx.MinSpin || ctx.Spin >= ctx.MaxSpin) return null;
        if (ctx.Used.Contains((ctx.Spin, ctx.Col))) return null;
        return new PlacedFeat { Id=FeatureId, Spin=ctx.Spin, Col=ctx.Col };
    }

    internal override void Fire(FireCtx ctx) { }

    internal override IEnumerable<Cell> Collect(FireCtx ctx)
    {
        for (int r = 0; r < Settings.ROWS; r++)
        {
            var cell = ctx.Board[r, ctx.Col];
            if (cell == null) continue;
            yield return cell.Clone();
            ctx.Board[r, ctx.Col] = null;
        }
    }
}

// ── EXTRA_SPIN ────────────────────────────────────────────────────────────────
internal sealed class XSpinFeat : Feat
{
    private const string FeatureId = "EXTRA_SPIN";

    internal override string Id      => FeatureId;
    internal override int    FeatSym => Settings.F_XSPIN;

    internal override PlacedFeat? TryPlace(PlaceCtx ctx)
    {
        if (ctx.Spin < ctx.MinSpin || ctx.Spin >= ctx.MaxSpin || ctx.Col >= Settings.COLS - 1) return null;
        if (ctx.Used.Contains((ctx.Spin, ctx.Col))) return null;
        return new PlacedFeat { Id=FeatureId, Spin=ctx.Spin, Col=ctx.Col };
    }

    internal override void Fire(FireCtx ctx) { }
}

// ── PRIZE_UPGRADE ─────────────────────────────────────────────────────────────
/// <summary>
/// Board token — visual only. Fire() is a no-op.
/// Prize tier overrides are pre-declared in GamePlan.PrizeTiers and read at payout time only.
/// Does NOT affect collection counts, board cells, stacks, or spin count.
/// </summary>
/// <summary>
/// Board token — visual only. Fire() is a no-op.
/// Prize tier overrides are pre-declared in GamePlan.PrizeTiers and read at payout time only.
/// Does NOT affect collection counts, board cells, stacks, or spin count.
///
/// Multi-level upgrade chains: a symbol's declared tier in MathInput.PrizeTiers (e.g. tier=2)
/// is reached by placing that many SEPARATE PRIZE_UPGRADE tokens for that symbol, each at its
/// own spin — token #1 escalates the symbol from tier 0 to tier 1, token #2 escalates it from
/// tier 1 to tier 2, and so on. Each token's own Fp.PrupTier records the tier it escalates TO,
/// so a ticket can show the step-by-step climb (e.g. "$1 → $2 → $5") rather than a single jump.
/// The symbol's FINAL tier — and therefore its payout — is whichever tier the LAST token in the
/// chain reaches, which by construction always equals the declared target in PrizeTiers.
/// </summary>
internal sealed class PrupFeat : Feat
{
    private const string FeatureId = "PRIZE_UPGRADE";

    internal override string Id      => FeatureId;
    internal override int    FeatSym => Settings.F_PRUP;

    internal override PlacedFeat? TryPlace(PlaceCtx ctx)
    {
        if (ctx.Input.PrizeTiers == null || ctx.Input.PrizeTiers.Count == 0) return null;
        if (ctx.Spin < ctx.MinSpin || ctx.Spin >= ctx.MaxSpin || ctx.Col >= Settings.COLS - 1) return null;
        if (ctx.Used.Contains((ctx.Spin, ctx.Col))) return null;

        // For each declared (symbol, targetTier) pair, find how many PRIZE_UPGRADE tokens
        // already target that symbol. The next token for that symbol escalates it by exactly
        // one tier step. Once a symbol has reached its declared target tier, no further tokens
        // are placed for it. Symbols are tried in id order for determinism; the first symbol
        // that still has remaining tier steps to climb is the one this token will represent.
        var alreadyPerSym = ctx.Done
            .Where(f => f.Id == FeatureId)
            .GroupBy(f => f.PrupSym)
            .ToDictionary(g => g.Key, g => g.Count());

        var candidate = ctx.Input.PrizeTiers
            .Where(kv => ctx.Input.Targets.ContainsKey(kv.Key) && kv.Value > 0)
            .Select(kv =>
            {
                var sym = kv.Key;
                var targetTier = kv.Value;
                var already = alreadyPerSym.GetValueOrDefault(sym, 0);
                var nextTier = already + 1;
                var lastSpin = ctx.Done
                    .Where(f => f.Id == FeatureId && f.PrupSym == sym)
                    .Select(f => f.Spin)
                    .DefaultIfEmpty(0)
                    .Max();
                var remainingAfterThis = targetTier - nextTier;
                var slack = (ctx.MaxSpin - 1 - ctx.Spin) - remainingAfterThis;
                return new
                {
                    Sym = sym,
                    TargetTier = targetTier,
                    Already = already,
                    NextTier = nextTier,
                    LastSpin = lastSpin,
                    RemainingAfterThis = remainingAfterThis,
                    Slack = slack,
                };
            })
            .Where(x => x.Already < x.TargetTier)
            .Where(x => ctx.Spin > x.LastSpin)
            .Where(x => x.Slack >= 0)
            .OrderBy(x => x.Slack)
            .ThenByDescending(x => x.RemainingAfterThis)
            .ThenBy(x => x.Sym)
            .FirstOrDefault();

        if (candidate != null)
        {
            return new PlacedFeat { Id=FeatureId, Spin=ctx.Spin, Col=ctx.Col,
                                     PrupSym=candidate.Sym, PrupTier=candidate.NextTier };
        }

        return null;   // every declared symbol has already reached its target tier
    }

    internal override void Fire(FireCtx ctx) { }  // intentional no-op
}

// ── Registry ──────────────────────────────────────────────────────────────────
internal static class FeatReg
{
    private static readonly Dictionary<string, Feat> ById = new()
    {
        ["WHEEL"]         = new WheelFeat(),
        ["FLUSH"]         = new FlushFeat(),
        ["EXTRA_SPIN"]    = new XSpinFeat(),
        ["PRIZE_UPGRADE"] = new PrupFeat(),
    };
    private static readonly Dictionary<int, Feat> BySym =
        ById.Where(kv => kv.Value.HasToken)
            .ToDictionary(kv => kv.Value.FeatSym, kv => kv.Value);

    internal static IEnumerable<string> Ordered(GameEngine.ICustomProfileSettings settings) =>
        settings.OrderedFeatureIds;

    internal static bool Has(string id)  => ById.ContainsKey(id);
    internal static bool HasSym(int sym) => BySym.ContainsKey(sym);
    internal static Feat Get(string id)  => ById[id];
    internal static Feat GetSym(int sym) => BySym[sym];
}
