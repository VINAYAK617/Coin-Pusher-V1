namespace CoinPusherEngine;

internal enum SymbolCollectionStatus
{
    Valid,
    UnknownSymbol,
    InvalidStack,
    WouldExceedWinTarget,
    WouldExceedNonWinCap,
    WouldCompleteWinOnWrongTurn,
    WouldCollectWinAfterCompletionTurn,
    WinTargetNotReached,
    NearMissMinimumNotReached,
    WinCompletionTurnMismatch,
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
    private readonly int? _expectedWinCompletionTurn;
    private int? _actualWinCompletionTurn;

    internal SymbolLedger(
        IReadOnlyDictionary<int, int> winTargets,
        IReadOnlyDictionary<int, int> nearMissMinimums,
        int maxSymbol,
        int? expectedWinCompletionTurn = null)
    {
        _winTargets = winTargets.ToDictionary(kv => kv.Key, kv => kv.Value);
        _nearMissMinimums = nearMissMinimums.ToDictionary(kv => kv.Key, kv => kv.Value);
        _maxSymbol = maxSymbol;
        _expectedWinCompletionTurn = expectedWinCompletionTurn;
    }

    private SymbolLedger(
        Dictionary<int, int> winTargets,
        Dictionary<int, int> nearMissMinimums,
        Dictionary<int, int> collected,
        int maxSymbol,
        int? expectedWinCompletionTurn,
        int? actualWinCompletionTurn)
    {
        _winTargets = winTargets.ToDictionary(kv => kv.Key, kv => kv.Value);
        _nearMissMinimums = nearMissMinimums.ToDictionary(kv => kv.Key, kv => kv.Value);
        _collected = collected.ToDictionary(kv => kv.Key, kv => kv.Value);
        _maxSymbol = maxSymbol;
        _expectedWinCompletionTurn = expectedWinCompletionTurn;
        _actualWinCompletionTurn = actualWinCompletionTurn;
    }

    internal IReadOnlyDictionary<int, int> Collected => _collected;
    internal int? WinCompletionTurn => _actualWinCompletionTurn;

    internal int CollectedCount(int symbol) => _collected.GetValueOrDefault(symbol);

    internal SymbolLedger Clone() =>
        new(
            _winTargets,
            _nearMissMinimums,
            _collected,
            _maxSymbol,
            _expectedWinCompletionTurn,
            _actualWinCompletionTurn);

    internal void ReplaceWith(SymbolLedger other)
    {
        _collected.Clear();
        foreach (var (symbol, count) in other._collected)
            _collected[symbol] = count;
        _actualWinCompletionTurn = other._actualWinCompletionTurn;
    }

    internal int RemainingWinCount(int symbol)
    {
        if (!_winTargets.TryGetValue(symbol, out var target)) return 0;
        return Math.Max(0, target - CollectedCount(symbol));
    }

    internal SymbolCollectionCheck CheckCollect(
        int symbol,
        int stack = 1,
        int? collectionTurn = null)
    {
        if (symbol < 1 || symbol > _maxSymbol || Settings.IsFeat(symbol))
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

            var remainingAfter = RemainingWinUnitsAfter(symbol, projected);
            if (_expectedWinCompletionTurn.HasValue)
            {
                if (collectionTurn.HasValue && collectionTurn.Value > _expectedWinCompletionTurn.Value)
                {
                    return new SymbolCollectionCheck(
                        SymbolCollectionStatus.WouldCollectWinAfterCompletionTurn,
                        symbol,
                        current,
                        projected,
                        _expectedWinCompletionTurn.Value,
                        $"symbol {symbol} would collect on turn {collectionTurn.Value}, after expected win completion turn {_expectedWinCompletionTurn.Value}");
                }

                if (remainingAfter == 0
                    && collectionTurn != _expectedWinCompletionTurn)
                {
                    return new SymbolCollectionCheck(
                        SymbolCollectionStatus.WouldCompleteWinOnWrongTurn,
                        symbol,
                        current,
                        projected,
                        _expectedWinCompletionTurn.Value,
                        $"symbol {symbol} would complete the win on turn {collectionTurn?.ToString() ?? "unknown"}, expected {_expectedWinCompletionTurn.Value}");
                }
            }

            return new SymbolCollectionCheck(
                SymbolCollectionStatus.Valid,
                symbol,
                current,
                projected,
                target,
                "ok");
        }

        var cap = Settings.SymbolFillCap(symbol);
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
        int? collectionTurn = null)
    {
        var check = CheckCollect(symbol, stack, collectionTurn);
        if (!check.IsValid) return check;

        _collected[symbol] = check.Projected;
        if (_winTargets.ContainsKey(symbol)
            && RemainingWinUnitsAfter(symbol, check.Projected) == 0
            && !_actualWinCompletionTurn.HasValue)
        {
            _actualWinCompletionTurn = collectionTurn;
        }

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

        if (_winTargets.Count > 0
            && _expectedWinCompletionTurn.HasValue
            && _actualWinCompletionTurn != _expectedWinCompletionTurn)
        {
            failures.Add(new SymbolCollectionCheck(
                SymbolCollectionStatus.WinCompletionTurnMismatch,
                0,
                _actualWinCompletionTurn ?? 0,
                _actualWinCompletionTurn ?? 0,
                _expectedWinCompletionTurn.Value,
                $"win completed on turn {_actualWinCompletionTurn?.ToString() ?? "unknown"}, expected {_expectedWinCompletionTurn.Value}"));
        }

        return failures;
    }

    private int RemainingWinUnitsAfter(int symbol, int projected)
    {
        var remaining = 0;
        foreach (var (winSymbol, target) in _winTargets)
        {
            var count = winSymbol == symbol
                ? projected
                : CollectedCount(winSymbol);
            remaining += Math.Max(0, target - count);
        }

        return remaining;
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
