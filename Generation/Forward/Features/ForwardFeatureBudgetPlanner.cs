namespace CoinPusherEngine;

internal enum ForwardFeatureBudgetStatus
{
    Valid,
    MissingInput,
    MissingObjectives,
    InvalidBaseSpins,
    UnknownRequiredFeature,
    NegativeRequiredFeature,
    RequiredFeatureExceedsLimit,
    PrizeUpgradeRequirementMismatch,
    WinningPolicyMismatch,
}

internal sealed class ForwardFeatureBudget
{
    internal ForwardFeatureBudget(
        int baseTurns,
        int totalTurns,
        int wheelCount,
        int flushCount,
        int extraGoCount,
        int requiredPrizeUpgradeCount,
        int optionalPrizeUpgradeCount,
        bool hasOptionalFeatures,
        int minimumWheelBonus = 0)
    {
        BaseTurns = baseTurns;
        TotalTurns = totalTurns;
        WheelCount = wheelCount;
        FlushCount = flushCount;
        ExtraGoCount = extraGoCount;
        RequiredPrizeUpgradeCount = requiredPrizeUpgradeCount;
        OptionalPrizeUpgradeCount = optionalPrizeUpgradeCount;
        HasOptionalFeatures = hasOptionalFeatures;
        MinimumWheelBonus = minimumWheelBonus;
        BoardFeatureCounts = new Dictionary<ForwardFeatureKind, int>
        {
            [ForwardFeatureKind.Wheel] = WheelCount,
            [ForwardFeatureKind.ExtraGo] = ExtraGoCount,
            [ForwardFeatureKind.PrizeUpgrade] = PrizeUpgradeCount,
        };
    }

    internal int BaseTurns { get; }
    internal int TotalTurns { get; }
    internal int WheelCount { get; }
    internal int FlushCount { get; }
    internal int ExtraGoCount { get; }
    internal int RequiredPrizeUpgradeCount { get; }
    internal int OptionalPrizeUpgradeCount { get; }
    internal int PrizeUpgradeCount => RequiredPrizeUpgradeCount + OptionalPrizeUpgradeCount;
    internal bool HasOptionalFeatures { get; }
    internal int MinimumWheelBonus { get; }
    internal IReadOnlyDictionary<ForwardFeatureKind, int> BoardFeatureCounts { get; }
}

internal sealed class ForwardFeatureBudgetResult
{
    internal ForwardFeatureBudgetResult(
        ForwardFeatureBudgetStatus status,
        string detail,
        ForwardFeatureBudget? budget)
    {
        Status = status;
        Detail = detail;
        Budget = budget;
    }

    internal ForwardFeatureBudgetStatus Status { get; }
    internal string Detail { get; }
    internal ForwardFeatureBudget? Budget { get; }
    internal bool IsValid => Status == ForwardFeatureBudgetStatus.Valid;
}

internal sealed class ForwardFeatureBudgetPlanner
{
    private const string Wheel = "WHEEL";
    private const string Flush = "FLUSH";
    private const string ExtraSpin = "EXTRA_SPIN";
    private const string PrizeUpgrade = "PRIZE_UPGRADE";

    private readonly ICustomProfileSettings _settings;

    private enum OptionalFeatureChoice
    {
        Wheel,
        Flush,
        ExtraGo,
        PrizeUpgrade,
    }

    internal ForwardFeatureBudgetPlanner(ICustomProfileSettings settings)
    {
        _settings = settings;
    }

    internal ForwardFeatureBudgetResult Plan(
        MathInput? input,
        ForwardObjectives? objectives,
        int seed)
    {
        if (input == null)
            return Fail(ForwardFeatureBudgetStatus.MissingInput, "MathInput is null");
        if (input.Required == null)
            return Fail(ForwardFeatureBudgetStatus.MissingInput, "MathInput.Required is null");
        if (objectives == null)
            return Fail(ForwardFeatureBudgetStatus.MissingObjectives, "forward objectives are missing");
        if (input.BaseSpins != _settings.BASE_SPINS)
        {
            return Fail(
                ForwardFeatureBudgetStatus.InvalidBaseSpins,
                $"BaseSpins={input.BaseSpins}, expected configured base {_settings.BASE_SPINS}");
        }

        var requiredCheck = RequiredCounts(input.Required);
        if (!requiredCheck.IsValid) return requiredCheck.Result!;
        var required = requiredCheck.Counts;

        var declaredPrizeUpgradeSteps = objectives.PrizeTiers.Values.Sum()
            + objectives.NonWinPrizeTiers.Values.Sum();
        if (required.TryGetValue(PrizeUpgrade, out var explicitPrizeUpgrades)
            && explicitPrizeUpgrades != declaredPrizeUpgradeSteps)
        {
            return Fail(
                ForwardFeatureBudgetStatus.PrizeUpgradeRequirementMismatch,
                $"Required[{PrizeUpgrade}]={explicitPrizeUpgrades}, but declared prize tiers need {declaredPrizeUpgradeSteps} token(s)");
        }

        var winningPolicy = objectives.WinningRoundPlan;
        var wheelCount = required.GetValueOrDefault(Wheel);
        var flushCount = required.GetValueOrDefault(Flush);
        var requestedExtraGoCount = required.GetValueOrDefault(ExtraSpin);
        if (winningPolicy != null
            && required.ContainsKey(ExtraSpin)
            && requestedExtraGoCount != winningPolicy.ExtraGoCount)
        {
            return Fail(
                ForwardFeatureBudgetStatus.WinningPolicyMismatch,
                $"Required[{ExtraSpin}]={requestedExtraGoCount}, but winning-round policy requires exactly {winningPolicy.ExtraGoCount}");
        }
        var extraGoCount = winningPolicy?.ExtraGoCount ?? requestedExtraGoCount;
        var requiredPrizeUpgradeCount = declaredPrizeUpgradeSteps;
        var optionalPrizeUpgradeCount = 0;

        var capCheck = CheckCaps(
            wheelCount,
            flushCount,
            extraGoCount,
            requiredPrizeUpgradeCount,
                input.BaseSpins);
        if (!capCheck.IsValid) return capCheck;

        var capacityExpansion = ExpandRequiredCollectionCapacity(
            objectives,
            input.BaseSpins,
            wheelCount,
            flushCount,
            extraGoCount,
            requiredPrizeUpgradeCount,
            allowExtraGoExpansion: winningPolicy == null);
        wheelCount = capacityExpansion.WheelCount;
        flushCount = capacityExpansion.FlushCount;
        extraGoCount = capacityExpansion.ExtraGoCount;

        capCheck = CheckCaps(
            wheelCount,
            flushCount,
            extraGoCount,
            requiredPrizeUpgradeCount,
            input.BaseSpins);
        if (!capCheck.IsValid) return capCheck;

        var rng = new Random(seed);
        var hasOptional = false;
        var optionalFeatureCount = 0;

        if (winningPolicy == null
            && objectives.IsNoWin
            && extraGoCount < MaxExtraGo(input.BaseSpins)
            && rng.NextDouble() < _settings.PNoWinExtraGoOptional)
        {
            extraGoCount++;
            hasOptional = true;
            optionalFeatureCount++;
        }

        if (objectives.IsNoWin
            && CanAddOptionalWheel(
                objectives,
                input.BaseSpins,
                wheelCount,
                flushCount,
                extraGoCount,
                requiredPrizeUpgradeCount + optionalPrizeUpgradeCount)
            && rng.NextDouble() < _settings.PNonWinWheel)
        {
            wheelCount++;
            hasOptional = true;
            optionalFeatureCount++;
        }

        if (CanAddOptionalFlush(objectives)
            && flushCount < _settings.FlushFeatureConfig.Max
            && rng.NextDouble() < _settings.PFlushOptional)
        {
            flushCount++;
            hasOptional = true;
            optionalFeatureCount++;
        }

        var optionalFeatureTicket = rng.NextDouble() < _settings.POptionalFeatureTicket;
        if (!optionalFeatureTicket
            && !objectives.IsNoWin
            && CanAddOptionalWheel(
                objectives,
                input.BaseSpins,
                wheelCount,
                flushCount,
                extraGoCount,
                requiredPrizeUpgradeCount)
            && rng.NextDouble() < _settings.PWheelOptional)
        {
            wheelCount++;
            hasOptional = true;
            optionalFeatureCount++;
        }

        if (optionalFeatureTicket)
        {
            var ticketWheelChance = objectives.IsNoWin
                ? 0.0
                : _settings.POptionalTicketWheel;
            if (CanAddOptionalWheel(
                    objectives,
                    input.BaseSpins,
                    wheelCount,
                    flushCount,
                    extraGoCount,
                    requiredPrizeUpgradeCount + optionalPrizeUpgradeCount)
                && rng.NextDouble() < ticketWheelChance)
            {
                wheelCount++;
                hasOptional = true;
                optionalFeatureCount++;
            }

            if (CanAddOptionalFlush(objectives)
                && flushCount < _settings.FlushFeatureConfig.Max
                && rng.NextDouble() < _settings.POptionalTicketFlush)
            {
                flushCount++;
                hasOptional = true;
                optionalFeatureCount++;
            }

            var ticketPrizeUpgradeChance = objectives.IsNoWin
                ? _settings.PNonWinPrizeUpgrade
                : _settings.POptionalTicketPrizeUpgrade;
            if (requiredPrizeUpgradeCount + optionalPrizeUpgradeCount < _settings.PrizeUpgradeFeatureConfig.Max
                && CanAddOptionalPrizeUpgrade(objectives, optionalPrizeUpgradeCount)
                && rng.NextDouble() < ticketPrizeUpgradeChance)
            {
                optionalPrizeUpgradeCount++;
                hasOptional = true;
                optionalFeatureCount++;
            }
        }

        AddMultipleOptionalFeatures(
            objectives,
            input.BaseSpins,
            requiredPrizeUpgradeCount,
            rng,
            allowOptionalExtraGo: winningPolicy == null,
            ref wheelCount,
            ref flushCount,
            ref extraGoCount,
            ref optionalPrizeUpgradeCount,
            ref hasOptional,
            ref optionalFeatureCount);

        wheelCount = AddRepeatOptionalWheels(
            objectives,
            input.BaseSpins,
            wheelCount,
            flushCount,
            extraGoCount,
            requiredPrizeUpgradeCount + optionalPrizeUpgradeCount,
            rng,
            ref hasOptional,
            ref optionalFeatureCount);

        var totalTurns = input.BaseSpins + extraGoCount;
        if (winningPolicy != null && totalTurns != winningPolicy.TotalTurns)
        {
            return Fail(
                ForwardFeatureBudgetStatus.WinningPolicyMismatch,
                $"feature budget produced {totalTurns} turns, winning-round policy requires {winningPolicy.TotalTurns}");
        }
        var minimumWheelBonus = MinimumRequiredWheelBonus(
            objectives,
            input.BaseSpins,
            flushCount,
            extraGoCount,
            requiredPrizeUpgradeCount + optionalPrizeUpgradeCount);
        return new ForwardFeatureBudgetResult(
            ForwardFeatureBudgetStatus.Valid,
            "ok",
            new ForwardFeatureBudget(
                input.BaseSpins,
                totalTurns,
                wheelCount,
                flushCount,
                extraGoCount,
                requiredPrizeUpgradeCount,
                optionalPrizeUpgradeCount,
                hasOptional,
                minimumWheelBonus));
    }

    private void AddMultipleOptionalFeatures(
        ForwardObjectives objectives,
        int baseTurns,
        int requiredPrizeUpgradeCount,
        Random rng,
        bool allowOptionalExtraGo,
        ref int wheelCount,
        ref int flushCount,
        ref int extraGoCount,
        ref int optionalPrizeUpgradeCount,
        ref bool hasOptional,
        ref int optionalFeatureCount)
    {
        if (!HasWinOrNearMissObjective(objectives)
            || rng.NextDouble() >= MultipleOptionalFeatureChance())
        {
            return;
        }

        const int targetOptionalFeatures = 2;
        for (var guard = 0; optionalFeatureCount < targetOptionalFeatures && guard < 8; guard++)
        {
            var candidates = OptionalFeatureCandidates(
                objectives,
                baseTurns,
                wheelCount,
                flushCount,
                extraGoCount,
                requiredPrizeUpgradeCount,
                optionalPrizeUpgradeCount,
                allowOptionalExtraGo);
            if (candidates.Count == 0)
                return;

            switch (PickOptionalFeature(candidates, rng))
            {
                case OptionalFeatureChoice.Wheel:
                    wheelCount++;
                    break;
                case OptionalFeatureChoice.Flush:
                    flushCount++;
                    break;
                case OptionalFeatureChoice.ExtraGo:
                    extraGoCount++;
                    break;
                case OptionalFeatureChoice.PrizeUpgrade:
                    optionalPrizeUpgradeCount++;
                    break;
            }

            hasOptional = true;
            optionalFeatureCount++;
        }
    }

    private IReadOnlyList<(OptionalFeatureChoice Choice, double Weight)> OptionalFeatureCandidates(
        ForwardObjectives objectives,
        int baseTurns,
        int wheelCount,
        int flushCount,
        int extraGoCount,
        int requiredPrizeUpgradeCount,
        int optionalPrizeUpgradeCount,
        bool allowOptionalExtraGo)
    {
        var candidates = new List<(OptionalFeatureChoice Choice, double Weight)>();

        var wheelWeight = objectives.IsNoWin
            ? 0.0
            : _settings.POptionalTicketWheel;
        if (wheelWeight > 0
            && CanAddOptionalWheel(
                objectives,
                baseTurns,
                wheelCount,
                flushCount,
                extraGoCount,
                requiredPrizeUpgradeCount + optionalPrizeUpgradeCount))
        {
            candidates.Add((OptionalFeatureChoice.Wheel, wheelWeight));
        }

        if (_settings.POptionalTicketFlush > 0
            && CanAddOptionalFlush(objectives)
            && flushCount < _settings.FlushFeatureConfig.Max)
        {
            candidates.Add((OptionalFeatureChoice.Flush, _settings.POptionalTicketFlush));
        }

        var extraGoWeight = objectives.IsNoWin
            ? _settings.PNoWinExtraGoOptional
            : _settings.ExtraSpinFeatureConfig.P;
        if (allowOptionalExtraGo
            && extraGoWeight > 0
            && extraGoCount < MaxExtraGo(baseTurns))
        {
            candidates.Add((OptionalFeatureChoice.ExtraGo, extraGoWeight));
        }

        var prizeUpgradeWeight = objectives.IsNoWin
            ? _settings.PNonWinPrizeUpgrade
            : _settings.POptionalTicketPrizeUpgrade;
        if (prizeUpgradeWeight > 0
            && requiredPrizeUpgradeCount + optionalPrizeUpgradeCount < _settings.PrizeUpgradeFeatureConfig.Max
            && CanAddOptionalPrizeUpgrade(objectives, optionalPrizeUpgradeCount))
        {
            candidates.Add((OptionalFeatureChoice.PrizeUpgrade, prizeUpgradeWeight));
        }

        return candidates;
    }

    private static OptionalFeatureChoice PickOptionalFeature(
        IReadOnlyList<(OptionalFeatureChoice Choice, double Weight)> candidates,
        Random rng)
    {
        var total = candidates.Sum(candidate => candidate.Weight);
        var roll = rng.NextDouble() * total;
        var acc = 0.0;
        foreach (var candidate in candidates)
        {
            acc += candidate.Weight;
            if (roll <= acc) return candidate.Choice;
        }

        return candidates[^1].Choice;
    }

    private static bool HasWinOrNearMissObjective(ForwardObjectives objectives) =>
        objectives.WinTargets.Count > 0 || objectives.NearMissTargets.Count > 0;

    private double MultipleOptionalFeatureChance() =>
        Math.Max(0.0, Math.Min(1.0, _settings.WExpFeature));

    private int AddRepeatOptionalWheels(
        ForwardObjectives objectives,
        int baseTurns,
        int wheelCount,
        int flushCount,
        int extraGoCount,
        int prizeUpgradeCount,
        Random rng,
        ref bool hasOptional,
        ref int optionalFeatureCount)
    {
        if (objectives.IsNoWin || wheelCount <= 0)
            return wheelCount;

        while (CanAddOptionalWheel(
                   objectives,
                   baseTurns,
                   wheelCount,
                   flushCount,
                   extraGoCount,
                   prizeUpgradeCount)
               && rng.NextDouble() < _settings.PWheelRepeatOptional)
        {
            wheelCount++;
            hasOptional = true;
            optionalFeatureCount++;
        }

        return wheelCount;
    }

    private RequiredCountResult RequiredCounts(IReadOnlyDictionary<string, int> required)
    {
        var counts = new Dictionary<string, int>();
        foreach (var (feature, count) in required)
        {
            if (!KnownFeature(feature))
            {
                return RequiredCountResult.Fail(Fail(
                    ForwardFeatureBudgetStatus.UnknownRequiredFeature,
                    $"unknown required feature '{feature}'"));
            }

            if (count < 0)
            {
                return RequiredCountResult.Fail(Fail(
                    ForwardFeatureBudgetStatus.NegativeRequiredFeature,
                    $"required feature '{feature}' has negative count {count}"));
            }

            counts[feature] = count;
        }

        return RequiredCountResult.Ok(counts);
    }

    private ForwardFeatureBudgetResult CheckCaps(
        int wheelCount,
        int flushCount,
        int extraGoCount,
        int prizeUpgradeCount,
        int baseTurns)
    {
        if (wheelCount > _settings.WheelFeatureConfig.Max)
            return Exceeds(Wheel, wheelCount, _settings.WheelFeatureConfig.Max);
        if (flushCount > _settings.FlushFeatureConfig.Max)
            return Exceeds(Flush, flushCount, _settings.FlushFeatureConfig.Max);
        if (extraGoCount > MaxExtraGo(baseTurns))
            return Exceeds(ExtraSpin, extraGoCount, MaxExtraGo(baseTurns));
        if (prizeUpgradeCount > _settings.PrizeUpgradeFeatureConfig.Max)
            return Exceeds(PrizeUpgrade, prizeUpgradeCount, _settings.PrizeUpgradeFeatureConfig.Max);

        return Ok();
    }

    private int MaxExtraGo(int baseTurns) =>
        Math.Min(_settings.ExtraSpinFeatureConfig.Max, _settings.MAX_SPINS - baseTurns);

    private CapacityExpansion ExpandRequiredCollectionCapacity(
        ForwardObjectives objectives,
        int baseTurns,
        int wheelCount,
        int flushCount,
        int extraGoCount,
        int prizeUpgradeCount,
        bool allowExtraGoExpansion)
    {
        var requiredCollections = objectives.WinTargets.Values.Sum()
            + objectives.NearMissTargets.Values.Sum();
        if (requiredCollections <= 0)
            return new CapacityExpansion(wheelCount, flushCount, extraGoCount);

        var maxExtraGo = MaxExtraGo(baseTurns);
        while (allowExtraGoExpansion
            && extraGoCount < maxExtraGo
            && EstimatedCollectionCapacity(
                objectives,
                baseTurns,
                wheelCount,
                flushCount,
                extraGoCount,
                prizeUpgradeCount) < requiredCollections)
        {
            extraGoCount++;
        }

        while (CanUseWheelForCapacity(objectives)
            && wheelCount < _settings.WheelFeatureConfig.Max
            && EstimatedCollectionCapacity(
                objectives,
                baseTurns,
                wheelCount,
                flushCount,
                extraGoCount,
                prizeUpgradeCount) < requiredCollections)
        {
            wheelCount++;
        }

        while (CanUseFlushForCapacity(objectives)
            && flushCount < _settings.FlushFeatureConfig.Max
            && EstimatedCollectionCapacity(
                objectives,
                baseTurns,
                wheelCount,
                flushCount,
                extraGoCount,
                prizeUpgradeCount) < requiredCollections)
        {
            flushCount++;
        }

        return new CapacityExpansion(wheelCount, flushCount, extraGoCount);
    }

    private int EstimatedCollectionCapacity(
        ForwardObjectives objectives,
        int baseTurns,
        int wheelCount,
        int flushCount,
        int extraGoCount,
        int prizeUpgradeCount)
    {
        var totalTurns = baseTurns + extraGoCount;
        var plannedTurnCapacity = VariedPusherCapacity(totalTurns);
        var flushBonus = flushCount * Math.Max(0, _settings.ROWS - _settings.MAX_PUSH);
        var wheelBonus = EstimatedWheelBonus(objectives, wheelCount);
        _ = prizeUpgradeCount;

        return Math.Max(0, plannedTurnCapacity + flushBonus + wheelBonus);
    }

    private int MinimumRequiredWheelBonus(
        ForwardObjectives objectives,
        int baseTurns,
        int flushCount,
        int extraGoCount,
        int prizeUpgradeCount)
    {
        var requiredCollections = objectives.WinTargets.Values.Sum()
            + objectives.NearMissTargets.Values.Sum();
        var capacityWithoutWheel = EstimatedCollectionCapacity(
            objectives,
            baseTurns,
            wheelCount: 0,
            flushCount,
            extraGoCount,
            prizeUpgradeCount);
        return Math.Max(0, requiredCollections - capacityWithoutWheel);
    }

    private int VariedPusherCapacity(int totalTurns)
    {
        var min = _settings.MIN_PUSH * _settings.COLS;
        var max = _settings.MAX_PUSH * _settings.COLS;
        // Public validation permits the same unordered pusher bag at most twice.
        // Capping each pop total twice is conservative and keeps capacity feasible.
        return Enumerable.Range(min, max - min + 1)
            .OrderByDescending(value => value)
            .SelectMany(value => new[] { value, value })
            .Take(Math.Max(0, totalTurns))
            .Sum();
    }

    private int EstimatedWheelBonus(ForwardObjectives objectives, int wheelCount)
    {
        if (wheelCount <= 0 || objectives.WinTargets.Count == 0)
            return 0;

        var stack = Math.Min(_settings.MAX_COIN_STACK, Math.Min(_settings.MAX_WHEEL_STACK_VALUE, 3) + 1);
        if (stack <= 1)
            return 0;

        return objectives.WinTargets
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Take(wheelCount)
            .Sum(kv =>
            {
                var zone = Math.Max(0, Math.Min(kv.Value / stack, _settings.COLS - 1) - 1);
                return zone * (stack - 1);
            });
    }

    private static bool CanAddOptionalPrizeUpgrade(
        ForwardObjectives objectives,
        int optionalPrizeUpgradeCount) =>
        optionalPrizeUpgradeCount < AvailableOptionalPrizeUpgradeSteps(objectives);

    private static int AvailableOptionalPrizeUpgradeSteps(ForwardObjectives objectives) =>
        objectives.NearMissTargets.Keys.Sum(symbol =>
        {
            var currentTier = objectives.NonWinPrizeTiers.GetValueOrDefault(symbol);
            if (!objectives.PrizeValues.TryGetValue(symbol, out var tiers))
                return 0;

            var highestTier = tiers.Keys
                .Where(tier => tier > currentTier)
                .DefaultIfEmpty(currentTier)
                .Max();
            return Math.Max(0, highestTier - currentTier);
        });

    private static bool CanUseWheelForCapacity(ForwardObjectives objectives) =>
        objectives.WinTargets.Count > 0
        && objectives.WinTargets.Values.Sum() + objectives.NearMissTargets.Values.Sum() >= 80;

    private static int PureFillerCount(ForwardObjectives objectives) =>
        objectives.FillSymbols.Count(symbol =>
            !objectives.WinTargets.ContainsKey(symbol)
            && !objectives.NearMissTargets.ContainsKey(symbol));

    private static bool CompressionPressure(ForwardObjectives objectives) =>
        objectives.WinTargets.Count > 0
        && objectives.WinTargets.Values.Sum() + objectives.NearMissTargets.Values.Sum() >= 80;

    private static bool CanUseFlushForCapacity(ForwardObjectives objectives)
    {
        if (CompressionPressure(objectives))
            return true;
        if (!HasGuaranteedSafeFiller(objectives))
            return false;
        if (ThinHighPressureFillerMargin(objectives))
            return false;

        return true;
    }

    private static bool CanAddOptionalFlush(ForwardObjectives objectives)
    {
        if (!HasGuaranteedSafeFiller(objectives))
            return false;
        if (ThinHighPressureFillerMargin(objectives))
            return false;

        return true;
    }

    private bool CanAddOptionalWheel(
        ForwardObjectives objectives,
        int baseTurns,
        int wheelCount,
        int flushCount,
        int extraGoCount,
        int prizeUpgradeCount)
    {
        if (wheelCount >= _settings.WheelFeatureConfig.Max)
            return false;
        if (!HasOptionalWheelTarget(objectives))
            return false;

        var requiredCollections = objectives.WinTargets.Values.Sum()
            + objectives.NearMissTargets.Values.Sum();
        var capacityWithWheel = EstimatedCollectionCapacity(
            objectives,
            baseTurns,
            wheelCount + 1,
            flushCount,
            extraGoCount,
            prizeUpgradeCount);

        return capacityWithWheel >= requiredCollections;
    }

    private static bool ThinHighPressureFillerMargin(ForwardObjectives objectives) =>
        PureFillerCount(objectives) < 2
        && CompressionPressure(objectives);

    private bool HasOptionalWheelTarget(ForwardObjectives objectives) =>
        objectives.FillSymbols
            .Where(symbol => !objectives.WinTargets.ContainsKey(symbol))
            .Where(symbol => !objectives.NearMissTargets.ContainsKey(symbol))
            .Any(symbol => symbol >= 1
                && symbol <= objectives.MaxSymbol
                && !_settings.IsFeat(symbol));

    private static bool HasGuaranteedSafeFiller(ForwardObjectives objectives) =>
        objectives.FillSymbols.Any(symbol =>
            !objectives.WinTargets.ContainsKey(symbol)
            && !objectives.NearMissTargets.ContainsKey(symbol));

    private static bool KnownFeature(string feature) =>
        feature == Wheel
        || feature == Flush
        || feature == ExtraSpin
        || feature == PrizeUpgrade;

    private static ForwardFeatureBudgetResult Exceeds(string feature, int count, int max) =>
        Fail(
            ForwardFeatureBudgetStatus.RequiredFeatureExceedsLimit,
            $"required {feature} count {count} exceeds configured max {max}");

    private static ForwardFeatureBudgetResult Ok() =>
        new(ForwardFeatureBudgetStatus.Valid, "ok", null);

    private static ForwardFeatureBudgetResult Fail(ForwardFeatureBudgetStatus status, string detail) =>
        new(status, detail, null);

    private sealed class RequiredCountResult
    {
        private RequiredCountResult(
            Dictionary<string, int> counts,
            ForwardFeatureBudgetResult? result)
        {
            Counts = counts;
            Result = result;
        }

        internal Dictionary<string, int> Counts { get; }
        internal ForwardFeatureBudgetResult? Result { get; }
        internal bool IsValid => Result == null;

        internal static RequiredCountResult Ok(Dictionary<string, int> counts) =>
            new(counts, null);

        internal static RequiredCountResult Fail(ForwardFeatureBudgetResult result) =>
            new(new Dictionary<string, int>(), result);
    }

    private readonly struct CapacityExpansion
    {
        internal CapacityExpansion(
            int wheelCount,
            int flushCount,
            int extraGoCount)
        {
            WheelCount = wheelCount;
            FlushCount = flushCount;
            ExtraGoCount = extraGoCount;
        }

        internal int WheelCount { get; }
        internal int FlushCount { get; }
        internal int ExtraGoCount { get; }
    }
}
