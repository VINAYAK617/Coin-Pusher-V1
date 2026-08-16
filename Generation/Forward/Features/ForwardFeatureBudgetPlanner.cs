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
        bool hasOptionalFeatures)
    {
        BaseTurns = baseTurns;
        TotalTurns = totalTurns;
        WheelCount = wheelCount;
        FlushCount = flushCount;
        ExtraGoCount = extraGoCount;
        RequiredPrizeUpgradeCount = requiredPrizeUpgradeCount;
        OptionalPrizeUpgradeCount = optionalPrizeUpgradeCount;
        HasOptionalFeatures = hasOptionalFeatures;
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

        var wheelCount = required.GetValueOrDefault(Wheel);
        var flushCount = required.GetValueOrDefault(Flush);
        var extraGoCount = required.GetValueOrDefault(ExtraSpin);
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
            requiredPrizeUpgradeCount);
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

        if (objectives.IsNoWin
            && extraGoCount < MaxExtraGo(input.BaseSpins)
            && rng.NextDouble() < _settings.PNoWinExtraGoOptional)
        {
            extraGoCount++;
            hasOptional = true;
        }

        if (CanAddOptionalFlush(objectives)
            && flushCount < _settings.FlushFeatureConfig.Max
            && rng.NextDouble() < _settings.PFlushOptional)
        {
            flushCount++;
            hasOptional = true;
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
        }

        if (optionalFeatureTicket)
        {
            if (CanAddOptionalWheel(
                    objectives,
                    input.BaseSpins,
                    wheelCount,
                    flushCount,
                    extraGoCount,
                    requiredPrizeUpgradeCount + optionalPrizeUpgradeCount)
                && rng.NextDouble() < _settings.POptionalTicketWheel)
            {
                wheelCount++;
                hasOptional = true;
            }

            if (CanAddOptionalFlush(objectives)
                && flushCount < _settings.FlushFeatureConfig.Max
                && rng.NextDouble() < _settings.POptionalTicketFlush)
            {
                flushCount++;
                hasOptional = true;
            }

            if (requiredPrizeUpgradeCount + optionalPrizeUpgradeCount < _settings.PrizeUpgradeFeatureConfig.Max
                && CanAddOptionalPrizeUpgrade(objectives)
                && rng.NextDouble() < _settings.POptionalTicketPrizeUpgrade)
            {
                optionalPrizeUpgradeCount++;
                hasOptional = true;
            }
        }

        var totalTurns = input.BaseSpins + extraGoCount;
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
                hasOptional));
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
        int prizeUpgradeCount)
    {
        var requiredCollections = objectives.WinTargets.Values.Sum()
            + objectives.NearMissTargets.Values.Sum();
        if (requiredCollections <= 0)
            return new CapacityExpansion(wheelCount, flushCount, extraGoCount);

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

        var maxExtraGo = MaxExtraGo(baseTurns);
        while (extraGoCount < maxExtraGo
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
        var startingBoardCapacity = _settings.ROWS * _settings.COLS;
        var nonFinalSpawnTurns = Math.Max(0, totalTurns - 1);
        var pressureTurnCapacity = CanUseDenseCollectionCapacity(objectives)
            ? Math.Min(
                _settings.MAX_PUSH * _settings.COLS,
                _settings.MixedPushCapacity(_settings.COLS) + 3)
            : _settings.MixedPushCapacity(_settings.COLS);
        var plannedTurnCapacity = nonFinalSpawnTurns * pressureTurnCapacity;
        var flushBonus = flushCount;
        var wheelBonus = EstimatedWheelBonus(objectives, wheelCount);
        var boardFeatureCount = wheelCount + extraGoCount + prizeUpgradeCount;
        var featureReserve = boardFeatureCount * 2;

        return Math.Max(
            0,
            startingBoardCapacity + plannedTurnCapacity + flushBonus + wheelBonus - featureReserve);
    }

    private int EstimatedWheelBonus(ForwardObjectives objectives, int wheelCount)
    {
        if (wheelCount <= 0 || objectives.WinTargets.Count == 0)
            return 0;

        var stack = Math.Min(_settings.MAX_COIN_STACK, _settings.MAX_WHEEL_STACK_VALUE + 1);
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

    private static bool CanAddOptionalPrizeUpgrade(ForwardObjectives objectives) =>
        objectives.NearMissTargets.Keys.Any(symbol =>
        {
            var currentTier = objectives.NonWinPrizeTiers.GetValueOrDefault(symbol);
            return objectives.PrizeValues.TryGetValue(symbol, out var tiers)
                && tiers.ContainsKey(currentTier + 1);
        });

    private static bool CanUseWheelForCapacity(ForwardObjectives objectives) =>
        objectives.WinTargets.Count > 0
        && PureFillerCount(objectives) >= 2
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

    private static bool CanUseDenseCollectionCapacity(ForwardObjectives objectives) =>
        HasGuaranteedSafeFiller(objectives);

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
