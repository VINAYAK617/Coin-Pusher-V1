namespace CoinPusherEngine;

internal enum ForwardPrizeUpgradeStatus
{
    Valid,
    UnknownSymbol,
    UpgradeNotPlanned,
    TierJump,
    ExceedsTargetTier,
    PrizeValueMissing,
    FinalTierMismatch,
}

internal readonly struct ForwardPrizeUpgradeCheck
{
    internal ForwardPrizeUpgradeCheck(
        ForwardPrizeUpgradeStatus status,
        int symbol,
        int currentTier,
        int requestedTier,
        int targetTier,
        string detail)
    {
        Status = status;
        Symbol = symbol;
        CurrentTier = currentTier;
        RequestedTier = requestedTier;
        TargetTier = targetTier;
        Detail = detail;
    }

    internal ForwardPrizeUpgradeStatus Status { get; }
    internal int Symbol { get; }
    internal int CurrentTier { get; }
    internal int RequestedTier { get; }
    internal int TargetTier { get; }
    internal string Detail { get; }
    internal bool IsValid => Status == ForwardPrizeUpgradeStatus.Valid;
}

internal sealed class ForwardPrizeUpgradeLedger
{
    private readonly Dictionary<int, int> _targetTiers;
    private readonly IReadOnlyDictionary<int, IReadOnlyDictionary<int, decimal>> _prizeValues;
    private readonly int _maxSymbol;
    private readonly ICustomProfileSettings _settings;
    private readonly Dictionary<int, int> _currentTiers = new();

    internal ForwardPrizeUpgradeLedger(
        IReadOnlyDictionary<int, int> targetTiers,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, decimal>> prizeValues,
        int maxSymbol,
        ICustomProfileSettings settings)
    {
        _targetTiers = targetTiers
            .Where(kv => kv.Value > 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        _prizeValues = prizeValues;
        _maxSymbol = maxSymbol;
        _settings = settings;
    }

    private ForwardPrizeUpgradeLedger(
        Dictionary<int, int> targetTiers,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, decimal>> prizeValues,
        int maxSymbol,
        ICustomProfileSettings settings,
        Dictionary<int, int> currentTiers)
    {
        _targetTiers = targetTiers.ToDictionary(kv => kv.Key, kv => kv.Value);
        _prizeValues = prizeValues;
        _maxSymbol = maxSymbol;
        _settings = settings;
        _currentTiers = currentTiers.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    internal int CurrentTier(int symbol) => _currentTiers.GetValueOrDefault(symbol);

    internal ForwardPrizeUpgradeLedger Clone() =>
        new(
            _targetTiers,
            _prizeValues,
            _maxSymbol,
            _settings,
            _currentTiers);

    internal void ReplaceWith(ForwardPrizeUpgradeLedger other)
    {
        _currentTiers.Clear();
        foreach (var (symbol, tier) in other._currentTiers)
            _currentTiers[symbol] = tier;
    }

    internal ForwardPrizeUpgradeCheck ApplyUpgrade(int symbol, int requestedTier)
    {
        if (symbol < 1 || symbol > _maxSymbol || _settings.IsFeat(symbol))
        {
            return Fail(
                ForwardPrizeUpgradeStatus.UnknownSymbol,
                symbol,
                requestedTier,
                0,
                $"symbol {symbol} is outside 1..{_maxSymbol} or is a feature symbol");
        }

        if (!_targetTiers.TryGetValue(symbol, out var targetTier))
        {
            return Fail(
                ForwardPrizeUpgradeStatus.UpgradeNotPlanned,
                symbol,
                requestedTier,
                0,
                $"symbol {symbol} has no planned prize upgrade");
        }

        var current = CurrentTier(symbol);
        var expectedNext = current + 1;
        if (requestedTier != expectedNext)
        {
            return new ForwardPrizeUpgradeCheck(
                ForwardPrizeUpgradeStatus.TierJump,
                symbol,
                current,
                requestedTier,
                targetTier,
                $"symbol {symbol} requested tier {requestedTier}, expected next tier {expectedNext}");
        }

        if (requestedTier > targetTier)
        {
            return new ForwardPrizeUpgradeCheck(
                ForwardPrizeUpgradeStatus.ExceedsTargetTier,
                symbol,
                current,
                requestedTier,
                targetTier,
                $"symbol {symbol} requested tier {requestedTier}, above target tier {targetTier}");
        }

        if (!_prizeValues.TryGetValue(symbol, out var tiers) || !tiers.ContainsKey(requestedTier))
        {
            return new ForwardPrizeUpgradeCheck(
                ForwardPrizeUpgradeStatus.PrizeValueMissing,
                symbol,
                current,
                requestedTier,
                targetTier,
                $"symbol {symbol} has no prize value for tier {requestedTier}");
        }

        _currentTiers[symbol] = requestedTier;
        return new ForwardPrizeUpgradeCheck(
            ForwardPrizeUpgradeStatus.Valid,
            symbol,
            requestedTier,
            requestedTier,
            targetTier,
            "ok");
    }

    internal IReadOnlyList<ForwardPrizeUpgradeCheck> ValidateFinal()
    {
        var failures = new List<ForwardPrizeUpgradeCheck>();
        foreach (var (symbol, targetTier) in _targetTiers)
        {
            var current = CurrentTier(symbol);
            if (current != targetTier)
            {
                failures.Add(new ForwardPrizeUpgradeCheck(
                    ForwardPrizeUpgradeStatus.FinalTierMismatch,
                    symbol,
                    current,
                    current,
                    targetTier,
                    $"symbol {symbol} final tier {current}, expected {targetTier}"));
            }
        }

        return failures;
    }

    private ForwardPrizeUpgradeCheck Fail(
        ForwardPrizeUpgradeStatus status,
        int symbol,
        int requestedTier,
        int targetTier,
        string detail) =>
        new(status, symbol, CurrentTier(symbol), requestedTier, targetTier, detail);
}
