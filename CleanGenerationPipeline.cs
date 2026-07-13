namespace CoinPusherEngine;

/// <summary>
/// Clean ticket generation architecture. This file owns the orchestration
/// contracts; low-level physics primitives such as Builder, Resolver, Sim, and
/// Verifier remain reusable engines underneath these explicit stages.
/// </summary>
internal sealed class CleanGenerationPipeline
{
    private readonly ObjectiveStage _objectives = new();
    private readonly FeaturePlanStage _features = new();
    private readonly PlacementStage _placement = new();
    private readonly AllocationStage _allocation = new();
    private readonly BoardRealizationStage _realization;

    internal CleanGenerationPipeline(IPlanAssemblyPipeline? boardPipeline = null)
    {
        _realization = new BoardRealizationStage(boardPipeline ?? new DefaultPlanAssemblyPipeline());
    }

    internal GamePlan Generate(GenerationRequest request)
    {
        var objectives = _objectives.Resolve(request);
        var featurePlan = _features.Resolve(objectives);
        var placements = _placement.Resolve(featurePlan);
        var allocations = _allocation.Resolve(placements);
        return _realization.Resolve(allocations);
    }
}

internal sealed record GenerationRequest(
    MathInput Input,
    int Seed,
    Random Rng,
    int PlanningPressure,
    List<string> Log);

internal sealed record ObjectivePlan(
    MathInput SourceInput,
    IReadOnlyList<int> WinSymbols,
    IReadOnlyList<int> FillSymbols,
    IReadOnlyDictionary<int, int> WinTargets,
    IReadOnlyDictionary<int, int> NonWinTargets,
    IReadOnlyDictionary<int, int> NonWinPrizeTiers,
    int PlanningPressure,
    int Seed,
    Random Rng,
    List<string> Log);

internal sealed record FeaturePlan(
    ObjectivePlan Objectives,
    MathInput SchedulingInput,
    IReadOnlyDictionary<int, int> AllocationTargets,
    IReadOnlyDictionary<int, int> PrizeTiers,
    IReadOnlyDictionary<int, IReadOnlyDictionary<int, decimal>> PrizeValues);

internal sealed record PlacementPlan(
    FeaturePlan Features,
    IReadOnlyList<PlacedFeat> PlacedFeatures,
    IReadOnlyList<WLock> WheelLocks,
    int TotalSpins);

internal sealed record AllocationPlan(
    PlacementPlan Placements,
    IReadOnlyList<Dictionary<int, int>> Allocations);

internal sealed class ObjectiveStage
{
    internal ObjectivePlan Resolve(GenerationRequest request)
    {
        var input = request.Input;
        var winSymbols = input.Targets.Keys.OrderBy(x => x).ToArray();
        var topPrizeSym = TopPrizeSymbol(input);
        var fillSymbols = Enumerable.Range(1, input.MaxSym)
            .Except(winSymbols)
            .ToArray();
        var nearMissCandidates = fillSymbols;

        var nonWinTargets = ResolveNonWinTargets(input, nearMissCandidates, request.Rng, request.PlanningPressure);
        var nonWinPrizeTiers = ResolveNonWinPrizeTiers(input, nonWinTargets, request.Rng, request.PlanningPressure);

        request.Log.Add($"wins=[{string.Join(",", winSymbols)}] fills=[{string.Join(",", fillSymbols)}]");
        if (nonWinTargets.Count > 0)
        {
            request.Log.Add("nonWins=[" + string.Join(",",
                nonWinTargets.Select(kv => $"sym{kv.Key}>={kv.Value}<cap{K.SymbolFillCap(kv.Key)}")) + "]");
        }
        if (nonWinPrizeTiers.Count > 0)
        {
            request.Log.Add("nonWinPrizeUpgrades=[" + string.Join(",",
                nonWinPrizeTiers.Select(kv => $"sym{kv.Key}@tier{kv.Value}")) + "]");
        }

        return new ObjectivePlan(
            input,
            winSymbols,
            fillSymbols,
            input.Targets.ToDictionary(kv => kv.Key, kv => kv.Value),
            nonWinTargets,
            nonWinPrizeTiers,
            request.PlanningPressure,
            request.Seed,
            request.Rng,
            request.Log);
    }

    private static Dictionary<int, int> ResolveNonWinTargets(
        MathInput input,
        IReadOnlyList<int> fillSymbols,
        Random rng,
        int planningPressure)
    {
        if (input.NonWinTargets != null)
            return input.NonWinTargets.ToDictionary(kv => kv.Key, kv => kv.Value);
        if (fillSymbols.Count == 0)
            return new Dictionary<int, int>();

        (double P, int Min, int Max, int MaxSymbols) profile = input.Targets.Count == 0
            ? (1.0, K.NONWIN_MIN_TARGET, K.FILL_CAP - 1, 5)
            : PickNonWinProfile(rng);
        if (profile.MaxSymbols <= 0 || profile.Max <= 0)
            return new Dictionary<int, int>();

        var maxSymbols = Math.Min(profile.MaxSymbols, NearMissSymbolCap(input, fillSymbols.Count, planningPressure));
        var count = PickNearMissCount(input, maxSymbols, fillSymbols.Count, planningPressure, rng);
        var minTarget = Math.Max(profile.Min, K.NONWIN_MIN_TARGET);

        return PickNearMissSymbols(fillSymbols, count, rng)
            .Take(count)
            .ToDictionary(
                sym => sym,
                sym =>
                {
                    var symbolMax = Math.Min(profile.Max, K.SymbolFillCap(sym) - 1);
                    var symbolMin = Math.Min(minTarget, symbolMax);
                    return rng.Next(symbolMin, symbolMax + 1);
                });
    }

    private static IReadOnlyList<int> PickNearMissSymbols(
        IReadOnlyList<int> fillSymbols,
        int count,
        Random rng)
    {
        var available = fillSymbols.ToList();
        var rankOrder = fillSymbols.OrderBy(sym => sym).ToArray();
        var picked = new List<int>();

        while (picked.Count < count && available.Count > 0)
        {
            var groups = available
                .GroupBy(sym => NearMissBand(sym, rankOrder))
                .Select(group => new
                {
                    Band = group.Key,
                    Symbols = group.OrderBy(_ => rng.Next()).ToList(),
                    Weight = NearMissBandWeight(group.Key),
                })
                .Where(group => group.Weight > 0)
                .ToList();

            if (groups.Count == 0)
                groups = available
                    .GroupBy(sym => NearMissBand(sym, rankOrder))
                    .Select(group => new
                    {
                        Band = group.Key,
                        Symbols = group.OrderBy(_ => rng.Next()).ToList(),
                        Weight = 1.0,
                    })
                    .ToList();

            var total = groups.Sum(group => group.Weight);
            var roll = rng.NextDouble() * total;
            var acc = 0.0;
            var chosenGroup = groups[^1];
            foreach (var group in groups)
            {
                acc += group.Weight;
                if (roll <= acc)
                {
                    chosenGroup = group;
                    break;
                }
            }

            var sym = chosenGroup.Symbols[0];
            picked.Add(sym);
            available.Remove(sym);
        }

        return picked;
    }

    private static int NearMissBand(int sym, IReadOnlyList<int> orderedAvailableSymbols)
    {
        var index = 0;
        for (; index < orderedAvailableSymbols.Count; index++)
            if (orderedAvailableSymbols[index] == sym) break;

        var lowCount = (int)Math.Ceiling(orderedAvailableSymbols.Count / 3.0);
        var midCount = (int)Math.Ceiling(orderedAvailableSymbols.Count * 2 / 3.0);
        if (index < lowCount) return 0;
        if (index < midCount) return 1;
        return 2;
    }

    private static double NearMissBandWeight(int band) =>
        band switch
        {
            0 => K.W_NONWIN_LOW,
            1 => K.W_NONWIN_MID,
            _ => K.W_NONWIN_HIGH,
        };

    private static int NearMissSymbolCap(MathInput input, int fillSymbolCount, int planningPressure)
    {
        if (fillSymbolCount <= 0) return 0;
        if (planningPressure >= 2 || IsHighPressureTicket(input))
            return Math.Min(1, fillSymbolCount);

        var byWinCount = input.Targets.Count switch
        {
            0 => 5,
            1 => 5,
            2 => 4,
            3 => 3,
            _ => 2,
        };

        if (planningPressure >= 1)
            byWinCount = Math.Min(byWinCount, 2);

        return Math.Min(byWinCount, fillSymbolCount);
    }

    private static int PickNearMissCount(
        MathInput input,
        int maxSymbols,
        int fillSymbolCount,
        int planningPressure,
        Random rng)
    {
        var capped = Math.Min(maxSymbols, fillSymbolCount);
        if (capped <= 1) return capped;

        if (planningPressure >= 2 || IsHighPressureTicket(input))
            return 1;

        var min = input.Targets.Count switch
        {
            0 when capped >= 3 => 3,
            1 when capped >= 5 => 4,
            1 when capped >= 3 => 3,
            2 when capped >= 4 => 2,
            3 when capped >= 3 => 2,
            _ => 1,
        };

        var allowedMin = Math.Min(min, capped);
        var weights = K.NONWIN_COUNT_WEIGHTS
            .Select((weight, index) => new
            {
                Count = index + 1,
                Weight = weight,
            })
            .Where(x => x.Count >= allowedMin && x.Count <= capped && x.Weight > 0)
            .ToArray();
        if (weights.Length == 0) return allowedMin;

        var total = weights.Sum(x => x.Weight);
        var roll = rng.NextDouble() * total;
        var acc = 0.0;
        foreach (var item in weights)
        {
            acc += item.Weight;
            if (roll <= acc) return item.Count;
        }

        return weights[^1].Count;
    }

    private static (double P, int Min, int Max, int MaxSymbols) PickNonWinProfile(Random rng)
    {
        var roll = rng.NextDouble();
        var acc = 0.0;
        foreach (var profile in K.NONWIN_TARGET_PROFILES)
        {
            acc += profile.P;
            if (roll <= acc) return profile;
        }

        return K.NONWIN_TARGET_PROFILES[^1];
    }

    private static Dictionary<int, int> ResolveNonWinPrizeTiers(
        MathInput input,
        IReadOnlyDictionary<int, int> nonWinTargets,
        Random rng,
        int planningPressure)
    {
        if (input.NonWinPrizeTiers != null)
            return input.NonWinPrizeTiers.ToDictionary(kv => kv.Key, kv => kv.Value);
        if (planningPressure >= 1)
            return new Dictionary<int, int>();

        var eligible = nonWinTargets
            .Where(kv => kv.Key != TopPrizeSymbol(input))
            .Where(kv => kv.Value >= K.NONWIN_MIN_TARGET && HasUpgradeTier(input, kv.Key, 1))
            .Select(kv => kv.Key)
            .OrderBy(_ => rng.Next())
            .ToArray();

        if (eligible.Length == 0 || rng.NextDouble() >= K.P_NONWIN_PRIZE_UPGRADE)
            return new Dictionary<int, int>();

        return new Dictionary<int, int> { [eligible[0]] = 1 };
    }

    private static bool HasUpgradeTier(MathInput input, int sym, int tier) =>
        input.PrizeValues != null
        && input.PrizeValues.TryGetValue(sym, out var tiers)
        && tiers.ContainsKey(tier);

    private static int TopPrizeSymbol(MathInput input)
    {
        if (input.PrizeValues == null || input.PrizeValues.Count == 0) return 0;
        return input.PrizeValues
            .Select(kv => (Sym: kv.Key, Value: kv.Value.Values.DefaultIfEmpty(0m).Max()))
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Sym)
            .First().Sym;
    }

    private static bool IsHighPressureTicket(MathInput input) =>
        input.Targets.Count >= 4
        || input.Targets.Values.Sum() >= 80
        || input.Required.GetValueOrDefault("PRIZE_UPGRADE") >= 4;
}

internal sealed class FeaturePlanStage
{
    internal FeaturePlan Resolve(ObjectivePlan objectives)
    {
        var input = objectives.SourceInput;
        var log = objectives.Log;
        var required = input.Required.ToDictionary(kv => kv.Key, kv => kv.Value);
        var winTargets = input.Targets.ToDictionary(kv => kv.Key, kv => kv.Value);
        var allocationTargets = winTargets
            .Concat(objectives.NonWinTargets)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var nonWinPrupTokens = objectives.NonWinPrizeTiers.Values.Sum();
        if (nonWinPrupTokens > 0)
            required["PRIZE_UPGRADE"] = required.GetValueOrDefault("PRIZE_UPGRADE") + nonWinPrupTokens;

        var wheelPlan = PlanWheelAndCapacity(objectives, required);
        required = wheelPlan.Required;

        var wheelOrder = wheelPlan.WheelOrder.Count > 0
            ? wheelPlan.WheelOrder
            : input.WheelSymOrder;
        var prizeTiers = MergeTiers(input.PrizeTiers, objectives.NonWinPrizeTiers);

        var schedulingInput = new MathInput
        {
            Targets = allocationTargets,
            BaseSpins = input.BaseSpins,
            Required = required,
            WheelSymOrder = wheelOrder,
            PrizeTiers = prizeTiers.Count > 0 ? prizeTiers : null,
            PrizeValues = input.PrizeValues,
            NonWinTargets = objectives.NonWinTargets,
            NonWinPrizeTiers = objectives.NonWinPrizeTiers,
            MaxSym = input.MaxSym,
        };

        log.Add($"features: {string.Join(",", required.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))}");

        return new FeaturePlan(
            objectives,
            schedulingInput,
            allocationTargets,
            input.PrizeTiers?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<int, int>(),
            ClonePrizeValues(input.PrizeValues));
    }

    private static (Dictionary<string, int> Required, IReadOnlyList<int> WheelOrder) PlanWheelAndCapacity(
        ObjectivePlan objectives,
        Dictionary<string, int> required)
    {
        var input = objectives.SourceInput;
        var rng = objectives.Rng;
        var fillCount = objectives.FillSymbols.Count;
        var plannedFillerLoad = objectives.NonWinTargets.Values.Sum();
        var minWheels = required.GetValueOrDefault("WHEEL");
        var minFlushes = required.GetValueOrDefault("FLUSH");
        var minExtras = required.GetValueOrDefault("EXTRA_SPIN");
        var fixedTokenLoad = RequiredTokenLoad(required) - minWheels - minExtras;
        var plan = ChooseMandatoryFeatureCounts(
            input,
            fillCount,
            plannedFillerLoad,
            fixedTokenLoad,
            minWheels,
            minFlushes,
            minExtras);

        var wheels = plan.Wheels;
        var flushes = plan.Flushes;
        var extras = plan.Extras;
        var wheelOrder = BuildMandatoryWheelOrder(objectives, wheels);

        if (objectives.PlanningPressure == 0)
        {
            foreach (var sym in objectives.WinSymbols
                         .OrderByDescending(s => input.Targets[s])
                         .Where(sym => !wheelOrder.Contains(sym) && input.Targets[sym] >= 10))
            {
                if (wheels >= FeatReg.Cfg["WHEEL"].Max) break;
                if (rng.NextDouble() >= K.P_WHEEL_OPTIONAL) continue;
                if (!IsFeatureShapeFeasible(
                        input,
                        fillCount,
                        plannedFillerLoad,
                        fixedTokenLoad,
                        wheels + 1,
                        flushes,
                        extras))
                {
                    continue;
                }

                wheels++;
                wheelOrder.Add(sym);
            }
        }

        var wheelBudget = FeatReg.Cfg["WHEEL"].Max - wheels;
        if (wheelBudget > 0 && objectives.PlanningPressure == 0)
        {
            foreach (var sym in objectives.NonWinTargets.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Take(1))
            {
                if (rng.NextDouble() >= K.P_NONWIN_WHEEL) continue;
                if (!IsFeatureShapeFeasible(
                        input,
                        fillCount,
                        plannedFillerLoad,
                        fixedTokenLoad,
                        wheels + 1,
                        flushes,
                        extras))
                {
                    continue;
                }

                wheels++;
                wheelOrder.Add(sym);
                break;
            }
        }

        if (objectives.PlanningPressure == 0)
        {
            while (flushes < K.COLS - 1)
            {
                if (rng.NextDouble() >= K.P_FLUSH_OPTIONAL) break;
                if (!IsFeatureShapeFeasible(
                        input,
                        fillCount,
                        plannedFillerLoad,
                        fixedTokenLoad,
                        wheels,
                        flushes + 1,
                        extras))
                {
                    break;
                }

                flushes++;
            }
        }

        SetRequired(required, "WHEEL", wheels);
        SetRequired(required, "FLUSH", flushes);
        SetRequired(required, "EXTRA_SPIN", extras);

        return (required, wheelOrder);
    }

    private static (int Wheels, int Flushes, int Extras) ChooseMandatoryFeatureCounts(
        MathInput input,
        int fillCount,
        int plannedFillerLoad,
        int fixedTokenLoad,
        int minWheels,
        int minFlushes,
        int minExtras)
    {
        var maxWheels = FeatReg.Cfg["WHEEL"].Max;
        var maxFlushes = Math.Min(FeatReg.Cfg["FLUSH"].Max, K.COLS - 1);
        var maxExtras = K.MAX_SPINS - K.BASE_SPINS;
        (int Wheels, int Flushes, int Extras, int Score)? best = null;

        for (var wheels = minWheels; wheels <= maxWheels; wheels++)
        for (var flushes = minFlushes; flushes <= maxFlushes; flushes++)
        for (var extras = minExtras; extras <= maxExtras; extras++)
        {
            if (!IsFeatureShapeFeasible(
                    input,
                    fillCount,
                    plannedFillerLoad,
                    fixedTokenLoad,
                    wheels,
                    flushes,
                    extras))
            {
                continue;
            }

            var physWins = CapacityAnalyzer.PhysicalWins(input.Targets, wheels);
            var plannedLoad = physWins + plannedFillerLoad;
            var tokenLoad = fixedTokenLoad + wheels + extras;
            var fillerBudget = CapacityAnalyzer.FillerBudget(plannedLoad, K.BASE_SPINS + extras, tokenLoad, flushes, wheels);
            var maxFiller = input.MaxSym > 0
                ? Enumerable.Range(1, input.MaxSym)
                    .Except(input.Targets.Keys)
                    .Sum(sym => K.SymbolFillCap(sym) - 2)
                : fillCount * (K.FILL_CAP - 2);
            var lowHeadroomPenalty = Math.Max(0, fillCount - fillerBudget) * 10;
            var highHeadroomPenalty = Math.Max(0, fillerBudget - maxFiller + fillCount) * 10;
            var totalSpins = K.BASE_SPINS + extras;
            var comfortablePushCapacity = totalSpins * K.COLS * 2;
            var pushPressurePenalty = Math.Max(0, plannedLoad + tokenLoad - comfortablePushCapacity) * 500;
            var score = extras * 100
                + wheels * 100
                + flushes * 20
                + pushPressurePenalty
                + lowHeadroomPenalty
                + highHeadroomPenalty;

            if (best == null || score < best.Value.Score)
                best = (wheels, flushes, extras, score);
        }

        if (best == null)
            throw new InvalidOperationException(
                "Could not find a feasible mandatory feature plan for this ticket envelope.");

        return (best.Value.Wheels, best.Value.Flushes, best.Value.Extras);
    }

    private static List<int> BuildMandatoryWheelOrder(ObjectivePlan objectives, int wheels)
    {
        var reliable = objectives.WinSymbols
            .Where(sym => objectives.SourceInput.Targets[sym] < 45)
            .OrderBy(_ => objectives.Rng.Next())
            .ToArray();
        var fallback = objectives.WinSymbols
            .Where(sym => objectives.SourceInput.Targets[sym] >= 45)
            .OrderBy(_ => objectives.Rng.Next())
            .ToArray();
        var order = new List<int>();

        foreach (var sym in reliable)
        {
            if (order.Count >= wheels) return order;
            order.Add(sym);
        }

        foreach (var sym in fallback)
        {
            if (order.Count >= wheels) return order;
            order.Add(sym);
        }

        var repeatIndex = 0;
        while (order.Count < wheels && reliable.Length > 0)
        {
            order.Add(reliable[repeatIndex % reliable.Length]);
            repeatIndex++;
        }

        return order;
    }

    private static bool IsFeatureShapeFeasible(
        MathInput input,
        int fillCount,
        int plannedFillerLoad,
        int fixedTokenLoad,
        int wheels,
        int flushes,
        int extras)
    {
        if (wheels > FeatReg.Cfg["WHEEL"].Max) return false;
        if (flushes > Math.Min(FeatReg.Cfg["FLUSH"].Max, K.COLS - 1)) return false;
        if (extras > K.MAX_SPINS - K.BASE_SPINS) return false;

        var physWins = CapacityAnalyzer.PhysicalWins(input.Targets, wheels);
        var plannedLoad = physWins + plannedFillerLoad;
        var tokenLoad = fixedTokenLoad + wheels + extras;
        var fillSymbols = Enumerable.Range(1, input.MaxSym)
            .Except(input.Targets.Keys)
            .ToArray();
        return CapacityAnalyzer.IsFeasible(
            plannedLoad,
            K.BASE_SPINS + extras,
            fillSymbols,
            tokenLoad,
            flushes,
            wheels);
    }

    private static void SetRequired(Dictionary<string, int> required, string id, int count)
    {
        if (count > 0) required[id] = count;
        else required.Remove(id);
    }

    private static int RequiredTokenLoad(IReadOnlyDictionary<string, int> required) =>
        required.Where(kv => FeatReg.Has(kv.Key) && FeatReg.Get(kv.Key).HasToken)
            .Sum(kv => kv.Value);

    private static Dictionary<int, int> MergeTiers(
        IReadOnlyDictionary<int, int>? prizeTiers,
        IReadOnlyDictionary<int, int> nonWinPrizeTiers)
    {
        var merged = prizeTiers?.ToDictionary(kv => kv.Key, kv => kv.Value)
            ?? new Dictionary<int, int>();
        foreach (var (sym, tier) in nonWinPrizeTiers)
            merged[sym] = tier;
        return merged;
    }

    private static Dictionary<int, IReadOnlyDictionary<int, decimal>> ClonePrizeValues(
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, decimal>>? prizeValues) =>
        prizeValues?.ToDictionary(kv => kv.Key,
            kv => (IReadOnlyDictionary<int, decimal>)kv.Value.ToDictionary(t => t.Key, t => t.Value))
        ?? new Dictionary<int, IReadOnlyDictionary<int, decimal>>();
}

internal sealed class PlacementStage
{
    internal PlacementPlan Resolve(FeaturePlan features)
    {
        var log = features.Objectives.Log;
        var placed = new Placer(features.SchedulingInput, features.Objectives.Rng, log).Place();
        var totalSpins = features.SchedulingInput.BaseSpins + placed.Count(f => f.Id == "EXTRA_SPIN");
        var locks = BuildLocks(placed, features.AllocationTargets, features.Objectives);
        return new PlacementPlan(features, placed, locks, totalSpins);
    }

    private static IReadOnlyList<WLock> BuildLocks(
        IReadOnlyList<PlacedFeat> placed,
        IReadOnlyDictionary<int, int> targets,
        ObjectivePlan objectives)
    {
        var locks = new List<WLock>();
        foreach (var group in placed.Where(f => f.Id == "WHEEL" && f.WSym != 0).GroupBy(f => f.WSym))
        {
            var sym = group.Key;
            var target = targets[sym];
            var ordered = group.OrderBy(f => f.Spin).ToArray();
            foreach (var (feature, index) in ordered.Select((feature, index) => (feature, index)))
            {
                var segmentTarget = ordered.Length == 1
                    ? target
                    : Math.Max(1, (int)Math.Ceiling(target / (double)ordered.Length));
                var remainingTarget = Math.Max(1, target - segmentTarget * index);
                var lockTarget = Math.Min(segmentTarget, remainingTarget);
                var wheelN = feature.WN > 0 ? feature.WN : WMath.BestN(lockTarget);
                var lockPlan = WMath.MakeLock(sym, lockTarget, feature.Spin, wheelN);
                locks.Add(lockPlan);
                objectives.Log.Add($"  WHEEL sym={sym} tgt={lockTarget}/{target} stack={lockPlan.Stack} pre={lockPlan.Pre} post={lockPlan.Post} zone={lockPlan.Zone} @S{feature.Spin}");
            }
        }
        return locks;
    }
}

internal sealed class AllocationStage
{
    internal AllocationPlan Resolve(PlacementPlan placements)
    {
        var features = placements.Features;
        var allocations = new Scheduler(
                features.AllocationTargets,
                placements.PlacedFeatures.ToList(),
                placements.WheelLocks,
                features.Objectives.Log,
                features.Objectives.WinSymbols)
            .Schedule(placements.TotalSpins);
        EnsureFinalWinAllocation(allocations, placements, features.Objectives.WinSymbols, features.Objectives.Log);

        return new AllocationPlan(placements, allocations);
    }

    private static void EnsureFinalWinAllocation(
        IReadOnlyList<Dictionary<int, int>> allocations,
        PlacementPlan placements,
        IReadOnlyList<int> winSymbols,
        List<string> log)
    {
        if (allocations.Count == 0) return;
        var finalSlot = allocations.Count - 1;
        var final = allocations[finalSlot];
        if (winSymbols.Any(sym => final.GetValueOrDefault(sym) > 0)) return;
        if (FinalSlotCapacity(placements, finalSlot) - final.Values.Sum() <= 0) return;

        foreach (var sym in winSymbols.OrderBy(sym => sym))
        {
            for (var slot = finalSlot - 1; slot >= 0; slot--)
            {
                if (!allocations[slot].TryGetValue(sym, out var count) || count <= 0) continue;

                allocations[slot][sym] = count - 1;
                if (allocations[slot][sym] == 0) allocations[slot].Remove(sym);
                final[sym] = final.GetValueOrDefault(sym) + 1;
                log.Add($"finalWinNormalized=sym{sym} moved S{slot + 1}->S{finalSlot + 1}");
                return;
            }
        }
    }

    private static int FinalSlotCapacity(PlacementPlan placements, int finalSlot)
    {
        var spinNum = finalSlot + 1;
        var flushCols = placements.PlacedFeatures.Count(f => f.Id == "FLUSH" && f.Spin == spinNum);
        var wheelSpin = placements.WheelLocks.Any(lockPlan => lockPlan.FireSpin == spinNum);
        var reserved = placements.PlacedFeatures.Count(f => f.Spin == finalSlot
            && FeatReg.Has(f.Id)
            && FeatReg.Get(f.Id).HasToken);
        var freeCols = K.COLS - flushCols;
        var push = wheelSpin ? K.MIN_PUSH : K.MAX_PUSH;
        return Math.Max(0, freeCols * push + flushCols * K.ROWS - reserved);
    }
}

internal sealed class BoardRealizationStage
{
    private readonly IPlanAssemblyPipeline _pipeline;

    internal BoardRealizationStage(IPlanAssemblyPipeline pipeline)
    {
        _pipeline = pipeline;
    }

    internal GamePlan Resolve(AllocationPlan allocation)
    {
        var placements = allocation.Placements;
        var features = placements.Features;
        var objectives = features.Objectives;
        return _pipeline.Assemble(new PlanAssemblyRequest(
            objectives.SourceInput,
            objectives.WinTargets,
            objectives.WinSymbols,
            objectives.FillSymbols,
            objectives.NonWinTargets,
            objectives.NonWinPrizeTiers,
            features.PrizeTiers,
            features.PrizeValues,
            placements.PlacedFeatures,
            placements.WheelLocks,
            allocation.Allocations,
            placements.TotalSpins,
            features.SchedulingInput.BaseSpins,
            new Dictionary<int, int>(),
            objectives.Rng.Next(),
            objectives.Log));
    }
}
