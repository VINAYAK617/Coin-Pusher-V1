namespace CoinPusherEngine;

internal enum ForwardFeatureIntentStatus
{
    Valid,
    MissingObjectives,
    MissingTiming,
    InvalidTimedFeature,
    FeatureOnFinalTurn,
    NoWheelTargetSymbol,
    InvalidWheelStackConfig,
    WheelCapacityUnsatisfied,
    OptionalPrizeUpgradeNoTarget,
    PrizeUpgradeNoEligibleSymbol,
    PrizeUpgradeInvalid,
    PrizeUpgradeFinalMismatch,
    PrizeValueMissing,
}

internal sealed class ForwardFeatureIntent
{
    private ForwardFeatureIntent(
        ForwardTimedFeatureKind kind,
        int turn,
        int? wheelSymbol,
        int? wheelStackValue,
        bool isCapacityRequiredWheel,
        int plannedWheelCollectionBonus,
        int? upgradeSymbol,
        int? upgradeTier,
        decimal? upgradePrizeValue)
    {
        Kind = kind;
        Turn = turn;
        WheelSymbol = wheelSymbol;
        WheelStackValue = wheelStackValue;
        IsCapacityRequiredWheel = isCapacityRequiredWheel;
        PlannedWheelCollectionBonus = plannedWheelCollectionBonus;
        UpgradeSymbol = upgradeSymbol;
        UpgradeTier = upgradeTier;
        UpgradePrizeValue = upgradePrizeValue;
    }

    internal ForwardTimedFeatureKind Kind { get; }
    internal int Turn { get; }
    internal int? WheelSymbol { get; }
    internal int? WheelStackValue { get; }
    internal bool IsCapacityRequiredWheel { get; }
    internal int PlannedWheelCollectionBonus { get; }
    internal int? ResultingWheelStack => WheelStackValue.HasValue ? WheelStackValue.Value + 1 : null;
    internal int? UpgradeSymbol { get; }
    internal int? UpgradeTier { get; }
    internal decimal? UpgradePrizeValue { get; }
    internal bool IsBoardFeature =>
        Kind == ForwardTimedFeatureKind.Wheel
        || Kind == ForwardTimedFeatureKind.ExtraGo
        || Kind == ForwardTimedFeatureKind.PrizeUpgrade;

    internal static ForwardFeatureIntent Wheel(
        int turn,
        int symbol,
        int stackValue,
        bool isCapacityRequired = false,
        int plannedCollectionBonus = 0) =>
        new(
            ForwardTimedFeatureKind.Wheel,
            turn,
            symbol,
            stackValue,
            isCapacityRequired,
            plannedCollectionBonus,
            null,
            null,
            null);

    internal static ForwardFeatureIntent Flush(int turn) =>
        new(ForwardTimedFeatureKind.Flush, turn, null, null, false, 0, null, null, null);

    internal static ForwardFeatureIntent ExtraGo(int turn) =>
        new(ForwardTimedFeatureKind.ExtraGo, turn, null, null, false, 0, null, null, null);

    internal static ForwardFeatureIntent PrizeUpgrade(
        int turn,
        int symbol,
        int tier,
        decimal prizeValue) =>
        new(ForwardTimedFeatureKind.PrizeUpgrade, turn, null, null, false, 0, symbol, tier, prizeValue);
}

internal sealed class ForwardFeatureIntentPlan
{
    internal ForwardFeatureIntentPlan(
        int totalTurns,
        IReadOnlyList<ForwardFeatureIntent> intents,
        IReadOnlyDictionary<int, int> effectivePrizeTiers,
        IReadOnlyDictionary<int, int> effectiveNonWinPrizeTiers)
    {
        TotalTurns = totalTurns;
        Intents = intents;
        EffectivePrizeTiers = effectivePrizeTiers;
        EffectiveNonWinPrizeTiers = effectiveNonWinPrizeTiers;
        ByTurn = intents
            .GroupBy(intent => intent.Turn)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ForwardFeatureIntent>)group
                    .OrderBy(intent => intent.Kind)
                    .ToArray());
    }

    internal int TotalTurns { get; }
    internal IReadOnlyList<ForwardFeatureIntent> Intents { get; }
    internal IReadOnlyDictionary<int, IReadOnlyList<ForwardFeatureIntent>> ByTurn { get; }
    internal IReadOnlyDictionary<int, int> EffectivePrizeTiers { get; }
    internal IReadOnlyDictionary<int, int> EffectiveNonWinPrizeTiers { get; }

    internal IReadOnlyList<ForwardFeatureIntent> BoardIntentsForTurn(int turn) =>
        ByTurn.TryGetValue(turn, out var list)
            ? list.Where(intent => intent.IsBoardFeature).ToArray()
            : Array.Empty<ForwardFeatureIntent>();
}

internal sealed class ForwardFeatureIntentResult
{
    internal ForwardFeatureIntentResult(
        ForwardFeatureIntentStatus status,
        string detail,
        ForwardFeatureIntentPlan? plan)
    {
        Status = status;
        Detail = detail;
        Plan = plan;
    }

    internal ForwardFeatureIntentStatus Status { get; }
    internal string Detail { get; }
    internal ForwardFeatureIntentPlan? Plan { get; }
    internal bool IsValid => Status == ForwardFeatureIntentStatus.Valid;
}

internal sealed class ForwardFeatureIntentPlanner
{
    private const int MaxPublicWheelStackValue = 3;

    private readonly ICustomProfileSettings _settings;
    private readonly Random _rng;

    internal ForwardFeatureIntentPlanner(ICustomProfileSettings settings, int seed)
    {
        _settings = settings;
        _rng = new Random(seed);
    }

    internal ForwardFeatureIntentResult Plan(
        ForwardObjectives? objectives,
        ForwardFeatureTiming? timing,
        ForwardFeatureBudget? budget = null)
    {
        if (objectives == null)
            return Fail(ForwardFeatureIntentStatus.MissingObjectives, "forward objectives are missing");
        if (timing == null)
            return Fail(ForwardFeatureIntentStatus.MissingTiming, "feature timing is missing");

        var effectivePrizeTiers = objectives.PrizeTiers.ToDictionary(kv => kv.Key, kv => kv.Value);
        var effectiveNonWinPrizeTiers = objectives.NonWinPrizeTiers.ToDictionary(kv => kv.Key, kv => kv.Value);

        var optional = AddOptionalPrizeUpgradeTargets(
            objectives,
            timing,
            effectivePrizeTiers,
            effectiveNonWinPrizeTiers);
        if (!optional.IsValid) return optional;

        var targetTiers = effectivePrizeTiers
            .Concat(effectiveNonWinPrizeTiers)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        var upgradeLedger = new ForwardPrizeUpgradeLedger(
            targetTiers,
            objectives.PrizeValues,
            objectives.MaxSymbol,
            _settings);
        var intents = new List<ForwardFeatureIntent>();
        var plannedWheelSymbols = new List<int>();
        var plannedWheelBonus = 0;
        var totalWheelCount = timing.Count(ForwardTimedFeatureKind.Wheel);
        var minimumWheelBonus = Math.Max(0, budget?.MinimumWheelBonus ?? 0);

        foreach (var turnGroup in timing.Events
                     .OrderBy(feature => feature.Turn)
                     .ThenBy(feature => feature.Kind)
                     .GroupBy(feature => feature.Turn))
        {
            if (turnGroup.Key <= 0 || turnGroup.Key > timing.TotalTurns)
                return Fail(ForwardFeatureIntentStatus.InvalidTimedFeature, $"feature turn {turnGroup.Key} is outside 1..{timing.TotalTurns}");
            if (turnGroup.Key >= timing.TotalTurns)
                return Fail(ForwardFeatureIntentStatus.FeatureOnFinalTurn, $"feature scheduled on final turn {timing.TotalTurns}");

            var upgradedThisTurn = new HashSet<int>();
            foreach (var timedFeature in turnGroup)
            {
                var intent = timedFeature.Kind switch
                {
                    ForwardTimedFeatureKind.Wheel => BuildWheelIntent(
                        timedFeature.Turn,
                        objectives,
                        plannedWheelSymbols,
                        totalWheelCount - plannedWheelSymbols.Count,
                        Math.Max(0, minimumWheelBonus - plannedWheelBonus)),
                    ForwardTimedFeatureKind.Flush => IntentBuildResult.Ok(ForwardFeatureIntent.Flush(timedFeature.Turn)),
                    ForwardTimedFeatureKind.ExtraGo => IntentBuildResult.Ok(ForwardFeatureIntent.ExtraGo(timedFeature.Turn)),
                    ForwardTimedFeatureKind.PrizeUpgrade => BuildPrizeUpgradeIntent(
                        timedFeature.Turn,
                        targetTiers,
                        objectives,
                        upgradeLedger,
                        upgradedThisTurn),
                    _ => IntentBuildResult.Fail(Fail(ForwardFeatureIntentStatus.InvalidTimedFeature, $"unknown timed feature kind {timedFeature.Kind}")),
                };

                if (!intent.IsValid) return intent.Result!;
                var builtIntent = intent.Intent!;
                intents.Add(builtIntent);
                if (builtIntent.Kind == ForwardTimedFeatureKind.Wheel
                    && builtIntent.WheelSymbol.HasValue)
                {
                    plannedWheelSymbols.Add(builtIntent.WheelSymbol.Value);
                    plannedWheelBonus += WheelCapacityBonus(
                        builtIntent.WheelSymbol.Value,
                        builtIntent.WheelStackValue!.Value,
                        objectives);
                }
            }
        }

        if (plannedWheelBonus < minimumWheelBonus)
        {
            return Fail(
                ForwardFeatureIntentStatus.WheelCapacityUnsatisfied,
                $"planned WHEEL bonus={plannedWheelBonus}, required minimum={minimumWheelBonus}");
        }

        var finalFailures = upgradeLedger.ValidateFinal();
        if (finalFailures.Count > 0)
        {
            var failure = finalFailures[0];
            return Fail(ForwardFeatureIntentStatus.PrizeUpgradeFinalMismatch, failure.Detail);
        }

        return new ForwardFeatureIntentResult(
            ForwardFeatureIntentStatus.Valid,
            "ok",
            new ForwardFeatureIntentPlan(
                timing.TotalTurns,
                intents
                    .OrderBy(intent => intent.Turn)
                    .ThenBy(intent => intent.Kind)
                    .ToArray(),
                effectivePrizeTiers,
                effectiveNonWinPrizeTiers));
    }

    private ForwardFeatureIntentResult AddOptionalPrizeUpgradeTargets(
        ForwardObjectives objectives,
        ForwardFeatureTiming timing,
        Dictionary<int, int> effectivePrizeTiers,
        Dictionary<int, int> effectiveNonWinPrizeTiers)
    {
        var timedPrizeUpgradeCount = timing.Count(ForwardTimedFeatureKind.PrizeUpgrade);
        var declaredCount = effectivePrizeTiers.Values.Sum() + effectiveNonWinPrizeTiers.Values.Sum();
        var optionalCount = timedPrizeUpgradeCount - declaredCount;
        if (optionalCount < 0)
        {
            return Fail(
                ForwardFeatureIntentStatus.PrizeUpgradeFinalMismatch,
                $"timing has {timedPrizeUpgradeCount} PRIZE_UPGRADE feature(s), but declarations require {declaredCount}");
        }

        var allocation = PickOptionalPrizeUpgradeSymbols(
            objectives,
            optionalCount,
            effectiveNonWinPrizeTiers,
            timing.TurnsFor(ForwardTimedFeatureKind.PrizeUpgrade)
                .GroupBy(turn => turn)
                .Any(group => group.Count() > 1));
        if (allocation.Count != optionalCount)
        {
            return Fail(
                ForwardFeatureIntentStatus.OptionalPrizeUpgradeNoTarget,
                "optional PRIZE_UPGRADE needs existing near-miss symbol(s) with configured next prize tiers");
        }

        foreach (var symbol in allocation)
            effectiveNonWinPrizeTiers[symbol] = effectiveNonWinPrizeTiers.GetValueOrDefault(symbol) + 1;

        return Ok();
    }

    private IReadOnlyList<int> PickOptionalPrizeUpgradeSymbols(
        ForwardObjectives objectives,
        int optionalCount,
        IReadOnlyDictionary<int, int> startingNonWinPrizeTiers,
        bool requireSpread)
    {
        var allocation = new List<int>();
        if (optionalCount <= 0) return allocation;

        var workingTiers = startingNonWinPrizeTiers.ToDictionary(kv => kv.Key, kv => kv.Value);
        var stackStyle = !requireSpread
            && ShouldStackOptionalPrizeUpgrades(objectives, optionalCount, workingTiers);

        if (stackStyle)
            AddStackedOptionalPrizeUpgrades(objectives, optionalCount, workingTiers, allocation);

        while (allocation.Count < optionalCount)
        {
            var symbol = PickSpreadOptionalPrizeUpgradeSymbol(objectives, workingTiers);
            if (symbol <= 0) break;

            allocation.Add(symbol);
            workingTiers[symbol] = workingTiers.GetValueOrDefault(symbol) + 1;
        }

        return allocation;
    }

    private bool ShouldStackOptionalPrizeUpgrades(
        ForwardObjectives objectives,
        int optionalCount,
        IReadOnlyDictionary<int, int> workingTiers)
    {
        if (optionalCount < 2) return false;

        var candidates = OptionalPrizeUpgradeCandidates(objectives, workingTiers);
        if (candidates.Count == 0) return false;
        if (candidates.Count == 1) return true;

        return _rng.NextDouble() < Math.Max(0.0, Math.Min(1.0, _settings.WExpStack));
    }

    private void AddStackedOptionalPrizeUpgrades(
        ForwardObjectives objectives,
        int optionalCount,
        Dictionary<int, int> workingTiers,
        List<int> allocation)
    {
        var symbol = PickStackedOptionalPrizeUpgradeSymbol(objectives, workingTiers);
        while (symbol > 0 && allocation.Count < optionalCount && CanAdvanceOptionalPrizeUpgrade(objectives, symbol, workingTiers))
        {
            allocation.Add(symbol);
            workingTiers[symbol] = workingTiers.GetValueOrDefault(symbol) + 1;
        }
    }

    private int PickStackedOptionalPrizeUpgradeSymbol(
        ForwardObjectives objectives,
        IReadOnlyDictionary<int, int> effectiveNonWinPrizeTiers)
    {
        var candidates = OptionalPrizeUpgradeCandidates(objectives, effectiveNonWinPrizeTiers);
        if (candidates.Count == 0) return 0;

        var bestRemaining = candidates.Max(candidate => candidate.RemainingSteps);
        return PickWeightedOptionalPrizeUpgradeSymbol(
            candidates.Where(candidate => candidate.RemainingSteps == bestRemaining).ToArray());
    }

    private int PickSpreadOptionalPrizeUpgradeSymbol(
        ForwardObjectives objectives,
        IReadOnlyDictionary<int, int> effectiveNonWinPrizeTiers)
    {
        var candidates = OptionalPrizeUpgradeCandidates(objectives, effectiveNonWinPrizeTiers);
        if (candidates.Count == 0) return 0;

        var lowestTier = candidates.Min(candidate => candidate.CurrentTier);
        return PickWeightedOptionalPrizeUpgradeSymbol(
            candidates.Where(candidate => candidate.CurrentTier == lowestTier).ToArray());
    }

    private IReadOnlyList<(int Symbol, int CurrentTier, int RemainingSteps, int NearMissTarget)> OptionalPrizeUpgradeCandidates(
        ForwardObjectives objectives,
        IReadOnlyDictionary<int, int> effectiveNonWinPrizeTiers) =>
        objectives.NearMissTargets.Keys
            .Select(symbol => OptionalPrizeUpgradeCandidate(objectives, effectiveNonWinPrizeTiers, symbol))
            .Where(candidate => candidate.RemainingSteps > 0)
            .OrderByDescending(candidate => candidate.NearMissTarget)
            .ThenBy(candidate => candidate.Symbol)
            .ToArray();

    private static (int Symbol, int CurrentTier, int RemainingSteps, int NearMissTarget) OptionalPrizeUpgradeCandidate(
        ForwardObjectives objectives,
        IReadOnlyDictionary<int, int> effectiveNonWinPrizeTiers,
        int symbol)
    {
        var currentTier = effectiveNonWinPrizeTiers.GetValueOrDefault(symbol);
        if (!objectives.PrizeValues.TryGetValue(symbol, out var tiers))
            return (symbol, currentTier, 0, objectives.NearMissTargets.GetValueOrDefault(symbol));

        var highestTier = tiers.Keys
            .Where(tier => tier > currentTier)
            .DefaultIfEmpty(currentTier)
            .Max();
        return (
            symbol,
            currentTier,
            Math.Max(0, highestTier - currentTier),
            objectives.NearMissTargets.GetValueOrDefault(symbol));
    }

    private static bool CanAdvanceOptionalPrizeUpgrade(
        ForwardObjectives objectives,
        int symbol,
        IReadOnlyDictionary<int, int> effectiveNonWinPrizeTiers) =>
        OptionalPrizeUpgradeCandidate(objectives, effectiveNonWinPrizeTiers, symbol).RemainingSteps > 0;

    private int PickWeightedOptionalPrizeUpgradeSymbol(
        IReadOnlyList<(int Symbol, int CurrentTier, int RemainingSteps, int NearMissTarget)> candidates)
    {
        if (candidates.Count == 0) return 0;

        var weighted = candidates
            .Select(candidate => (candidate.Symbol, Weight: Math.Max(1.0, candidate.NearMissTarget)))
            .ToArray();
        var total = weighted.Sum(candidate => candidate.Weight);
        var roll = _rng.NextDouble() * total;
        var acc = 0.0;
        foreach (var candidate in weighted)
        {
            acc += candidate.Weight;
            if (roll <= acc) return candidate.Symbol;
        }

        return weighted[^1].Symbol;
    }

    private IntentBuildResult BuildWheelIntent(
        int turn,
        ForwardObjectives objectives,
        IReadOnlyList<int> plannedWheelSymbols,
        int remainingWheelCount,
        int remainingRequiredBonus)
    {
        if (WheelSymbolCandidates(objectives).Count == 0)
            return IntentBuildResult.Fail(Fail(ForwardFeatureIntentStatus.NoWheelTargetSymbol, "no normal symbol is available for WHEEL"));

        var options = WheelOptions(objectives);
        if (options.Count == 0)
            return IntentBuildResult.Fail(Fail(ForwardFeatureIntentStatus.InvalidWheelStackConfig, "no weighted legal WHEEL stack value is configured"));

        var futureWheelCount = Math.Max(0, remainingWheelCount - 1);
        var maxFutureBonus = futureWheelCount * options.Max(option => option.Bonus);
        var feasible = options
            .Where(option => option.Bonus + maxFutureBonus >= remainingRequiredBonus)
            .ToArray();
        if (feasible.Length == 0)
        {
            return IntentBuildResult.Fail(Fail(
                ForwardFeatureIntentStatus.WheelCapacityUnsatisfied,
                $"remaining WHEEL bonus={remainingRequiredBonus} cannot be satisfied by {remainingWheelCount} WHEEL event(s)"));
        }

        var repeatSymbols = plannedWheelSymbols.ToHashSet();
        var repeated = feasible
            .Where(option => repeatSymbols.Contains(option.Symbol))
            .ToArray();
        if (repeated.Length > 0 && _rng.NextDouble() < _settings.PWheelRepeatOptional)
        {
            feasible = repeated;
        }

        var selected = PickWeightedWheelOption(feasible);
        return IntentBuildResult.Ok(ForwardFeatureIntent.Wheel(
            turn,
            selected.Symbol,
            selected.StackValue,
            isCapacityRequired: remainingRequiredBonus > 0 && selected.Bonus > 0,
            plannedCollectionBonus: selected.Bonus));
    }

    private IReadOnlyList<int> WheelSymbolCandidates(ForwardObjectives objectives)
    {
        return objectives.FillSymbols
            .Concat(objectives.NearMissTargets.Keys)
            .Concat(objectives.WinSymbols)
            .Where(symbol => symbol >= 1 && symbol <= objectives.MaxSymbol && !_settings.IsFeat(symbol))
            .Distinct()
            .OrderBy(symbol => symbol)
            .ToArray();
    }

    private IReadOnlyList<WheelOption> WheelOptions(ForwardObjectives objectives)
    {
        var maxValue = Math.Min(
            Math.Min(_settings.MAX_WHEEL_STACK_VALUE, _settings.MAX_COIN_STACK - 1),
            MaxPublicWheelStackValue);
        var minValue = Math.Max(1, _settings.MIN_WHEEL_STACK_VALUE);
        if (minValue > maxValue) return Array.Empty<WheelOption>();

        return WheelSymbolCandidates(objectives)
            .SelectMany(symbol => Enumerable.Range(minValue, maxValue - minValue + 1)
                .Select(stackValue => new WheelOption(
                    symbol,
                    stackValue,
                    WheelCapacityBonus(symbol, stackValue, objectives),
                    WheelSymbolWeight(symbol, objectives) * WheelStackWeight(stackValue))))
            .ToArray();
    }

    private WheelOption PickWeightedWheelOption(IReadOnlyList<WheelOption> options)
    {
        if (options.Count == 0)
            throw new InvalidOperationException("WHEEL option selection requires at least one option");

        var total = options.Sum(item => item.Weight);
        if (total <= 0)
            return options[_rng.Next(options.Count)];

        var roll = _rng.NextDouble() * total;
        var acc = 0.0;
        foreach (var item in options)
        {
            acc += item.Weight;
            if (roll <= acc) return item;
        }

        return options[^1];
    }

    private double WheelSymbolWeight(int symbol, ForwardObjectives objectives)
    {
        var weight = 1.0;
        if (objectives.NearMissTargets.TryGetValue(symbol, out var nearMissTarget))
            weight += 5.0 + (nearMissTarget / 8.0);
        else if (!objectives.WinTargets.ContainsKey(symbol))
            weight += 3.0;
        if (objectives.WinTargets.TryGetValue(symbol, out var winTarget))
            weight += 0.25 + (winTarget / 60.0);
        if (symbol == objectives.TopPrizeSymbol && symbol > 0)
            weight += 1.0;
        return weight;
    }

    private double WheelStackWeight(int value) =>
        value switch
        {
            1 => _settings.PWheelStackValue1,
            2 => _settings.PWheelStackValue2,
            _ => Math.Max(0.0, 1.0 - _settings.PWheelStackValue1 - _settings.PWheelStackValue2),
        };

    private int WheelCapacityBonus(
        int symbol,
        int stackValue,
        ForwardObjectives objectives)
    {
        if (!objectives.WinTargets.TryGetValue(symbol, out var target))
            return 0;

        var stack = Math.Min(_settings.MAX_COIN_STACK, stackValue + 1);
        if (stack <= 1) return 0;

        var zone = Math.Max(0, Math.Min(target / stack, _settings.COLS - 1) - 1);
        return zone * (stack - 1);
    }

    private readonly struct WheelOption
    {
        internal WheelOption(int symbol, int stackValue, int bonus, double weight)
        {
            Symbol = symbol;
            StackValue = stackValue;
            Bonus = bonus;
            Weight = weight;
        }

        internal int Symbol { get; }
        internal int StackValue { get; }
        internal int Bonus { get; }
        internal double Weight { get; }
    }

    private IntentBuildResult BuildPrizeUpgradeIntent(
        int turn,
        IReadOnlyDictionary<int, int> targetTiers,
        ForwardObjectives objectives,
        ForwardPrizeUpgradeLedger ledger,
        HashSet<int> upgradedThisTurn)
    {
        var symbol = PickPrizeUpgradeSymbol(targetTiers, ledger, upgradedThisTurn);
        if (symbol <= 0)
        {
            return IntentBuildResult.Fail(Fail(
                ForwardFeatureIntentStatus.PrizeUpgradeNoEligibleSymbol,
                $"no PRIZE_UPGRADE target can advance on turn {turn} without repeating a symbol in the same turn"));
        }

        var nextTier = ledger.CurrentTier(symbol) + 1;
        if (!objectives.PrizeValues.TryGetValue(symbol, out var tiers)
            || !tiers.TryGetValue(nextTier, out var prizeValue))
        {
            return IntentBuildResult.Fail(Fail(
                ForwardFeatureIntentStatus.PrizeValueMissing,
                $"symbol {symbol} has no prize value for tier {nextTier}"));
        }

        var check = ledger.ApplyUpgrade(symbol, nextTier);
        if (!check.IsValid)
            return IntentBuildResult.Fail(Fail(ForwardFeatureIntentStatus.PrizeUpgradeInvalid, check.Detail));

        upgradedThisTurn.Add(symbol);
        return IntentBuildResult.Ok(ForwardFeatureIntent.PrizeUpgrade(turn, symbol, nextTier, prizeValue));
    }

    private int PickPrizeUpgradeSymbol(
        IReadOnlyDictionary<int, int> targetTiers,
        ForwardPrizeUpgradeLedger ledger,
        HashSet<int> upgradedThisTurn)
    {
        var candidates = targetTiers
            .Where(kv => ledger.CurrentTier(kv.Key) < kv.Value)
            .Where(kv => !upgradedThisTurn.Contains(kv.Key))
            .Select(kv => (Symbol: kv.Key, Remaining: kv.Value - ledger.CurrentTier(kv.Key)))
            .OrderByDescending(item => item.Remaining)
            .ThenBy(item => item.Symbol)
            .ToArray();
        if (candidates.Length == 0) return 0;

        var bestRemaining = candidates[0].Remaining;
        var best = candidates
            .Where(item => item.Remaining == bestRemaining)
            .ToArray();
        return best[_rng.Next(best.Length)].Symbol;
    }

    private static ForwardFeatureIntentResult Ok() =>
        new(ForwardFeatureIntentStatus.Valid, "ok", null);

    private static ForwardFeatureIntentResult Fail(ForwardFeatureIntentStatus status, string detail) =>
        new(status, detail, null);

    private sealed class IntentBuildResult
    {
        private IntentBuildResult(
            ForwardFeatureIntent? intent,
            ForwardFeatureIntentResult? result)
        {
            Intent = intent;
            Result = result;
        }

        internal ForwardFeatureIntent? Intent { get; }
        internal ForwardFeatureIntentResult? Result { get; }
        internal bool IsValid => Result == null;

        internal static IntentBuildResult Ok(ForwardFeatureIntent intent) =>
            new(intent, null);

        internal static IntentBuildResult Fail(ForwardFeatureIntentResult result) =>
            new(null, result);
    }
}
