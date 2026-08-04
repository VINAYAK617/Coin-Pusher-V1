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
    TicketExperienceProfile ExperienceProfile,
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
        var fillSymbols = Enumerable.Range(1, input.MaxSym)
            .Except(winSymbols)
            .ToArray();
        var nearMissCandidates = fillSymbols;
        var experienceProfile = PickExperienceProfile(request.Rng);

        var nonWinTargets = ResolveNonWinTargets(input, nearMissCandidates, request.Rng, request.PlanningPressure, experienceProfile);
        var nonWinPrizeTiers = ResolveNonWinPrizeTiers(input, nonWinTargets, request.Rng, request.PlanningPressure, experienceProfile);

        request.Log.Add($"experience={experienceProfile} wins=[{string.Join(",", winSymbols)}] fills=[{string.Join(",", fillSymbols)}]");
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
            experienceProfile,
            request.PlanningPressure,
            request.Seed,
            request.Rng,
            request.Log);
    }

    private static Dictionary<int, int> ResolveNonWinTargets(
        MathInput input,
        IReadOnlyList<int> fillSymbols,
        Random rng,
        int planningPressure,
        TicketExperienceProfile experienceProfile)
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

        var maxSymbols = Math.Min(profile.MaxSymbols, NearMissSymbolCap(input, fillSymbols.Count, planningPressure, experienceProfile));
        var count = PickNearMissCount(input, maxSymbols, fillSymbols.Count, planningPressure, rng, experienceProfile);
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

    private static int NearMissSymbolCap(
        MathInput input,
        int fillSymbolCount,
        int planningPressure,
        TicketExperienceProfile experienceProfile)
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

        if (experienceProfile == TicketExperienceProfile.NearMissHeavy && planningPressure == 0)
            byWinCount++;

        return Math.Min(byWinCount, fillSymbolCount);
    }

    private static int PickNearMissCount(
        MathInput input,
        int maxSymbols,
        int fillSymbolCount,
        int planningPressure,
        Random rng,
        TicketExperienceProfile experienceProfile)
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

        if (experienceProfile == TicketExperienceProfile.NearMissHeavy && planningPressure == 0)
            min = Math.Min(capped, min + 1);
        if (experienceProfile == TicketExperienceProfile.FeatureRich && input.Targets.Count > 0)
            min = Math.Max(1, min - 1);

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
        int planningPressure,
        TicketExperienceProfile experienceProfile)
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

        if (eligible.Length == 0
            || rng.NextDouble() >= ProfiledProbability(K.P_NONWIN_PRIZE_UPGRADE, experienceProfile, nearMiss: true))
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

    private static TicketExperienceProfile PickExperienceProfile(Random rng)
    {
        var weighted = new[]
        {
            (Profile: TicketExperienceProfile.Balanced, Weight: K.W_EXP_BALANCED),
            (Profile: TicketExperienceProfile.NearMissHeavy, Weight: K.W_EXP_NEARMISS),
            (Profile: TicketExperienceProfile.FeatureRich, Weight: K.W_EXP_FEATURE),
            (Profile: TicketExperienceProfile.StackDrama, Weight: K.W_EXP_STACK),
            (Profile: TicketExperienceProfile.LateWin, Weight: K.W_EXP_LATEWIN),
        }.Where(item => item.Weight > 0).ToArray();
        if (weighted.Length == 0) return TicketExperienceProfile.Balanced;

        var total = weighted.Sum(item => item.Weight);
        var roll = rng.NextDouble() * total;
        var acc = 0.0;
        foreach (var item in weighted)
        {
            acc += item.Weight;
            if (roll <= acc) return item.Profile;
        }

        return weighted[^1].Profile;
    }

    internal static double ProfiledProbability(
        double baseProbability,
        TicketExperienceProfile profile,
        bool wheel = false,
        bool repeatWheel = false,
        bool flush = false,
        bool nearMiss = false)
    {
        var multiplier = profile switch
        {
            TicketExperienceProfile.FeatureRich when wheel || flush => 1.65,
            TicketExperienceProfile.FeatureRich => 1.35,
            TicketExperienceProfile.StackDrama when wheel || repeatWheel => 1.80,
            TicketExperienceProfile.NearMissHeavy when nearMiss => 1.25,
            TicketExperienceProfile.NearMissHeavy when wheel => 0.85,
            TicketExperienceProfile.LateWin when flush => 0.85,
            TicketExperienceProfile.LateWin => 1.05,
            _ => 1.0,
        };

        return Math.Clamp(baseProbability * multiplier, 0.0, 1.0);
    }
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
                if (rng.NextDouble() >= ObjectiveStage.ProfiledProbability(K.P_WHEEL_OPTIONAL, objectives.ExperienceProfile, wheel: true)) continue;
                if (!IsFeatureShapeFeasible(
                        input,
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

        if (objectives.PlanningPressure == 0 && wheels > 0)
        {
            foreach (var sym in RepeatWheelCandidates(objectives.SourceInput, wheelOrder))
            {
                if (wheels >= FeatReg.Cfg["WHEEL"].Max) break;
                if (rng.NextDouble() >= ObjectiveStage.ProfiledProbability(K.P_WHEEL_REPEAT_OPTIONAL, objectives.ExperienceProfile, repeatWheel: true)) continue;
                if (!IsFeatureShapeFeasible(
                        input,
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
                if (rng.NextDouble() >= ObjectiveStage.ProfiledProbability(K.P_NONWIN_WHEEL, objectives.ExperienceProfile, wheel: true, nearMiss: true)) continue;
                if (!IsFeatureShapeFeasible(
                        input,
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
                if (rng.NextDouble() >= ObjectiveStage.ProfiledProbability(K.P_FLUSH_OPTIONAL, objectives.ExperienceProfile, flush: true)) break;
                if (!IsFeatureShapeFeasible(
                        input,
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

    private static IEnumerable<int> RepeatWheelCandidates(MathInput input, IReadOnlyList<int> wheelOrder)
    {
        if (input.WheelSymOrder == null || input.WheelSymOrder.Count == 0)
            yield break;

        var plannedCounts = wheelOrder.GroupBy(sym => sym)
            .ToDictionary(group => group.Key, group => group.Count());
        foreach (var sym in input.WheelSymOrder.Where(input.Targets.ContainsKey))
        {
            var alreadyPlanned = plannedCounts.GetValueOrDefault(sym);
            var requested = input.WheelSymOrder.Count(candidate => candidate == sym);
            if (alreadyPlanned < requested)
            {
                plannedCounts[sym] = alreadyPlanned + 1;
                yield return sym;
            }
        }
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
        {
            for (var flushes = minFlushes; flushes <= maxFlushes; flushes++)
            {
                for (var extras = minExtras; extras <= maxExtras; extras++)
                {
                    if (!IsFeatureShapeFeasible(
                            input,
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
            }
        }

        if (best == null)
            throw new InvalidOperationException(
                "Could not find a feasible mandatory feature plan for this ticket envelope.");

        return (best.Value.Wheels, best.Value.Flushes, best.Value.Extras);
    }

    private static List<int> BuildMandatoryWheelOrder(ObjectivePlan objectives, int wheels)
    {
        var ordered = objectives.WinSymbols
            .OrderByDescending(sym => objectives.SourceInput.Targets[sym])
            .ThenBy(_ => objectives.Rng.Next())
            .ToArray();
        var order = new List<int>();

        foreach (var sym in ordered)
        {
            if (order.Count >= wheels) return order;
            order.Add(sym);
        }

        var repeatIndex = 0;
        while (order.Count < wheels && ordered.Length > 0)
        {
            order.Add(ordered[repeatIndex % ordered.Length]);
            repeatIndex++;
        }

        return order;
    }

    private static bool IsFeatureShapeFeasible(
        MathInput input,
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
        var placed = new Placer(features.SchedulingInput, features.Objectives.Rng, log, features.Objectives.ExperienceProfile).Place();
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
        var finalAnchorSym = FinalAnchorSymbol(features);
        var allocations = new Scheduler(
                features.AllocationTargets,
                placements.PlacedFeatures.ToList(),
                placements.WheelLocks,
                features.Objectives.Log,
                features.Objectives.WinSymbols,
                finalAnchorSym)
            .Schedule(placements.TotalSpins);
        EnsureFinalWinAllocation(allocations, placements, features.Objectives.WinSymbols, finalAnchorSym, features.Objectives.Log);

        return new AllocationPlan(placements, allocations);
    }

    private static int FinalAnchorSymbol(FeaturePlan features)
    {
        var topPrizeSym = TopPrizeSymbol(features.PrizeValues);
        return topPrizeSym > 0 && features.Objectives.WinSymbols.Contains(topPrizeSym)
            ? topPrizeSym
            : features.Objectives.WinSymbols.OrderBy(sym => sym).FirstOrDefault();
    }

    private static void EnsureFinalWinAllocation(
        IReadOnlyList<Dictionary<int, int>> allocations,
        PlacementPlan placements,
        IReadOnlyList<int> winSymbols,
        int finalAnchorSym,
        List<string> log)
    {
        if (allocations.Count == 0) return;
        var finalSlot = allocations.Count - 1;
        var final = allocations[finalSlot];
        var requiredFinalSym = finalAnchorSym > 0 && winSymbols.Contains(finalAnchorSym)
            ? finalAnchorSym
            : 0;
        if (requiredFinalSym > 0)
        {
            if (final.GetValueOrDefault(requiredFinalSym) > 0) return;
        }
        else if (winSymbols.Any(sym => final.GetValueOrDefault(sym) > 0)) return;

        if (FinalSlotCapacity(placements, finalSlot) - final.Values.Sum() <= 0) return;

        var candidates = requiredFinalSym > 0
            ? new[] { requiredFinalSym }
            : winSymbols.OrderBy(sym => sym).ToArray();
        foreach (var sym in candidates)
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

    private static int TopPrizeSymbol(IReadOnlyDictionary<int, IReadOnlyDictionary<int, decimal>> prizeValues)
    {
        if (prizeValues.Count == 0) return 0;
        return prizeValues
            .Select(kv => (Sym: kv.Key, Value: kv.Value.Values.DefaultIfEmpty(0m).Max()))
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Sym)
            .First().Sym;
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
        return Math.Max(0, K.MixedPushCapacity(freeCols) + flushCols * K.ROWS - reserved);
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
            objectives.ExperienceProfile,
            objectives.Rng.Next(),
            objectives.Log));
    }
}
