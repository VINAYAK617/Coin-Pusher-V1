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
    InvalidPpsConfig,
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
        if (Settings.ROWS <= 0 || Settings.COLS <= 0)
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidGridShape,
                $"grid must be positive, found ROWS={Settings.ROWS}, COLS={Settings.COLS}");
        }

        if (Settings.MIN_PUSH < 1 || Settings.MAX_PUSH < Settings.MIN_PUSH || Settings.MAX_PUSH >= Settings.ROWS)
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidPushRange,
                $"normal push range must be 1..ROWS-1, found MIN_PUSH={Settings.MIN_PUSH}, MAX_PUSH={Settings.MAX_PUSH}, ROWS={Settings.ROWS}");
        }

        if (Settings.BASE_SPINS < 1 || Settings.MAX_SPINS < Settings.BASE_SPINS)
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidSpinRange,
                $"spin range invalid, found BASE_SPINS={Settings.BASE_SPINS}, MAX_SPINS={Settings.MAX_SPINS}");
        }

        if (Settings.MAX_COIN_STACK < 1
            || Settings.MIN_WHEEL_STACK_VALUE < 1
            || Settings.MAX_WHEEL_STACK_VALUE < Settings.MIN_WHEEL_STACK_VALUE
            || Settings.MAX_WHEEL_STACK_VALUE > Settings.MAX_COIN_STACK)
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidStackRange,
                $"stack range invalid, found MAX_COIN_STACK={Settings.MAX_COIN_STACK}, wheel={Settings.MIN_WHEEL_STACK_VALUE}..{Settings.MAX_WHEEL_STACK_VALUE}");
        }

        var ladderCheck = ValidatePrizeLadder();
        if (!ladderCheck.IsValid)
            return ladderCheck;

        var featureIdCheck = ValidateFeatureIds();
        if (!featureIdCheck.IsValid)
            return featureIdCheck;

        var featureConfig = ValidateFeatureConfig();
        if (!featureConfig.IsValid)
            return featureConfig;

        return ValidatePpsConfig();
    }

    private CoinPusherTicketGenerationRequestValidationResult ValidatePrizeLadder()
    {
        if (Settings.PrizeLadderRows == null || Settings.PrizeLadderRows.Count == 0)
            return Fail(CoinPusherTicketGenerationRequestStatus.MissingPrizeLadder, "settings.PrizeLadderRows is empty");

        for (var index = 0; index < Settings.PrizeLadderRows.Count; index++)
        {
            var row = Settings.PrizeLadderRows[index];
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
        var ids = new[] { Settings.F_WHEEL, Settings.F_XSPIN, Settings.F_PRUP, Settings.F_FLUSH_ID };
        if (ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length || ids.Contains(Settings.F_COIN))
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidFeatureIds,
                $"feature ids must be positive, unique, and different from F_COIN={Settings.F_COIN}");
        }

        return new CoinPusherTicketGenerationRequestValidationResult(
            CoinPusherTicketGenerationRequestStatus.Valid,
            "ok");
    }

    private CoinPusherTicketGenerationRequestValidationResult ValidateFeatureConfig()
    {
        var configs = new[]
        {
            ("WHEEL", Settings.WheelFeatureConfig),
            ("FLUSH", Settings.FlushFeatureConfig),
            ("EXTRA_SPIN", Settings.ExtraSpinFeatureConfig),
            ("PRIZE_UPGRADE", Settings.PrizeUpgradeFeatureConfig),
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

        if (Settings.ExtraSpinFeatureConfig.Max > Settings.MAX_SPINS - Settings.BASE_SPINS)
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidFeatureConfig,
                $"EXTRA_SPIN max {Settings.ExtraSpinFeatureConfig.Max} exceeds available extra turns {Settings.MAX_SPINS - Settings.BASE_SPINS}");
        }

        return new CoinPusherTicketGenerationRequestValidationResult(
            CoinPusherTicketGenerationRequestStatus.Valid,
            "ok");
    }

    private CoinPusherTicketGenerationRequestValidationResult ValidatePpsConfig()
    {
        if (Settings.PpsCombinations == null || Settings.PpsCombinations.Count == 0)
            return Ok();

        if (Settings.PpsSpinRules == null || Settings.PpsSpinRules.Count == 0)
            return Fail(CoinPusherTicketGenerationRequestStatus.InvalidPpsConfig, "PPS combinations are configured but PPS spin rules are empty");

        foreach (var rule in Settings.PpsSpinRules)
        {
            if (rule.MinWinInclusive < 0m
                || (rule.MaxWinExclusive.HasValue && rule.MaxWinExclusive.Value <= rule.MinWinInclusive)
                || rule.MinExtraGo < 0
                || rule.MaxExtraGo < rule.MinExtraGo
                || rule.MaxExtraGo > Settings.MAX_SPINS - Settings.BASE_SPINS
                || rule.MaxExtraGo > Settings.ExtraSpinFeatureConfig.Max)
            {
                return Fail(
                    CoinPusherTicketGenerationRequestStatus.InvalidPpsConfig,
                    $"invalid PPS spin rule for win range {rule.MinWinInclusive}..{rule.MaxWinExclusive?.ToString() ?? "max"}");
            }

            if (rule.MinWinningTurn.HasValue != rule.MaxWinningTurn.HasValue
                || (rule.MinWinningTurn.HasValue && rule.MaxWinningTurn!.Value < rule.MinWinningTurn.Value))
            {
                return Fail(
                    CoinPusherTicketGenerationRequestStatus.InvalidPpsConfig,
                    $"invalid PPS winning-turn range for win range {rule.MinWinInclusive}..{rule.MaxWinExclusive?.ToString() ?? "max"}");
            }
        }

        foreach (var combination in Settings.PpsCombinations)
        {
            var check = ValidatePpsCombination(combination);
            if (!check.IsValid)
                return check;
        }

        return Ok();
    }

    private CoinPusherTicketGenerationRequestValidationResult ValidatePpsCombination(
        PpsPrizeCombination combination)
    {
        if (combination.Id <= 0 || combination.TotalPrize <= 0m)
        {
            return Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidPpsConfig,
                $"PPS combination #{combination.Id} has invalid id or total prize {combination.TotalPrize}");
        }

        if (combination.Components == null || combination.Components.Count == 0)
            return Fail(CoinPusherTicketGenerationRequestStatus.InvalidPpsConfig, $"PPS combination #{combination.Id} has no components");

        var duplicate = combination.Components
            .GroupBy(component => component.SymbolId)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
            return Fail(CoinPusherTicketGenerationRequestStatus.InvalidPpsConfig, $"PPS combination #{combination.Id} repeats symbol {duplicate.Key}");

        var total = 0m;
        foreach (var component in combination.Components)
        {
            if (component.SymbolId < 1 || component.SymbolId > Settings.PrizeLadderRows.Count)
                return Fail(CoinPusherTicketGenerationRequestStatus.InvalidPpsConfig, $"PPS combination #{combination.Id} references symbol {component.SymbolId}");

            var row = Settings.PrizeLadderRows[component.SymbolId - 1];
            if (component.Tier < 0 || component.Tier >= row.Tiers.Count)
                return Fail(CoinPusherTicketGenerationRequestStatus.InvalidPpsConfig, $"PPS combination #{combination.Id} references symbol {component.SymbolId} tier {component.Tier}");

            total += row.Tiers[component.Tier];
        }

        return total == combination.TotalPrize
            ? Ok()
            : Fail(
                CoinPusherTicketGenerationRequestStatus.InvalidPpsConfig,
                $"PPS combination #{combination.Id} components total {total}, expected {combination.TotalPrize}");
    }

    private static CoinPusherTicketGenerationRequestValidationResult Ok() =>
        new(CoinPusherTicketGenerationRequestStatus.Valid, "ok");

    private static CoinPusherTicketGenerationRequestValidationResult Fail(
        CoinPusherTicketGenerationRequestStatus status,
        string detail) =>
        new(status, detail);
}
