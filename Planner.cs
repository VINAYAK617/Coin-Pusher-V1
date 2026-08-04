#pragma warning disable S2245 // Planner randomness is intentionally seedable for reproducible tickets/tests.
namespace CoinPusherEngine;

/// <summary>
/// Public ticket planner. The active implementation is the fresh staged
/// architecture:
/// Input -> Objectives -> FeaturePlan -> PlacementPlan -> AllocationPlan ->
/// BoardRealization -> Verifier.
/// </summary>
public sealed class Planner
{
    private readonly MathInput _input;
    private readonly int _baseSeed;
    private readonly CleanGenerationPipeline _pipeline;
    private readonly Settings _settings;
    private static readonly Random SeedRng = new();
    private static readonly object SeedLock = new();
    private static readonly IPlanAssemblyPipeline DefaultBoardPipeline = new DefaultPlanAssemblyPipeline();
    private static readonly CleanGenerationPipeline DefaultPipeline = new(DefaultBoardPipeline);

    private const int MaxPlanAttempts = 64;

    public Planner(MathInput inp, int? seed = null)
        : this(inp, seed, new Settings(), DefaultBoardPipeline)
    {
    }

    public Planner(MathInput inp, Settings settings, int? seed = null)
        : this(inp, seed, settings, DefaultBoardPipeline)
    {
    }

    internal Planner(MathInput inp, int? seed, IPlanAssemblyPipeline boardPipeline)
        : this(inp, seed, new Settings(), boardPipeline)
    {
    }

    internal Planner(MathInput inp, int? seed, Settings settings, IPlanAssemblyPipeline boardPipeline)
    {
        _input = inp;
        _baseSeed = seed ?? NextSeed();
        _settings = settings;
        _pipeline = ReferenceEquals(boardPipeline, DefaultBoardPipeline)
            ? DefaultPipeline
            : new CleanGenerationPipeline(boardPipeline);
    }

    public GamePlan Plan()
    {
        Validate(_input, _settings);

        Exception? last = null;
        var retryCauses = new Dictionary<string, int>();
        var pressure = PlanningPressure(_input);

        for (var attempt = 0; attempt < MaxPlanAttempts; attempt++)
        {
            var attemptSeed = AttemptSeed(_baseSeed, attempt);
            var rng = new Random(attemptSeed);
            var log = new List<string>();

            try
            {
                var plan = _pipeline.Generate(new GenerationRequest(
                    _input,
                    attemptSeed,
                    rng,
                    _settings,
                    pressure,
                    log));

                if (attempt > 0)
                {
                    plan.Log.Insert(0, $"planned after {attempt + 1} internal attempts");
                    plan.Log.Insert(1, "planner retry causes: " + string.Join(", ",
                        retryCauses.OrderByDescending(kv => kv.Value)
                                   .Select(kv => $"{kv.Key}={kv.Value}")));
                }

                return plan;
            }
            catch (Exception ex)
            {
                last = ex;
                var key = FailureKey(ex);
                retryCauses[key] = retryCauses.GetValueOrDefault(key) + 1;
            }
        }

        throw new InvalidOperationException(
            $"Could not build a verified plan after {MaxPlanAttempts} smart attempts.",
            last);
    }

    private static int NextSeed()
    {
        lock (SeedLock) return SeedRng.Next();
    }

    private static int PlanningPressure(MathInput input)
    {
        var targetSum = input.Targets.Values.Sum();
        var featureTokenLoad = input.Required
            .Where(kv => FeatReg.Has(kv.Key) && FeatReg.Get(kv.Key).HasToken)
            .Sum(kv => kv.Value);

        if (input.Targets.Count >= 4 || targetSum >= 80 || featureTokenLoad >= 4)
            return 2;
        if (input.Targets.Count >= 3 || featureTokenLoad > 0)
            return 1;
        return 0;
    }

    private static string FailureKey(Exception ex)
    {
        var leaf = ex;
        while (leaf.InnerException != null) leaf = leaf.InnerException;

        var message = leaf.Message;
        if (message.StartsWith("VERIFY FAIL nonwin", StringComparison.Ordinal)) return "verify-nonwin";
        if (message.StartsWith("VERIFY FAIL filler", StringComparison.Ordinal)) return "verify-filler-cap";
        if (message.StartsWith("VERIFY FAIL sym", StringComparison.Ordinal)) return "verify-win";
        if (message.StartsWith("VERIFY FAIL last spin", StringComparison.Ordinal)) return "verify-final-win";
        if (message.StartsWith("VERIFY FAIL WHEEL token", StringComparison.Ordinal)) return "verify-final-wheel";
        if (message.StartsWith("VERIFY FAIL TotalSpins", StringComparison.Ordinal)) return "verify-extra-spin-count";
        if (message.StartsWith("VERIFY FAIL top prize", StringComparison.Ordinal)) return "verify-top-prize-upgrade";
        if (message.StartsWith("VERIFY FAIL WheelStackValue", StringComparison.Ordinal)) return "verify-wheel-stack";
        if (message.StartsWith("VERIFY FAIL cell stack", StringComparison.Ordinal)) return "verify-cell-stack";
        if (message.Contains("required WHEEL", StringComparison.Ordinal)) return "place-wheel";
        if (message.Contains("required FLUSH", StringComparison.Ordinal)) return "place-flush";
        if (message.Contains("required EXTRA_SPIN", StringComparison.Ordinal)) return "place-extra-spin";
        if (message.Contains("required PRIZE_UPGRADE", StringComparison.Ordinal)) return "place-prize-upgrade";
        if (message.Contains("board suffix", StringComparison.Ordinal)) return "suffix-realization";
        return leaf.GetType().Name;
    }

    private static int AttemptSeed(int seed, int attempt)
    {
        unchecked
        {
            uint x = (uint)seed;
            x ^= (uint)(attempt + 1) * 0x9E3779B9u;
            x ^= x >> 16;
            x *= 0x85EBCA6Bu;
            x ^= x >> 13;
            x *= 0xC2B2AE35u;
            x ^= x >> 16;
            return (int)x;
        }
    }

    private static void Validate(MathInput input, Settings settings)
    {
        if (input.BaseSpins != settings.BASE_SPINS)
            throw new ArgumentException($"BaseSpins must be exactly {settings.BASE_SPINS}");
        if (input.Targets.Count == 0 && input.NonWinTargets is { Count: 0 })
            throw new ArgumentException("Targets and NonWinTargets cannot both be empty");
        if (input.MaxSym < 2)
            throw new ArgumentException("MaxSym must be >= 2");

        foreach (var (sym, target) in input.Targets)
        {
            if (sym < 1 || sym > input.MaxSym)
                throw new ArgumentException($"Symbol {sym} out of range 1..{input.MaxSym}");
            if (target <= 0)
                throw new ArgumentException($"Target for sym {sym} must be > 0");
        }

        var fillerCount = input.MaxSym - input.Targets.Count;
        if (fillerCount < 2)
        {
            throw new ArgumentException(
                $"Need at least 2 filler symbols; got {fillerCount} " +
                $"({input.Targets.Count} win symbols in a {input.MaxSym}-symbol game).");
        }

        foreach (var (id, count) in input.Required)
        {
            if (!FeatReg.Has(id))
                throw new ArgumentException($"Unknown feature '{id}'");
            if (count < 0)
                throw new ArgumentException($"Required count for {id} must be >= 0");
        }

        if (input.PrizeTiers != null)
        {
            foreach (var (sym, tier) in input.PrizeTiers)
            {
                if (!input.Targets.ContainsKey(sym))
                    throw new ArgumentException($"PrizeTiers sym {sym} not in Targets");
                if (tier < 0)
                    throw new ArgumentException($"Prize tier for sym {sym} must be >= 0");
            }
        }

        if (input.PrizeValues != null)
        {
            foreach (var (sym, tiers) in input.PrizeValues)
            {
                if (sym < 1 || sym > input.MaxSym)
                    throw new ArgumentException($"PrizeValues sym {sym} out of range 1..{input.MaxSym}");
                foreach (var (tier, value) in tiers)
                {
                    if (tier < 0)
                        throw new ArgumentException($"Prize value tier for sym {sym} must be >= 0");
                    if (value < 0)
                        throw new ArgumentException($"Prize value for sym {sym} tier {tier} must be >= 0");
                }
            }
        }

        if (input.NonWinTargets != null)
        {
            foreach (var (sym, target) in input.NonWinTargets)
            {
                if (sym < 1 || sym > input.MaxSym)
                    throw new ArgumentException($"NonWinTargets sym {sym} out of range 1..{input.MaxSym}");
                if (input.Targets.ContainsKey(sym))
                    throw new ArgumentException($"NonWinTargets sym {sym} is already a winning target");
                var cap = settings.SymbolFillCap(sym);
                if (target < settings.NONWIN_MIN_TARGET || target >= cap)
                    throw new ArgumentException($"NonWinTargets sym {sym} must be in range {settings.NONWIN_MIN_TARGET}..{cap - 1}");
            }
        }

        if (input.NonWinPrizeTiers != null)
        {
            foreach (var (sym, tier) in input.NonWinPrizeTiers)
            {
                if (sym < 1 || sym > input.MaxSym)
                    throw new ArgumentException($"NonWinPrizeTiers sym {sym} out of range 1..{input.MaxSym}");
                if (input.Targets.ContainsKey(sym))
                    throw new ArgumentException($"NonWinPrizeTiers sym {sym} is already a winning target");
                if (input.NonWinTargets != null && !input.NonWinTargets.ContainsKey(sym))
                    throw new ArgumentException($"NonWinPrizeTiers sym {sym} must exist in NonWinTargets");
                if (tier <= 0)
                    throw new ArgumentException($"NonWinPrizeTiers sym {sym} must be > 0");
            }
        }
    }
}
