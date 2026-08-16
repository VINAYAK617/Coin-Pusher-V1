namespace CoinPusherEngine;

internal enum SymbolCollectionStatus
{
    Valid,
    UnknownSymbol,
    InvalidStack,
    WouldExceedWinTarget,
    WouldExceedNonWinCap,
    TopPrizeCompletesBeforeFinalTurn,
    WinTargetNotReached,
    NearMissMinimumNotReached,
}

internal readonly struct SymbolCollectionCheck
{
    internal SymbolCollectionCheck(
        SymbolCollectionStatus status,
        int symbol,
        int current,
        int projected,
        int limit,
        string detail)
    {
        Status = status;
        Symbol = symbol;
        Current = current;
        Projected = projected;
        Limit = limit;
        Detail = detail;
    }

    internal SymbolCollectionStatus Status { get; }
    internal int Symbol { get; }
    internal int Current { get; }
    internal int Projected { get; }
    internal int Limit { get; }
    internal string Detail { get; }
    internal bool IsValid => Status == SymbolCollectionStatus.Valid;
}

internal sealed class SymbolLedger
{
    private readonly Dictionary<int, int> _winTargets;
    private readonly Dictionary<int, int> _nearMissMinimums;
    private readonly Dictionary<int, int> _collected = new();
    private readonly int _maxSymbol;
    private readonly ICustomProfileSettings _settings;

    internal SymbolLedger(
        IReadOnlyDictionary<int, int> winTargets,
        IReadOnlyDictionary<int, int> nearMissMinimums,
        int maxSymbol,
        ICustomProfileSettings settings)
    {
        _winTargets = winTargets.ToDictionary(kv => kv.Key, kv => kv.Value);
        _nearMissMinimums = nearMissMinimums.ToDictionary(kv => kv.Key, kv => kv.Value);
        _maxSymbol = maxSymbol;
        _settings = settings;
    }

    private SymbolLedger(
        Dictionary<int, int> winTargets,
        Dictionary<int, int> nearMissMinimums,
        Dictionary<int, int> collected,
        int maxSymbol,
        ICustomProfileSettings settings)
    {
        _winTargets = winTargets.ToDictionary(kv => kv.Key, kv => kv.Value);
        _nearMissMinimums = nearMissMinimums.ToDictionary(kv => kv.Key, kv => kv.Value);
        _collected = collected.ToDictionary(kv => kv.Key, kv => kv.Value);
        _maxSymbol = maxSymbol;
        _settings = settings;
    }

    internal IReadOnlyDictionary<int, int> Collected => _collected;

    internal int CollectedCount(int symbol) => _collected.GetValueOrDefault(symbol);

    internal SymbolLedger Clone() =>
        new(_winTargets, _nearMissMinimums, _collected, _maxSymbol, _settings);

    internal void ReplaceWith(SymbolLedger other)
    {
        _collected.Clear();
        foreach (var (symbol, count) in other._collected)
            _collected[symbol] = count;
    }

    internal int RemainingWinCount(int symbol)
    {
        if (!_winTargets.TryGetValue(symbol, out var target)) return 0;
        return Math.Max(0, target - CollectedCount(symbol));
    }

    internal SymbolCollectionCheck CheckCollect(
        int symbol,
        int stack = 1,
        int? collectionTurn = null,
        int topPrizeSymbol = 0,
        int finalTurn = 0)
    {
        if (symbol < 1 || symbol > _maxSymbol || _settings.IsFeat(symbol))
        {
            return Fail(
                SymbolCollectionStatus.UnknownSymbol,
                symbol,
                stack,
                0,
                $"symbol {symbol} is outside 1..{_maxSymbol} or is a feature symbol");
        }

        if (stack <= 0)
        {
            return Fail(
                SymbolCollectionStatus.InvalidStack,
                symbol,
                stack,
                0,
                $"stack {stack} must be positive");
        }

        var current = CollectedCount(symbol);
        var projected = current + stack;

        if (_winTargets.TryGetValue(symbol, out var target))
        {
            if (projected > target)
            {
                return new SymbolCollectionCheck(
                    SymbolCollectionStatus.WouldExceedWinTarget,
                    symbol,
                    current,
                    projected,
                    target,
                    $"symbol {symbol} would collect {projected}, above win target {target}");
            }

            if (symbol == topPrizeSymbol
                && collectionTurn.HasValue
                && finalTurn > 0
                && collectionTurn.Value < finalTurn
                && projected >= target)
            {
                return new SymbolCollectionCheck(
                    SymbolCollectionStatus.TopPrizeCompletesBeforeFinalTurn,
                    symbol,
                    current,
                    projected,
                    target,
                    $"top prize symbol {symbol} would complete on turn {collectionTurn.Value}, before final turn {finalTurn}");
            }

            return new SymbolCollectionCheck(
                SymbolCollectionStatus.Valid,
                symbol,
                current,
                projected,
                target,
                "ok");
        }

        var cap = NonWinningCollectionLimit(symbol);
        if (projected >= cap)
        {
            return new SymbolCollectionCheck(
                SymbolCollectionStatus.WouldExceedNonWinCap,
                symbol,
                current,
                projected,
                cap,
                $"symbol {symbol} would collect {projected}, crossing non-win cap {cap}");
        }

        return new SymbolCollectionCheck(
            SymbolCollectionStatus.Valid,
            symbol,
            current,
            projected,
            cap,
            "ok");
    }

    internal SymbolCollectionCheck Collect(
        int symbol,
        int stack = 1,
        int? collectionTurn = null,
        int topPrizeSymbol = 0,
        int finalTurn = 0)
    {
        var check = CheckCollect(symbol, stack, collectionTurn, topPrizeSymbol, finalTurn);
        if (!check.IsValid) return check;

        _collected[symbol] = check.Projected;
        return check;
    }

    internal IReadOnlyList<SymbolCollectionCheck> ValidateFinal()
    {
        var failures = new List<SymbolCollectionCheck>();

        foreach (var (symbol, target) in _winTargets)
        {
            var current = CollectedCount(symbol);
            if (current != target)
            {
                failures.Add(new SymbolCollectionCheck(
                    SymbolCollectionStatus.WinTargetNotReached,
                    symbol,
                    current,
                    current,
                    target,
                    $"symbol {symbol} collected {current}, expected exact win target {target}"));
            }
        }

        foreach (var (symbol, minimum) in _nearMissMinimums)
        {
            var current = CollectedCount(symbol);
            if (current < minimum)
            {
                failures.Add(new SymbolCollectionCheck(
                    SymbolCollectionStatus.NearMissMinimumNotReached,
                    symbol,
                    current,
                    current,
                    minimum,
                    $"symbol {symbol} collected {current}, below near-miss minimum {minimum}"));
            }
        }

        return failures;
    }

    private int NonWinningCollectionLimit(int symbol)
    {
        var prizeCap = _settings.SymbolFillCap(symbol);
        var displayCap = prizeCap > _settings.FILL_CAP
            ? Math.Min(prizeCap, _settings.FILL_CAP + _settings.COLS)
            : Math.Min(prizeCap, _settings.FILL_CAP);

        if (_nearMissMinimums.TryGetValue(symbol, out var target))
            return Math.Min(prizeCap, Math.Max(displayCap, target + 1));

        return displayCap;
    }

    private SymbolCollectionCheck Fail(
        SymbolCollectionStatus status,
        int symbol,
        int stack,
        int limit,
        string detail)
    {
        var current = symbol > 0 ? CollectedCount(symbol) : 0;
        return new SymbolCollectionCheck(status, symbol, current, current + Math.Max(0, stack), limit, detail);
    }
}
