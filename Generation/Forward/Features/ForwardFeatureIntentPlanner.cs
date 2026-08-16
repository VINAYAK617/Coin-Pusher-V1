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
        int? upgradeSymbol,
        int? upgradeTier,
        decimal? upgradePrizeValue)
    {
        Kind = kind;
        Turn = turn;
        WheelSymbol = wheelSymbol;
        WheelStackValue = wheelStackValue;
        UpgradeSymbol = upgradeSymbol;
        UpgradeTier = upgradeTier;
        UpgradePrizeValue = upgradePrizeValue;
    }

    internal ForwardTimedFeatureKind Kind { get; }
    internal int Turn { get; }
    internal int? WheelSymbol { get; }
    internal int? WheelStackValue { get; }
    internal int? ResultingWheelStack => WheelStackValue.HasValue ? WheelStackValue.Value + 1 : null;
    internal int? UpgradeSymbol { get; }
    internal int? UpgradeTier { get; }
    internal decimal? UpgradePrizeValue { get; }
    internal bool IsBoardFeature =>
        Kind == ForwardTimedFeatureKind.Wheel
        || Kind == ForwardTimedFeatureKind.ExtraGo
        || Kind == ForwardTimedFeatureKind.PrizeUpgrade;

    internal static ForwardFeatureIntent Wheel(int turn, int symbol, int stackValue) =>
        new(ForwardTimedFeatureKind.Wheel, turn, symbol, stackValue, null, null, null);

    internal static ForwardFeatureIntent Flush(int turn) =>
        new(ForwardTimedFeatureKind.Flush, turn, null, null, null, null, null);

    internal static ForwardFeatureIntent ExtraGo(int turn) =>
        new(ForwardTimedFeatureKind.ExtraGo, turn, null, null, null, null, null);

    internal static ForwardFeatureIntent PrizeUpgrade(
        int turn,
        int symbol,
        int tier,
        decimal prizeValue) =>
        new(ForwardTimedFeatureKind.PrizeUpgrade, turn, null, null, symbol, tier, prizeValue);
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
    private readonly ICustomProfileSettings _settings;
    private readonly Random _rng;

    internal ForwardFeatureIntentPlanner(ICustomProfileSettings settings, int seed)
    {
        _settings = settings;
        _rng = new Random(seed);
    }

    internal ForwardFeatureIntentResult Plan(
        ForwardObjectives? objectives,
        ForwardFeatureTiming? timing)
    {
        if (objectives == null)
            return Fail(ForwardFeatureIntentStatus.MissingObjectives, "forward objectives are missing");
        if (timing == null)
            return Fail(ForwardFeatureIntentStatus.MissingTiming, "feature timing is missing");

        var effectivePrizeTiers = objectives.PrizeTiers.ToDictionary(kv => kv.Key, kv => kv.Value);
        var effectiveNonWinPrizeTiers = objectives.NonWinPrizeTiers.ToDictionary(kv => kv.Key, kv => kv.Value);

        var optional = AddOptionalPrizeUpgradeTargets(
            objectives,
            timing.Count(ForwardTimedFeatureKind.PrizeUpgrade),
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
                    ForwardTimedFeatureKind.Wheel => BuildWheelIntent(timedFeature.Turn, objectives),
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
                intents.Add(intent.Intent!);
            }
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
        int timedPrizeUpgradeCount,
        Dictionary<int, int> effectivePrizeTiers,
        Dictionary<int, int> effectiveNonWinPrizeTiers)
    {
        var declaredCount = effectivePrizeTiers.Values.Sum() + effectiveNonWinPrizeTiers.Values.Sum();
        var optionalCount = timedPrizeUpgradeCount - declaredCount;
        if (optionalCount < 0)
        {
            return Fail(
                ForwardFeatureIntentStatus.PrizeUpgradeFinalMismatch,
                $"timing has {timedPrizeUpgradeCount} PRIZE_UPGRADE feature(s), but declarations require {declaredCount}");
        }

        for (var i = 0; i < optionalCount; i++)
        {
            var symbol = PickOptionalPrizeUpgradeSymbol(objectives, effectiveNonWinPrizeTiers);
            if (symbol <= 0)
            {
                return Fail(
                    ForwardFeatureIntentStatus.OptionalPrizeUpgradeNoTarget,
                    "optional PRIZE_UPGRADE needs an existing near-miss symbol with a configured next prize tier");
            }

            effectiveNonWinPrizeTiers[symbol] = effectiveNonWinPrizeTiers.GetValueOrDefault(symbol) + 1;
        }

        return Ok();
    }

    private int PickOptionalPrizeUpgradeSymbol(
        ForwardObjectives objectives,
        IReadOnlyDictionary<int, int> effectiveNonWinPrizeTiers)
    {
        var candidates = objectives.NearMissTargets.Keys
            .Where(symbol => objectives.PrizeValues.TryGetValue(symbol, out var tiers)
                && tiers.ContainsKey(effectiveNonWinPrizeTiers.GetValueOrDefault(symbol) + 1))
            .OrderByDescending(symbol => objectives.NearMissTargets[symbol])
            .ThenBy(symbol => symbol)
            .ToArray();

        return candidates.Length == 0 ? 0 : candidates[_rng.Next(candidates.Length)];
    }

    private IntentBuildResult BuildWheelIntent(
        int turn,
        ForwardObjectives objectives)
    {
        var symbol = PickWheelSymbol(objectives);
        if (symbol <= 0)
            return IntentBuildResult.Fail(Fail(ForwardFeatureIntentStatus.NoWheelTargetSymbol, "no normal symbol is available for WHEEL"));

        var stackValue = PickWheelStackValue(objectives);
        if (stackValue <= 0)
            return IntentBuildResult.Fail(Fail(ForwardFeatureIntentStatus.InvalidWheelStackConfig, "no legal WHEEL stack value is configured"));

        return IntentBuildResult.Ok(ForwardFeatureIntent.Wheel(turn, symbol, stackValue));
    }

    private int PickWheelSymbol(ForwardObjectives objectives)
    {
        if (ShouldCompressWithWheel(objectives))
        {
            var winCandidates = objectives.WinTargets
                .Where(kv => kv.Key >= 1 && kv.Key <= objectives.MaxSymbol && !_settings.IsFeat(kv.Key))
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key)
                .Select(kv => kv.Key)
                .ToArray();
            if (winCandidates.Length > 0)
                return PickWeightedWheelSymbol(winCandidates, objectives);
        }

        var pureFiller = objectives.FillSymbols
            .Where(symbol => !objectives.WinTargets.ContainsKey(symbol))
            .Where(symbol => !objectives.NearMissTargets.ContainsKey(symbol))
            .Where(symbol => symbol >= 1 && symbol <= objectives.MaxSymbol && !_settings.IsFeat(symbol))
            .Distinct()
            .OrderBy(symbol => symbol)
            .ToArray();
        if (pureFiller.Length > 0)
            return PickWeightedWheelSymbol(pureFiller, objectives);

        var nonWinning = objectives.NearMissTargets.Keys
            .Concat(objectives.FillSymbols)
            .Where(symbol => !objectives.WinTargets.ContainsKey(symbol))
            .Where(symbol => symbol >= 1 && symbol <= objectives.MaxSymbol && !_settings.IsFeat(symbol))
            .Distinct()
            .OrderBy(symbol => symbol)
            .ToArray();
        var candidates = nonWinning.Length > 0
            ? nonWinning
            : objectives.WinSymbols
                .Where(symbol => symbol >= 1 && symbol <= objectives.MaxSymbol && !_settings.IsFeat(symbol))
                .Distinct()
                .OrderBy(symbol => symbol)
                .ToArray();
        if (candidates.Length == 0) return 0;

        return PickWeightedWheelSymbol(candidates, objectives);
    }

    private int PickWeightedWheelSymbol(
        IReadOnlyList<int> candidates,
        ForwardObjectives objectives)
    {
        var weighted = candidates
            .Select(symbol => (Symbol: symbol, Weight: WheelSymbolWeight(symbol, objectives)))
            .Where(item => item.Weight > 0)
            .ToArray();
        if (weighted.Length == 0) return candidates[_rng.Next(candidates.Count)];

        var total = weighted.Sum(item => item.Weight);
        var roll = _rng.NextDouble() * total;
        var acc = 0.0;
        foreach (var item in weighted)
        {
            acc += item.Weight;
            if (roll <= acc) return item.Symbol;
        }

        return weighted[^1].Symbol;
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

    private int PickWheelStackValue(ForwardObjectives objectives)
    {
        var maxValue = Math.Min(_settings.MAX_WHEEL_STACK_VALUE, _settings.MAX_COIN_STACK - 1);
        var minValue = Math.Max(1, _settings.MIN_WHEEL_STACK_VALUE);
        if (minValue > maxValue) return 0;
        if (ShouldCompressWithWheel(objectives))
            return maxValue;

        var values = Enumerable.Range(minValue, maxValue - minValue + 1)
            .Select(value => (Value: value, Weight: WheelStackWeight(value)))
            .Where(item => item.Weight > 0)
            .ToArray();
        if (values.Length == 0) return 0;

        var total = values.Sum(item => item.Weight);
        var roll = _rng.NextDouble() * total;
        var acc = 0.0;
        foreach (var item in values)
        {
            acc += item.Weight;
            if (roll <= acc) return item.Value;
        }

        return values[^1].Value;
    }

    private double WheelStackWeight(int value) =>
        value switch
        {
            1 => _settings.PWheelStackValue1,
            2 => _settings.PWheelStackValue2,
            _ => Math.Max(0.0, 1.0 - _settings.PWheelStackValue1 - _settings.PWheelStackValue2),
        };

    private static bool ShouldCompressWithWheel(ForwardObjectives objectives) =>
        objectives.WinTargets.Count > 0
        && objectives.FillSymbols.Count(symbol =>
            !objectives.WinTargets.ContainsKey(symbol)
            && !objectives.NearMissTargets.ContainsKey(symbol)) >= 2
        && objectives.WinTargets.Values.Sum() + objectives.NearMissTargets.Values.Sum() >= 80;

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
