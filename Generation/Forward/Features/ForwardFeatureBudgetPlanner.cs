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
        if (input.BaseSpins != Settings.BASE_SPINS)
        {
            return Fail(
                ForwardFeatureBudgetStatus.InvalidBaseSpins,
                $"BaseSpins={input.BaseSpins}, expected configured base {Settings.BASE_SPINS}");
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
            requiredPrizeUpgradeCount,
            input.LockExtraGoCount);
        wheelCount = capacityExpansion.WheelCount;
        flushCount = capacityExpansion.FlushCount;
        extraGoCount = capacityExpansion.ExtraGoCount;
        if (!input.LockExtraGoCount)
        {
            extraGoCount = Math.Max(
                extraGoCount,
                MinimumExtraGoForVisualBreathing(objectives, input.BaseSpins));
        }

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
            && !input.LockExtraGoCount
            && extraGoCount < MaxExtraGo(input.BaseSpins)
            && rng.NextDouble() < Settings.PNoWinExtraGoOptional)
        {
            extraGoCount++;
            hasOptional = true;
        }

        if (CanAddOptionalFlush(objectives)
            && flushCount < Settings.FlushFeatureConfig.Max
            && rng.NextDouble() < Settings.PFlushOptional)
        {
            flushCount++;
            hasOptional = true;
        }

        var optionalFeatureTicket = rng.NextDouble() < Settings.POptionalFeatureTicket;
        if (!optionalFeatureTicket
            && CanAddOptionalWheel(
                objectives,
                input.BaseSpins,
                wheelCount,
                flushCount,
                extraGoCount,
                requiredPrizeUpgradeCount)
            && rng.NextDouble() < Settings.PWheelOptional)
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
                && rng.NextDouble() < Settings.POptionalTicketWheel)
            {
                wheelCount++;
                hasOptional = true;
            }

            if (CanAddOptionalFlush(objectives)
                && flushCount < Settings.FlushFeatureConfig.Max
                && rng.NextDouble() < Settings.POptionalTicketFlush)
            {
                flushCount++;
                hasOptional = true;
            }

            if (requiredPrizeUpgradeCount + optionalPrizeUpgradeCount < Settings.PrizeUpgradeFeatureConfig.Max
                && CanAddOptionalPrizeUpgrade(objectives)
                && rng.NextDouble() < Settings.POptionalTicketPrizeUpgrade)
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
        if (wheelCount > Settings.WheelFeatureConfig.Max)
            return Exceeds(Wheel, wheelCount, Settings.WheelFeatureConfig.Max);
        if (flushCount > Settings.FlushFeatureConfig.Max)
            return Exceeds(Flush, flushCount, Settings.FlushFeatureConfig.Max);
        if (extraGoCount > MaxExtraGo(baseTurns))
            return Exceeds(ExtraSpin, extraGoCount, MaxExtraGo(baseTurns));
        if (prizeUpgradeCount > Settings.PrizeUpgradeFeatureConfig.Max)
            return Exceeds(PrizeUpgrade, prizeUpgradeCount, Settings.PrizeUpgradeFeatureConfig.Max);

        return Ok();
    }

    private int MaxExtraGo(int baseTurns) =>
        Math.Min(Settings.ExtraSpinFeatureConfig.Max, Settings.MAX_SPINS - baseTurns);

    private int MinimumExtraGoForVisualBreathing(
        ForwardObjectives objectives,
        int baseTurns)
    {
        var requiredCollections = objectives.WinTargets.Values.Sum()
            + objectives.NearMissTargets.Values.Sum();
        if (requiredCollections < MaxPressureVisualThreshold())
            return 0;

        var targetTurns = Math.Min(Settings.MAX_SPINS, Settings.BASE_SPINS + 4);
        return Math.Min(MaxExtraGo(baseTurns), Math.Max(0, targetTurns - baseTurns));
    }

    private int MaxPressureVisualThreshold() =>
        Settings.PrizeLadderRows.Sum(row => row.Target);

    private CapacityExpansion ExpandRequiredCollectionCapacity(
        ForwardObjectives objectives,
        int baseTurns,
        int wheelCount,
        int flushCount,
        int extraGoCount,
        int prizeUpgradeCount,
        bool lockExtraGoCount)
    {
        var requiredCollections = objectives.WinTargets.Values.Sum()
            + objectives.NearMissTargets.Values.Sum();
        if (requiredCollections <= 0)
            return new CapacityExpansion(wheelCount, flushCount, extraGoCount);
        var targetCapacity = requiredCollections + CollectionCapacityReserve(objectives);

        while (CanUseWheelForCapacity(objectives)
            && wheelCount < Settings.WheelFeatureConfig.Max
            && EstimatedCollectionCapacity(
                objectives,
                baseTurns,
                wheelCount,
                flushCount,
                extraGoCount,
                prizeUpgradeCount) < targetCapacity)
        {
            wheelCount++;
        }

        while (CanUseFlushForCapacity(objectives)
            && flushCount < Settings.FlushFeatureConfig.Max
            && EstimatedCollectionCapacity(
                objectives,
                baseTurns,
                wheelCount,
                flushCount,
                extraGoCount,
                prizeUpgradeCount) < targetCapacity)
        {
            flushCount++;
        }

        var maxExtraGo = MaxExtraGo(baseTurns);
        while (!lockExtraGoCount
            && extraGoCount < maxExtraGo
            && EstimatedCollectionCapacity(
                objectives,
                baseTurns,
                wheelCount,
                flushCount,
                extraGoCount,
                prizeUpgradeCount) < targetCapacity)
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
        var startingBoardCapacity = Settings.ROWS * Settings.COLS;
        var nonFinalSpawnTurns = Math.Max(0, totalTurns - 1);
        var pressureTurnCapacity = PressureTurnCapacity(objectives);
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
        _ = objectives;
        _ = wheelCount;
        return 0;
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
        && HasGuaranteedSafeFiller(objectives)
        && objectives.WinTargets.Values.Sum() + objectives.NearMissTargets.Values.Sum() >= 80;

    private static int PureFillerCount(ForwardObjectives objectives) =>
        objectives.FillSymbols.Count(symbol =>
            !objectives.WinTargets.ContainsKey(symbol)
            && !objectives.NearMissTargets.ContainsKey(symbol));

    private static bool CompressionPressure(ForwardObjectives objectives) =>
        objectives.WinTargets.Count > 0
        && objectives.WinTargets.Values.Sum() + objectives.NearMissTargets.Values.Sum() >= 80;

    private int CollectionCapacityReserve(ForwardObjectives objectives) =>
        !HasGuaranteedSafeFiller(objectives) && objectives.NearMissTargets.Count == 0
            ? 0
            : CompressionPressure(objectives)
            ? Settings.COLS * 2
            : Settings.COLS;

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
        if (wheelCount >= Settings.WheelFeatureConfig.Max)
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
                && !Settings.IsFeat(symbol));

    private static bool HasGuaranteedSafeFiller(ForwardObjectives objectives) =>
        objectives.FillSymbols.Any(symbol =>
            !objectives.WinTargets.ContainsKey(symbol)
            && !objectives.NearMissTargets.ContainsKey(symbol));

    private int PressureTurnCapacity(ForwardObjectives objectives)
    {
        if (CompressionPressure(objectives))
            return Math.Min(
                Settings.MAX_PUSH * Settings.COLS,
                Settings.MixedPushCapacity(Settings.COLS) + 3);

        return HasGuaranteedSafeFiller(objectives)
            ? Math.Min(
                Settings.MAX_PUSH * Settings.COLS,
                Settings.MixedPushCapacity(Settings.COLS) + 3)
            : Settings.MixedPushCapacity(Settings.COLS);
    }

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
