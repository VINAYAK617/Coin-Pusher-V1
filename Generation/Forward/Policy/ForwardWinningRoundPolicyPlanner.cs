namespace CoinPusherEngine;

internal enum ForwardWinningRoundPolicyStatus
{
    Valid,
    InvalidTotalWin,
    MissingRules,
    RuleNotFound,
    AmbiguousRule,
    InvalidRule,
    NoLegalExtraGoCount,
    NoLegalWinningTurn,
}

internal sealed class ForwardWinningRoundPlan
{
    internal ForwardWinningRoundPlan(
        decimal totalWin,
        WinningRoundRule rule,
        int extraGoCount,
        int totalTurns,
        int? winningCompletionTurn)
    {
        TotalWin = totalWin;
        Rule = rule;
        ExtraGoCount = extraGoCount;
        TotalTurns = totalTurns;
        WinningCompletionTurn = winningCompletionTurn;
    }

    internal decimal TotalWin { get; }
    internal WinningRoundRule Rule { get; }
    internal int ExtraGoCount { get; }
    internal int TotalTurns { get; }
    internal int? WinningCompletionTurn { get; }
    internal bool IsNoWin => TotalWin == 0m;
}

internal sealed class ForwardWinningRoundPolicyResult
{
    internal ForwardWinningRoundPolicyResult(
        ForwardWinningRoundPolicyStatus status,
        string detail,
        ForwardWinningRoundPlan? plan)
    {
        Status = status;
        Detail = detail;
        Plan = plan;
    }

    internal ForwardWinningRoundPolicyStatus Status { get; }
    internal string Detail { get; }
    internal ForwardWinningRoundPlan? Plan { get; }
    internal bool IsValid => Status == ForwardWinningRoundPolicyStatus.Valid;
}

internal sealed class ForwardWinningRoundPolicyPlanner
{
    private readonly ICustomProfileSettings _settings;

    internal ForwardWinningRoundPolicyPlanner(ICustomProfileSettings settings)
    {
        _settings = settings;
    }

    internal ForwardWinningRoundPolicyResult Plan(decimal totalWin, int seed)
    {
        if (totalWin < 0m)
            return Fail(ForwardWinningRoundPolicyStatus.InvalidTotalWin, $"total win {totalWin} cannot be negative");

        var rules = _settings.WinningRoundRules;
        if (rules == null || rules.Count == 0)
            return Fail(ForwardWinningRoundPolicyStatus.MissingRules, "WinningRoundRules is empty");

        var matching = rules.Where(rule => rule != null && rule.Matches(totalWin)).ToArray();
        if (matching.Length == 0)
            return Fail(ForwardWinningRoundPolicyStatus.RuleNotFound, $"no winning-round rule covers total win {totalWin}");
        if (matching.Length > 1)
            return Fail(ForwardWinningRoundPolicyStatus.AmbiguousRule, $"{matching.Length} winning-round rules cover total win {totalWin}");

        var rule = matching[0];
        var ruleError = ValidateRule(rule, totalWin == 0m);
        if (ruleError != null)
            return Fail(ForwardWinningRoundPolicyStatus.InvalidRule, ruleError);

        var maxExtraGo = Math.Min(
            _settings.ExtraSpinFeatureConfig.Max,
            _settings.MAX_SPINS - _settings.BASE_SPINS);
        var legalExtraGoCounts = rule.ExtraGoCounts
            .Distinct()
            .Where(count => count >= 0 && count <= maxExtraGo)
            .Where(count => totalWin == 0m || rule.MinWinningTurn!.Value <= _settings.BASE_SPINS + count)
            .OrderBy(count => count)
            .ToArray();
        if (legalExtraGoCounts.Length == 0)
        {
            return Fail(
                ForwardWinningRoundPolicyStatus.NoLegalExtraGoCount,
                $"rule {RuleText(rule)} has no Extra Go count legal for BASE_SPINS={_settings.BASE_SPINS}, " +
                $"MAX_SPINS={_settings.MAX_SPINS}, feature max={_settings.ExtraSpinFeatureConfig.Max}");
        }

        var rng = new Random(seed);
        var extraGoCount = SelectExtraGoCount(totalWin, legalExtraGoCounts, rng);
        var totalTurns = _settings.BASE_SPINS + extraGoCount;
        if (totalWin == 0m)
        {
            return Ok(new ForwardWinningRoundPlan(
                totalWin,
                rule,
                extraGoCount,
                totalTurns,
                winningCompletionTurn: null));
        }

        var minTurn = Math.Max(1, rule.MinWinningTurn!.Value);
        var maxTurn = Math.Min(totalTurns, rule.MaxWinningTurn!.Value);
        if (minTurn > maxTurn)
        {
            return Fail(
                ForwardWinningRoundPolicyStatus.NoLegalWinningTurn,
                $"rule {RuleText(rule)} and Extra Go count {extraGoCount} provide no winning turn inside 1..{totalTurns}");
        }

        var winningTurn = rng.Next(minTurn, maxTurn + 1);
        return Ok(new ForwardWinningRoundPlan(
            totalWin,
            rule,
            extraGoCount,
            totalTurns,
            winningTurn));
    }

    private int SelectExtraGoCount(decimal totalWin, IReadOnlyList<int> legalCounts, Random rng)
    {
        if (totalWin != 0m)
            return legalCounts[rng.Next(legalCounts.Count)];

        var zeroAllowed = legalCounts.Contains(0);
        var positive = legalCounts.Where(count => count > 0).ToArray();
        if (!zeroAllowed)
            return positive[rng.Next(positive.Length)];
        if (positive.Length == 0 || rng.NextDouble() >= _settings.PNoWinExtraGoOptional)
            return 0;

        return positive[rng.Next(positive.Length)];
    }

    private static string? ValidateRule(WinningRoundRule rule, bool noWin)
    {
        if (rule.MinWinInclusive < 0m
            || (rule.MaxWinExclusive.HasValue && rule.MaxWinExclusive.Value <= rule.MinWinInclusive))
        {
            return $"invalid win range {RuleText(rule)}";
        }
        if (rule.ExtraGoCounts == null || rule.ExtraGoCounts.Count == 0)
            return $"rule {RuleText(rule)} has no Extra Go choices";
        if (rule.ExtraGoCounts.Any(count => count < 0))
            return $"rule {RuleText(rule)} has a negative Extra Go count";

        if (noWin)
        {
            return rule.MinWinningTurn.HasValue || rule.MaxWinningTurn.HasValue
                ? $"no-win rule {RuleText(rule)} must not define a winning turn"
                : null;
        }

        if (!rule.MinWinningTurn.HasValue
            || !rule.MaxWinningTurn.HasValue
            || rule.MinWinningTurn.Value < 1
            || rule.MaxWinningTurn.Value < rule.MinWinningTurn.Value)
        {
            return $"winning rule {RuleText(rule)} has an invalid winning-turn range";
        }

        return null;
    }

    private static string RuleText(WinningRoundRule rule) =>
        $"[{rule.MinWinInclusive}..{(rule.MaxWinExclusive.HasValue ? rule.MaxWinExclusive.Value.ToString() : "unbounded")})";

    private static ForwardWinningRoundPolicyResult Ok(ForwardWinningRoundPlan plan) =>
        new(ForwardWinningRoundPolicyStatus.Valid, "ok", plan);

    private static ForwardWinningRoundPolicyResult Fail(
        ForwardWinningRoundPolicyStatus status,
        string detail) =>
        new(status, detail, null);
}
