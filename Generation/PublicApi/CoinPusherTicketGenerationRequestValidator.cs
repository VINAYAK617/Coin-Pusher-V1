namespace CoinPusherEngine;

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
    private readonly Settings _settings;

    public CoinPusherTicketGenerationRequestValidator()
        : this(new Settings())
    {
    }

    public CoinPusherTicketGenerationRequestValidator(Settings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
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

        if (_settings.MAX_COIN_STACK < 1
            || _settings.MIN_WHEEL_STACK_VALUE < 1
            || _settings.MAX_WHEEL_STACK_VALUE < _settings.MIN_WHEEL_STACK_VALUE
            || _settings.MAX_WHEEL_STACK_VALUE > _settings.MAX_COIN_STACK)
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidStackRange,
                $"stack range invalid, found MAX_COIN_STACK={_settings.MAX_COIN_STACK}, wheel={_settings.MIN_WHEEL_STACK_VALUE}..{_settings.MAX_WHEEL_STACK_VALUE}");
        }

        var ladderCheck = ValidatePrizeLadder();
        if (!ladderCheck.IsValid)
            return ladderCheck;

        var featureIdCheck = ValidateFeatureIds();
        if (!featureIdCheck.IsValid)
            return featureIdCheck;

        return ValidateFeatureConfig();
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
        if (ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length || ids.Contains(_settings.F_COIN))
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidFeatureIds,
                $"feature ids must be positive, unique, and different from F_COIN={_settings.F_COIN}");
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
            if (config.Item2.P < 0.0 || config.Item2.P > 1.0 || config.Item2.Max < 0)
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

    private static CoinPusherTicketGenerationRequestValidationResult Fail(
        CoinPusherTicketGenerationRequestStatus status,
        string detail) =>
        new(status, detail);
}
