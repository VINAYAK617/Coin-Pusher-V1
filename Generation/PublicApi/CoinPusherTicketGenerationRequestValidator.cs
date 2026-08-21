namespace CoinPusherEngine;

using GameEngine;

public enum CoinPusherTicketGenerationRequestStatus
{
    Valid,
    MissingPrizeAmounts,
    NegativePrizeAmount,
    ZeroPrizeAmount,
    InvalidGridShape,
    InvalidPushRange,
    InvalidSpinRange,
    InvalidStackRange,
    MissingPrizeLadder,
    InvalidPrizeLadderRow,
    InvalidFeatureIds,
    InvalidFeatureConfig,
    InvalidWinningRoundConfig,
}

public sealed class CoinPusherTicketGenerationRequestValidationResult
{
    internal CoinPusherTicketGenerationRequestValidationResult(
        CoinPusherTicketGenerationRequestStatus status,
        string detail)
    {
        Status = status;
        Detail = detail;
    }

    public CoinPusherTicketGenerationRequestStatus Status { get; }
    public string Detail { get; }
    public bool IsValid => Status == CoinPusherTicketGenerationRequestStatus.Valid;
}

public sealed class CoinPusherTicketGenerationRequestValidator
{
    private const int MaxPublicWheelStackValue = 3;

    private readonly ICustomProfileSettings _settings;

    public CoinPusherTicketGenerationRequestValidator()
        : this(Settings)
    {
    }

    internal CoinPusherTicketGenerationRequestValidator(ICustomProfileSettings settings)
    {
        _settings = settings;
    }

    public CoinPusherTicketGenerationRequestValidationResult Validate(
        IReadOnlyList<decimal>? prizeAmounts)
    {
        var settingsCheck = ValidateSettings();
        if (!settingsCheck.IsValid)
            return settingsCheck;

        if (prizeAmounts == null)
            return Fail(CoinPusherTicketGenerationRequestStatus.MissingPrizeAmounts, "prize amount list is null");

        var negative = prizeAmounts.FirstOrDefault(amount => amount < 0m);
        if (negative < 0m)
            return Fail(CoinPusherTicketGenerationRequestStatus.NegativePrizeAmount, $"prize amount {negative} cannot be negative");

        if (prizeAmounts.Any(amount => amount == 0m))
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.ZeroPrizeAmount,
                "0-win tickets must pass an empty prize list, not a 0 prize amount");
        }

        return new CoinPusherTicketGenerationRequestValidationResult(
            CoinPusherTicketGenerationRequestStatus.Valid,
            "ok");
    }

    private CoinPusherTicketGenerationRequestValidationResult ValidateSettings()
    {
        if (_settings.ROWS <= 0 || _settings.COLS <= 0)
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidGridShape,
                $"grid must be positive, found ROWS={_settings.ROWS}, COLS={_settings.COLS}");
        }
        if (_settings.ROWS != _settings.COLS)
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidGridShape,
                $"Coin Pusher rotation requires a square grid, found ROWS={_settings.ROWS}, COLS={_settings.COLS}");
        }

        if (_settings.MIN_PUSH < 1 || _settings.MAX_PUSH < _settings.MIN_PUSH || _settings.MAX_PUSH >= _settings.ROWS)
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidPushRange,
                $"normal push range must be 1..ROWS-1, found MIN_PUSH={_settings.MIN_PUSH}, MAX_PUSH={_settings.MAX_PUSH}, ROWS={_settings.ROWS}");
        }

        if (_settings.BASE_SPINS < 1 || _settings.MAX_SPINS < _settings.BASE_SPINS)
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidSpinRange,
                $"spin range invalid, found BASE_SPINS={_settings.BASE_SPINS}, MAX_SPINS={_settings.MAX_SPINS}");
        }
        if (_settings.MaxPlanAttempts < 1 || _settings.LocalRealizationAttempts < 1)
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidFeatureConfig,
                $"attempt limits must be positive, found MaxPlanAttempts={_settings.MaxPlanAttempts}, " +
                $"LocalRealizationAttempts={_settings.LocalRealizationAttempts}");
        }
        if (_settings.FILL_CAP <= 1 || _settings.NONWIN_MIN_TARGET < 1)
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidPrizeLadderRow,
                $"filler/non-win limits invalid, found FILL_CAP={_settings.FILL_CAP}, NONWIN_MIN_TARGET={_settings.NONWIN_MIN_TARGET}");
        }

        var maxPublicWheelStackValue = Math.Min(_settings.MAX_COIN_STACK - 1, MaxPublicWheelStackValue);
        if (_settings.MAX_COIN_STACK < 2
            || _settings.MIN_WHEEL_STACK_VALUE < 1
            || _settings.MAX_WHEEL_STACK_VALUE < _settings.MIN_WHEEL_STACK_VALUE
            || _settings.MAX_WHEEL_STACK_VALUE > maxPublicWheelStackValue)
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidStackRange,
                $"stack range invalid, found MAX_COIN_STACK={_settings.MAX_COIN_STACK}, " +
                $"wheel bonus={_settings.MIN_WHEEL_STACK_VALUE}..{_settings.MAX_WHEEL_STACK_VALUE}; " +
                $"WheelStackValue must be in 1..{MaxPublicWheelStackValue}, and total stack " +
                $"1 + WheelStackValue must not exceed MAX_COIN_STACK ({maxPublicWheelStackValue} max for this config)");
        }

        var ladderCheck = ValidatePrizeLadder();
        if (!ladderCheck.IsValid)
            return ladderCheck;

        var featureIdCheck = ValidateFeatureIds();
        if (!featureIdCheck.IsValid)
            return featureIdCheck;

        var featureConfigCheck = ValidateFeatureConfig();
        if (!featureConfigCheck.IsValid)
            return featureConfigCheck;

        var distributionCheck = ValidateDistributionConfig();
        if (!distributionCheck.IsValid)
            return distributionCheck;

        return ValidateWinningRoundRules();
    }

    private CoinPusherTicketGenerationRequestValidationResult ValidatePrizeLadder()
    {
        if (_settings.PrizeLadderRows == null || _settings.PrizeLadderRows.Count == 0)
            return Fail(CoinPusherTicketGenerationRequestStatus.MissingPrizeLadder, "settings.PrizeLadderRows is empty");

        for (var index = 0; index < _settings.PrizeLadderRows.Count; index++)
        {
            var row = _settings.PrizeLadderRows[index];
            if (row.Target <= 0)
            {
                return Fail(
                    CoinPusherTicketGenerationRequestStatus.InvalidPrizeLadderRow,
                    $"prize ladder row {index + 1} has invalid target {row.Target}");
            }

            if (row.Tiers == null || row.Tiers.Count == 0)
            {
                return Fail(
                    CoinPusherTicketGenerationRequestStatus.InvalidPrizeLadderRow,
                    $"prize ladder row {index + 1} has no tier amounts");
            }

            for (var tier = 0; tier < row.Tiers.Count; tier++)
            {
                if (row.Tiers[tier] <= 0m)
                {
                    return Fail(
                        CoinPusherTicketGenerationRequestStatus.InvalidPrizeLadderRow,
                        $"prize ladder row {index + 1}, tier {tier} has non-positive amount {row.Tiers[tier]}");
                }

                if (tier > 0 && row.Tiers[tier] <= row.Tiers[tier - 1])
                {
                    return Fail(
                        CoinPusherTicketGenerationRequestStatus.InvalidPrizeLadderRow,
                        $"prize ladder row {index + 1} tiers must strictly increase");
                }
            }
        }

        return new CoinPusherTicketGenerationRequestValidationResult(
            CoinPusherTicketGenerationRequestStatus.Valid,
            "ok");
    }

    private CoinPusherTicketGenerationRequestValidationResult ValidateFeatureIds()
    {
        var ids = new[] { _settings.F_WHEEL, _settings.F_XSPIN, _settings.F_PRUP, _settings.F_FLUSH_ID };
        var maxCoinSymbol = _settings.PrizeLadderRows.Count;
        if (ids.Any(id => id <= maxCoinSymbol)
            || ids.Distinct().Count() != ids.Length
            || _settings.F_COIN < 1
            || _settings.F_COIN > maxCoinSymbol
            || ids.Contains(_settings.F_COIN))
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidFeatureIds,
                $"feature ids must be unique and outside normal coin range 1..{maxCoinSymbol}; " +
                $"F_COIN={_settings.F_COIN} must be inside that normal coin range");
        }

        return new CoinPusherTicketGenerationRequestValidationResult(
            CoinPusherTicketGenerationRequestStatus.Valid,
            "ok");
    }

    private CoinPusherTicketGenerationRequestValidationResult ValidateFeatureConfig()
    {
        var configs = new[]
        {
            ("WHEEL", _settings.WheelFeatureConfig),
            ("FLUSH", _settings.FlushFeatureConfig),
            ("EXTRA_SPIN", _settings.ExtraSpinFeatureConfig),
            ("PRIZE_UPGRADE", _settings.PrizeUpgradeFeatureConfig),
        };

        foreach (var config in configs)
        {
            if (!IsProbability(config.Item2.P) || config.Item2.Max < 0)
            {
                return Fail(
                    CoinPusherTicketGenerationRequestStatus.InvalidFeatureConfig,
                    $"{config.Item1} feature probability/max invalid");
            }

            if (config.Item2.MinS < 0 || config.Item2.MaxS < config.Item2.MinS)
            {
                return Fail(
                    CoinPusherTicketGenerationRequestStatus.InvalidFeatureConfig,
                    $"{config.Item1} feature spin window invalid");
            }
        }

        if (_settings.ExtraSpinFeatureConfig.Max > _settings.MAX_SPINS - _settings.BASE_SPINS)
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidFeatureConfig,
                $"EXTRA_SPIN max {_settings.ExtraSpinFeatureConfig.Max} exceeds available extra turns {_settings.MAX_SPINS - _settings.BASE_SPINS}");
        }

        return new CoinPusherTicketGenerationRequestValidationResult(
            CoinPusherTicketGenerationRequestStatus.Valid,
            "ok");
    }

    private CoinPusherTicketGenerationRequestValidationResult ValidateDistributionConfig()
    {
        if (_settings.WinLateTailSpins < 0
            || _settings.WinLateMinTail < 0
            || _settings.MaxDeferredCollectionsPerTurn < 0)
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidFeatureConfig,
                $"late-win tail settings must be non-negative, found WinLateTailSpins={_settings.WinLateTailSpins}, " +
                $"WinLateMinTail={_settings.WinLateMinTail}, " +
                $"MaxDeferredCollectionsPerTurn={_settings.MaxDeferredCollectionsPerTurn}");
        }

        var probabilities = new (string Name, double Value)[]
        {
            (nameof(_settings.PWheelStackValue1), _settings.PWheelStackValue1),
            (nameof(_settings.PWheelStackValue2), _settings.PWheelStackValue2),
            (nameof(_settings.PWheelRepeatOptional), _settings.PWheelRepeatOptional),
            (nameof(_settings.PWheelStackCollection), _settings.PWheelStackCollection),
            (nameof(_settings.PWheelPreferDenseTarget), _settings.PWheelPreferDenseTarget),
            (nameof(_settings.POptionalFeatureTicket), _settings.POptionalFeatureTicket),
            (nameof(_settings.POptionalTicketWheel), _settings.POptionalTicketWheel),
            (nameof(_settings.POptionalTicketFlush), _settings.POptionalTicketFlush),
            (nameof(_settings.POptionalTicketPrizeUpgrade), _settings.POptionalTicketPrizeUpgrade),
            (nameof(_settings.PNoWinExtraGoOptional), _settings.PNoWinExtraGoOptional),
            (nameof(_settings.PWheelOptional), _settings.PWheelOptional),
            (nameof(_settings.PFlushOptional), _settings.PFlushOptional),
            (nameof(_settings.PNonWinWheel), _settings.PNonWinWheel),
            (nameof(_settings.PNonWinPrizeUpgrade), _settings.PNonWinPrizeUpgrade),
            (nameof(_settings.PWinLateCompletion), _settings.PWinLateCompletion),
            (nameof(_settings.WinLateTailFraction), _settings.WinLateTailFraction),
            (nameof(_settings.PFeatureRetriggerChain), _settings.PFeatureRetriggerChain),
            (nameof(_settings.PFeatureLatePlacement), _settings.PFeatureLatePlacement),
            (nameof(_settings.PFeatureSameTurn), _settings.PFeatureSameTurn),
            (nameof(_settings.WExpFeature), _settings.WExpFeature),
            (nameof(_settings.WExpStack), _settings.WExpStack),
        };
        var invalidProbability = probabilities.FirstOrDefault(item => !IsProbability(item.Value));
        if (invalidProbability.Name != null)
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidFeatureConfig,
                $"{invalidProbability.Name} must be finite and in 0..1, found {invalidProbability.Value}");
        }
        if (_settings.PWheelStackValue1 + _settings.PWheelStackValue2 > 1.0)
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidFeatureConfig,
                $"PWheelStackValue1 + PWheelStackValue2 must not exceed 1, found " +
                $"{_settings.PWheelStackValue1 + _settings.PWheelStackValue2}");
        }

        var weights = new (string Name, double Value)[]
        {
            (nameof(_settings.WExpBalanced), _settings.WExpBalanced),
            (nameof(_settings.WExpNearMiss), _settings.WExpNearMiss),
            (nameof(_settings.WExpLateWin), _settings.WExpLateWin),
            (nameof(_settings.WNonWinLow), _settings.WNonWinLow),
            (nameof(_settings.WNonWinMid), _settings.WNonWinMid),
            (nameof(_settings.WNonWinHigh), _settings.WNonWinHigh),
            (nameof(_settings.WPusherLowPop), _settings.WPusherLowPop),
            (nameof(_settings.WPusherMidPop), _settings.WPusherMidPop),
            (nameof(_settings.WPusherHighPop), _settings.WPusherHighPop),
        };
        var invalidWeight = weights.FirstOrDefault(item => !IsWeight(item.Value));
        if (invalidWeight.Name != null)
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidFeatureConfig,
                $"{invalidWeight.Name} must be finite and non-negative, found {invalidWeight.Value}");
        }
        if (_settings.WNonWinLow + _settings.WNonWinMid + _settings.WNonWinHigh <= 0.0
            || _settings.WPusherLowPop + _settings.WPusherMidPop + _settings.WPusherHighPop <= 0.0)
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidFeatureConfig,
                "non-win tier weights and pusher-range weights must each contain at least one positive value");
        }

        var countWeights = _settings.NonWinCountWeights ?? Array.Empty<double>();
        if (countWeights.Length == 0
            || countWeights.Any(value => !IsWeight(value))
            || countWeights.All(value => value == 0.0))
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidFeatureConfig,
                "NonWinCountWeights must contain at least one positive finite weight");
        }

        var profiles = _settings.NonWinTargetProfiles
            ?? Array.Empty<(double P, int Min, int Max, int MaxSymbols)>();
        if (profiles.Length == 0 || profiles.All(profile => profile.P == 0.0))
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidFeatureConfig,
                "NonWinTargetProfiles must contain at least one positive-weight profile");
        }
        for (var index = 0; index < profiles.Length; index++)
        {
            var profile = profiles[index];
            var disabled = profile.MaxSymbols == 0 && profile.Min == 0 && profile.Max == 0;
            if (!IsWeight(profile.P)
                || profile.MaxSymbols < 0
                || (!disabled && (profile.Min < 1 || profile.Max < profile.Min)))
            {
                return Fail(
                    CoinPusherTicketGenerationRequestStatus.InvalidFeatureConfig,
                    $"NonWinTargetProfiles[{index}] is invalid: P={profile.P}, Min={profile.Min}, " +
                    $"Max={profile.Max}, MaxSymbols={profile.MaxSymbols}");
            }
        }

        var bridgeIds = _settings.FeatureRetriggerBridgeIds ?? Array.Empty<int>();
        var validBoardFeatureIds = new[] { _settings.F_WHEEL, _settings.F_XSPIN, _settings.F_PRUP };
        if (bridgeIds.Distinct().Count() != bridgeIds.Length
            || bridgeIds.Any(id => !validBoardFeatureIds.Contains(id)))
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidFeatureConfig,
                "FeatureRetriggerBridgeIds must be unique configured board-feature ids");
        }

        return new CoinPusherTicketGenerationRequestValidationResult(
            CoinPusherTicketGenerationRequestStatus.Valid,
            "ok");
    }

    private CoinPusherTicketGenerationRequestValidationResult ValidateWinningRoundRules()
    {
        var rules = _settings.WinningRoundRules;
        if (rules == null || rules.Count == 0)
            return WinningRuleFailure("WinningRoundRules is empty");
        if (rules.Any(rule => rule == null))
            return WinningRuleFailure("WinningRoundRules contains a null rule");

        var ordered = rules.OrderBy(rule => rule.MinWinInclusive).ToArray();
        if (ordered[0].MinWinInclusive != 0m)
            return WinningRuleFailure($"winning-round coverage must start at 0, found {ordered[0].MinWinInclusive}");

        var maxExtraGo = Math.Min(
            _settings.ExtraSpinFeatureConfig.Max,
            _settings.MAX_SPINS - _settings.BASE_SPINS);
        for (var index = 0; index < ordered.Length; index++)
        {
            var rule = ordered[index];
            if (rule.MinWinInclusive < 0m
                || (rule.MaxWinExclusive.HasValue && rule.MaxWinExclusive.Value <= rule.MinWinInclusive))
            {
                return WinningRuleFailure($"rule {index} has invalid range {WinningRuleText(rule)}");
            }
            if (rule.ExtraGoCounts == null
                || rule.ExtraGoCounts.Count == 0
                || rule.ExtraGoCounts.Distinct().Count() != rule.ExtraGoCounts.Count
                || rule.ExtraGoCounts.Any(count => count < 0 || count > maxExtraGo))
            {
                return WinningRuleFailure(
                    $"rule {WinningRuleText(rule)} ExtraGoCounts must be unique and inside 0..{maxExtraGo}");
            }

            var isNoWinRule = rule.Matches(0m);
            if (isNoWinRule)
            {
                if (rule.MinWinningTurn.HasValue || rule.MaxWinningTurn.HasValue)
                    return WinningRuleFailure($"no-win rule {WinningRuleText(rule)} must not define a winning turn");
            }
            else
            {
                if (!rule.MinWinningTurn.HasValue
                    || !rule.MaxWinningTurn.HasValue
                    || rule.MinWinningTurn.Value < 1
                    || rule.MaxWinningTurn.Value < rule.MinWinningTurn.Value
                    || rule.MaxWinningTurn.Value > _settings.MAX_SPINS)
                {
                    return WinningRuleFailure($"rule {WinningRuleText(rule)} has an invalid winning-turn range");
                }
                if (rule.ExtraGoCounts.Any(count =>
                        rule.MinWinningTurn.Value > _settings.BASE_SPINS + count))
                {
                    return WinningRuleFailure(
                        $"rule {WinningRuleText(rule)} contains an Extra Go count that cannot reach its minimum winning turn {rule.MinWinningTurn}");
                }
            }

            if (index < ordered.Length - 1)
            {
                if (!rule.MaxWinExclusive.HasValue
                    || rule.MaxWinExclusive.Value != ordered[index + 1].MinWinInclusive)
                {
                    return WinningRuleFailure(
                        $"winning-round ranges overlap or have a gap between {WinningRuleText(rule)} and {WinningRuleText(ordered[index + 1])}");
                }
            }
            else if (rule.MaxWinExclusive.HasValue)
            {
                return WinningRuleFailure($"final winning-round rule {WinningRuleText(rule)} must be unbounded");
            }
        }

        return new CoinPusherTicketGenerationRequestValidationResult(
            CoinPusherTicketGenerationRequestStatus.Valid,
            "ok");
    }

    private static string WinningRuleText(WinningRoundRule rule) =>
        $"[{rule.MinWinInclusive}..{(rule.MaxWinExclusive.HasValue ? rule.MaxWinExclusive.Value.ToString() : "unbounded")})";

    private static CoinPusherTicketGenerationRequestValidationResult WinningRuleFailure(string detail) =>
        Fail(CoinPusherTicketGenerationRequestStatus.InvalidWinningRoundConfig, detail);

    private static bool IsProbability(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0.0 && value <= 1.0;

    private static bool IsWeight(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0.0;

    private static CoinPusherTicketGenerationRequestValidationResult Fail(
        CoinPusherTicketGenerationRequestStatus status,
        string detail) =>
        new(status, detail);
}
