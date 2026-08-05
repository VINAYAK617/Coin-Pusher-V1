using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
namespace CoinPusherEngine;

/// <summary>
/// Converts a verified GamePlan into the ticket JSON structure the front end consumes:
///   { WinInfo: { TotalSpins, WinSymbols, NonWinSymbols, PrizeTiers },
///     StartingBoard: [5x5 of {Id}],
///     Turns: [ { Pushers: [...], Spawns: [{Pos,Id,...}] }, ... ] }
///
/// Feature object shapes:
///   WHEEL         -> { FeatureId, ConvertToId, WheelSymbolId, WheelStackValue }
///                    WheelStackValue is bonus N; collected value is 1 + N.
///   EXTRA_SPIN    -> { FeatureId, ConvertToId, ReTrigger: [...] }
///   PRIZE_UPGRADE -> { FeatureId, ConvertToId, UpgradeSymbolId, UpgradePrizeValue }
///
/// ReTrigger chaining: with configurable probability, a same-turn cosmetic
/// PRIZE_UPGRADE token may be folded into another feature token's ReTrigger array.
/// EXTRA_SPIN and WHEEL always stay physical because TotalSpins and WHEEL stack timing
/// are load-bearing. ReTrigger depth is intentionally capped at one nested feature.
///
/// Pos field: every spawn carries "Pos": row*5+col (flat index), per the established schema.
/// </summary>
public static class TicketSerializer
{
    // DTO models
    public class TicketDto
    {
        public WinInfoDto WinInfo { get; set; } = null!;
        public BoardCellDto[][] StartingBoard { get; set; } = null!;
        public TurnDto[] Turns { get; set; } = null!;
    }

    public class WinInfoDto
    {
        public int TotalSpins { get; set; }
        public WinSymbolDto[] WinSymbols { get; set; } = null!;
        public NonWinSymbolDto[] NonWinSymbols { get; set; } = null!;
        public PrizeTierDto[] PrizeTiers { get; set; } = null!;
    }

    public class WinSymbolDto { public int Id { get; set; } public int Target { get; set; } }
    public class NonWinSymbolDto
    {
        public int Id { get; set; }
        public int MinTarget { get; set; }
        public int MaxThreshold { get; set; }
        public int? PrizeTier { get; set; }
        public decimal? PrizeValue { get; set; }
    }
    public class PrizeTierDto { public int SymId { get; set; } public int Tier { get; set; } }

    public class BoardCellDto { public int Id { get; set; } }

    public class TurnDto
    {
        public PusherDto[] Pushers { get; set; } = null!;
        public SpawnDto[] Spawns { get; set; } = null!;
    }

    public class PusherDto
    {
        public int PushValue { get; set; }
        public int? FeatureId { get; set; }
    }

    public class SpawnDto
    {
        public int Pos { get; set; }
        public int Id { get; set; }
        public int? Stack { get; set; }
        public FeatureDto? Feature { get; set; }
    }

    public class FeatureDto
    {
        public int FeatureId { get; set; }
        public int ConvertToId { get; set; }
        public int? WheelSymbolId { get; set; }
        public int? WheelStackValue { get; set; }
        public int? UpgradeSymbolId { get; set; }
        public decimal? UpgradePrizeValue { get; set; }
        public FeatureDto[] ReTrigger { get; set; } = System.Array.Empty<FeatureDto>();
    }

    /// <summary>Build the plain object graph (no JSON string yet) for a verified GamePlan.</summary>
    public static TicketDto ToTicketObject(GamePlan plan) =>
        ToTicketObject(plan, new Settings());

    /// <summary>Build the plain object graph (no JSON string yet) for a verified GamePlan.</summary>
    public static TicketDto ToTicketObject(GamePlan plan, Settings settings)
    {
        var board = plan.Spins[0].Board;
        var startingBoard = Enumerable.Range(0, settings.ROWS).Select(r =>
            Enumerable.Range(0, settings.COLS).Select(c => new BoardCellDto { Id = board[r, c]?.Sym ?? 0 }).ToArray()
        ).ToArray();
        var collectedTotals = Sim.Run(plan);

        return new TicketDto
        {
            WinInfo = new WinInfoDto
            {
                TotalSpins = plan.TotalSpins,
                WinSymbols = plan.Targets.OrderBy(kv => kv.Key)
                                 .Select(kv => new WinSymbolDto { Id = kv.Key, Target = kv.Value }).ToArray(),
                NonWinSymbols = BuildNonWinSymbols(plan, collectedTotals, settings),
                PrizeTiers = plan.PrizeTiers.OrderBy(kv => kv.Key)
                                 .Select(kv => new PrizeTierDto { SymId = kv.Key, Tier = kv.Value }).ToArray()
            },
            StartingBoard = startingBoard,
            Turns = BuildTurns(plan, settings)
        };
    }

    private static NonWinSymbolDto[] BuildNonWinSymbols(
        GamePlan plan,
        IReadOnlyDictionary<int, int> collectedTotals,
        Settings settings)
    {
        var ids = plan.NonWinTargets.Keys
            .Concat(collectedTotals
                .Where(kv => kv.Value > 0)
                .Select(kv => kv.Key)
                .Where(sym => !plan.Targets.ContainsKey(sym))
                .Where(sym => !settings.IsFeat(sym)))
            .Distinct()
            .OrderBy(sym => sym);

        return ids.Select(sym =>
        {
            plan.NonWinTargets.TryGetValue(sym, out int plannedMin);
            collectedTotals.TryGetValue(sym, out int collected);
            plan.NonWinPrizeTiers.TryGetValue(sym, out int tier);

            return new NonWinSymbolDto
            {
                Id = sym,
                MinTarget = plannedMin > 0 ? plannedMin : Math.Max(1, collected),
                MaxThreshold = settings.SymbolFillCap(sym),
                PrizeTier = tier > 0 ? tier : null,
                PrizeValue = tier > 0 ? PrizeValueFor(plan, sym, tier) : null,
            };
        }).ToArray();
    }

    /// <summary>Serialize a verified GamePlan straight to an indented JSON string.</summary>
    public static string ToJson(GamePlan plan) =>
        ToJson(plan, new Settings());

    /// <summary>Serialize a verified GamePlan straight to an indented JSON string.</summary>
    public static string ToJson(GamePlan plan, Settings settings) =>
        JsonConvert.SerializeObject(ToTicketObject(plan, settings), new JsonSerializerSettings
        {
            Formatting = Formatting.None,
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore
        });

    // ── Turn / spawn assembly ───────────────────────────────────────────────────

    private static TurnDto[] BuildTurns(GamePlan plan, Settings settings)
    {
        var allFeatureTokens = plan.Spins
            .SelectMany(sp => sp.Spawns
                .Where(kv => kv.Value.IsFeat)
                .Select(kv => (Spin: sp.Spin, Pos: kv.Key, Cell: kv.Value)))
            .OrderBy(t => t.Spin)
            .ThenBy(t => t.Pos.Item1 * settings.COLS + t.Pos.Item2)
            .ToList();
        var chainPlan = BuildFeatureChainPlan(plan, allFeatureTokens, settings);

        var chainStart = chainPlan.Start is null
            ? new HashSet<(int Spin, (int, int) Pos)>()
            : new HashSet<(int Spin, (int, int) Pos)> { (chainPlan.Start.Value.Spin, chainPlan.Start.Value.Pos) };
        var suppressed = chainPlan.Suppressed
            .Select(token => (token.Spin, token.Pos))
            .ToHashSet();

        var turns = new List<TurnDto>();
        foreach (var sp in plan.Spins)
        {
            var pushers = Enumerable.Range(0, settings.COLS).Select(c =>
                sp.Flush[c]
                    ? new PusherDto { PushValue = settings.ROWS, FeatureId = settings.F_FLUSH_ID }
                    : new PusherDto { PushValue = sp.Push[c], FeatureId = null }
            ).ToArray();

            var spawns = new List<SpawnDto>();
            foreach (var kv in sp.Spawns.OrderBy(kv => kv.Key.Item1 * settings.COLS + kv.Key.Item2))
            {
                var posKey = (sp.Spin, kv.Key);

                int pos = kv.Key.Item1 * settings.COLS + kv.Key.Item2;

                if (suppressed.Contains(posKey))
                {
                    spawns.Add(ConvertedSpawnObj(kv.Value, pos, settings));
                    continue;
                }

                if (chainStart.Contains(posKey) && chainPlan.Nested != null)
                {
                    var c   = kv.Value;
                    spawns.Add(new SpawnDto
                    {
                        Pos = pos,
                        Id = c.Sym,
                        Feature = FeatureObj(c, plan, settings, new[] { chainPlan.Nested }, depth: 0)
                    });
                    continue;
                }

                spawns.Add(SpawnObj(kv.Value, pos, plan, settings));
            }

            turns.Add(new TurnDto { Pushers = pushers, Spawns = spawns.ToArray() });
        }

        return turns.ToArray();
    }

    private static FeatureChainPlan BuildFeatureChainPlan(
        GamePlan plan,
        IReadOnlyList<(int Spin, (int, int) Pos, Cell Cell)> featureTokens,
        Settings settings)
    {
        if (featureTokens.Count == 0) return FeatureChainPlan.Empty;

        var chainable = featureTokens
            .Where(token => token.Spin < plan.TotalSpins)
            .Where(token => IsReTriggerChainParticipant(token.Cell, settings))
            .ToList();
        if (chainable.Count == 0) return FeatureChainPlan.Empty;

        if (chainable.Count < 2) return FeatureChainPlan.Empty;

        var ordered = chainable
            .OrderBy(token => token.Spin)
            .ThenBy(token => token.Pos.Item1 * settings.COLS + token.Pos.Item2)
            .ThenBy(token => token.Cell.Sym)
            .ToList();

        var start = ordered[DeterministicIndex(plan, ordered.Count, salt: 97, settings)];
        var payloadCandidates = ordered
            .Where(token => token.Spin != start.Spin || token.Pos != start.Pos)
            .Where(token => IsTimingSafeReTriggerPayload(start, token, settings))
            .ToList();
        if (payloadCandidates.Count == 0) return FeatureChainPlan.Empty;

        var roll = DeterministicUnitInterval(plan, start.Cell.Sym, start.Spin, start.Pos, payloadCandidates.Count, settings);
        if (roll >= settings.PFeatureRetriggerChain) return FeatureChainPlan.Empty;

        var payload = payloadCandidates[DeterministicIndex(plan, payloadCandidates.Count, salt: 193, settings)];
        var nested = FeatureObj(payload.Cell, plan, settings, System.Array.Empty<FeatureDto>(), depth: 1);
        return new FeatureChainPlan(start, new[] { payload }, nested);
    }

    private static bool IsNoBoardEffectFeature(Cell cell, Settings settings) =>
        cell.Sym == settings.F_XSPIN || cell.Sym == settings.F_PRUP;

    private static bool IsReTriggerChainParticipant(Cell cell, Settings settings) =>
        IsNoBoardEffectFeature(cell, settings) || cell.Sym == settings.F_WHEEL;

    private static bool IsTimingSafeReTriggerPayload(
        (int Spin, (int, int) Pos, Cell Cell) start,
        (int Spin, (int, int) Pos, Cell Cell) payload,
        Settings settings)
    {
        if (payload.Cell.Sym == settings.F_WHEEL || payload.Cell.Sym == settings.F_XSPIN)
            return false;

        if (payload.Spin != start.Spin)
            return false;

        return payload.Cell.Sym == settings.F_PRUP;
    }

    private static int FeatureChainConvertId(Cell cell, int depth, GamePlan plan, Settings settings)
    {
        var ids = plan.WinSyms
            .Concat(plan.NonWinTargets.Keys)
            .Concat(plan.FillSyms)
            .Concat(settings.FeatureRetriggerBridgeIds)
            .Distinct()
            .ToArray();
        if (ids.Length == 0) return settings.F_COIN;

        var hash = cell.Sym;
        hash = unchecked(hash * 397) ^ cell.CvtSym;
        hash = unchecked(hash * 397) ^ depth;
        hash = unchecked(hash * 397) ^ (cell.Fp?.PrupSym ?? 0);
        return ids[(hash & 0x7fffffff) % ids.Length];
    }

    private sealed record FeatureChainPlan(
        (int Spin, (int, int) Pos, Cell Cell)? Start,
        IReadOnlyList<(int Spin, (int, int) Pos, Cell Cell)> Suppressed,
        FeatureDto? Nested)
    {
        public static FeatureChainPlan Empty { get; } =
            new(null, System.Array.Empty<(int, (int, int), Cell)>(), null);
    }

    private static double DeterministicUnitInterval(
        GamePlan plan,
        int featureId,
        int spin,
        (int r, int c) pos,
        int payloadCount,
        Settings settings)
    {
        unchecked
        {
            uint hash = FeatureChainHash(plan, salt: 131, settings);
            hash ^= (uint)featureId * 0x9E3779B9u;
            hash = Mix(hash + (uint)spin * 0x85EBCA6Bu);
            hash = Mix(hash + (uint)(pos.r * settings.COLS + pos.c) * 0xC2B2AE35u);
            hash = Mix(hash + (uint)payloadCount * 0x27D4EB2Fu);
            return (hash & 0x7fffffffu) / (double)0x80000000u;
        }
    }

    private static int DeterministicIndex(GamePlan plan, int count, int salt, Settings settings)
    {
        if (count <= 1) return 0;
        unchecked
        {
            return (int)(FeatureChainHash(plan, salt, settings) % (uint)count);
        }
    }

    private static uint FeatureChainHash(GamePlan plan, int salt, Settings settings)
    {
        unchecked
        {
            uint hash = Mix((uint)(salt * 397 + plan.TotalSpins));
            foreach (var (sym, target) in plan.Targets.OrderBy(kv => kv.Key))
                hash = Mix(hash ^ (uint)(sym * 1009 + target));
            foreach (var spin in plan.Spins)
            {
                hash = Mix(hash ^ (uint)(spin.Spin * 9176 + spin.Spawns.Count));
                foreach (var kv in spin.Spawns.OrderBy(kv => kv.Key.Item1 * settings.COLS + kv.Key.Item2))
                {
                    var pos = kv.Key.Item1 * settings.COLS + kv.Key.Item2;
                    var cell = kv.Value;
                    hash = Mix(hash ^ (uint)(pos * 257 + cell.Sym * 17 + cell.CvtSym));
                }
            }
            return hash;
        }
    }

    private static uint Mix(uint value)
    {
        unchecked
        {
            value ^= value >> 16;
            value *= 0x7FEB352Du;
            value ^= value >> 15;
            value *= 0x846CA68Bu;
            value ^= value >> 16;
            return value;
        }
    }

    private static SpawnDto SpawnObj(Cell c, int pos, GamePlan plan, Settings settings)
    {
        if (!c.IsFeat)
            return c.Stack > 1
                ? new SpawnDto { Pos = pos, Id = c.Sym, Stack = c.Stack }
                : new SpawnDto { Pos = pos, Id = c.Sym };

        int cvt = c.CvtSym > 0 ? c.CvtSym : settings.F_COIN;
        if (c.Sym == settings.F_WHEEL)
        {
            return new SpawnDto
            {
                Pos = pos,
                Id = c.Sym,
                Feature = FeatureObj(c, plan, settings)
            };
        }

        if (c.Sym == settings.F_XSPIN)
        {
            return new SpawnDto
            {
                Pos = pos,
                Id = c.Sym,
                Feature = FeatureObj(c, plan, settings)
            };
        }

        if (c.Sym == settings.F_PRUP)
        {
            return new SpawnDto
            {
                Pos = pos,
                Id = c.Sym,
                Feature = FeatureObj(c, plan, settings)
            };
        }

        return new SpawnDto
            {
                Pos = pos,
                Id = c.Sym,
                Feature = new FeatureDto { FeatureId = c.Sym, ConvertToId = cvt }
            };
    }

    private static FeatureDto FeatureObj(
        Cell c,
        GamePlan plan,
        Settings settings,
        FeatureDto[]? reTrigger = null,
        int depth = 0)
    {
        var chain = reTrigger ?? System.Array.Empty<FeatureDto>();
        var convertToId = chain.Length > 0 && depth > 0
            ? FeatureChainConvertId(c, depth, plan, settings)
            : c.CvtSym > 0 && !settings.IsFeat(c.CvtSym) ? c.CvtSym : settings.F_COIN;

        var dto = new FeatureDto
        {
            FeatureId = c.Sym,
            ConvertToId = convertToId,
            ReTrigger = chain,
        };

        if (c.Sym == settings.F_WHEEL)
        {
            dto.WheelSymbolId = c.Fp?.WheelSym ?? 0;
            // Public JSON uses bonus semantics: N means a collected cell counts
            // as 1 + N. Internally Fp.WheelStack stores the total stack value.
            dto.WheelStackValue = Math.Clamp(
                Math.Max(0, (c.Fp?.WheelStack ?? 1) - 1),
                settings.MIN_WHEEL_STACK_VALUE,
                settings.MAX_WHEEL_STACK_VALUE);
        }
        else if (c.Sym == settings.F_PRUP)
        {
            dto.UpgradeSymbolId = c.Fp?.PrupSym ?? 0;
            dto.UpgradePrizeValue = PrizeValueFor(plan, c.Fp?.PrupSym ?? 0, c.Fp?.PrupTier ?? 0);
        }

        return dto;
    }

    private static SpawnDto ConvertedSpawnObj(Cell c, int pos, Settings settings)
    {
        int cvt = c.CvtSym > 0 && !settings.IsFeat(c.CvtSym) ? c.CvtSym : settings.F_COIN;
        return new SpawnDto { Pos = pos, Id = cvt };
    }

    private static decimal PrizeValueFor(GamePlan plan, int sym, int tier)
    {
        if (plan.PrizeValues.TryGetValue(sym, out var tiers)
            && tiers.TryGetValue(tier, out decimal value))
            return value;

        // Hand-authored MathInput may only provide PrizeTiers. Keep serialization usable
        // while LadderCombinator-backed tickets emit actual prize values.
        return tier;
    }
}
