namespace CoinPusherEngine;

internal enum SymbolCollectionStatus
{
    Valid,
    UnknownSymbol,
    InvalidStack,
    WouldExceedWinTarget,
    WouldExceedNonWinCap,
    TopPrizeCompletesBeforeFinalTurn,
    MissingWinCollectionTurn,
    WinCollectionAfterCompletionTurn,
    AllWinsCompleteBeforeRequiredTurn,
    WinningCompletionTurnMismatch,
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
    private readonly Dictionary<int, Dictionary<int, int>> _winCollectionsByTurn = new();
    private readonly int _maxSymbol;
    private readonly ICustomProfileSettings _settings;
    private readonly int? _winningCompletionTurn;

    internal SymbolLedger(
        IReadOnlyDictionary<int, int> winTargets,
        IReadOnlyDictionary<int, int> nearMissMinimums,
        int maxSymbol,
        ICustomProfileSettings settings,
        int? winningCompletionTurn = null)
    {
        _winTargets = winTargets.ToDictionary(kv => kv.Key, kv => kv.Value);
        _nearMissMinimums = nearMissMinimums.ToDictionary(kv => kv.Key, kv => kv.Value);
        _maxSymbol = maxSymbol;
        _settings = settings;
        _winningCompletionTurn = winningCompletionTurn;
    }

    private SymbolLedger(
        Dictionary<int, int> winTargets,
        Dictionary<int, int> nearMissMinimums,
        Dictionary<int, int> collected,
        Dictionary<int, Dictionary<int, int>> winCollectionsByTurn,
        int maxSymbol,
        ICustomProfileSettings settings,
        int? winningCompletionTurn)
    {
        _winTargets = winTargets.ToDictionary(kv => kv.Key, kv => kv.Value);
        _nearMissMinimums = nearMissMinimums.ToDictionary(kv => kv.Key, kv => kv.Value);
        _collected = collected.ToDictionary(kv => kv.Key, kv => kv.Value);
        _winCollectionsByTurn = winCollectionsByTurn.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.ToDictionary(turn => turn.Key, turn => turn.Value));
        _maxSymbol = maxSymbol;
        _settings = settings;
        _winningCompletionTurn = winningCompletionTurn;
    }

    internal IReadOnlyDictionary<int, int> Collected => _collected;

    internal int CollectedCount(int symbol) => _collected.GetValueOrDefault(symbol);

    internal SymbolLedger Clone() =>
        new(
            _winTargets,
            _nearMissMinimums,
            _collected,
            _winCollectionsByTurn,
            _maxSymbol,
            _settings,
            _winningCompletionTurn);

    internal void ReplaceWith(SymbolLedger other)
    {
        _collected.Clear();
        foreach (var (symbol, count) in other._collected)
            _collected[symbol] = count;

        _winCollectionsByTurn.Clear();
        foreach (var (symbol, turns) in other._winCollectionsByTurn)
            _winCollectionsByTurn[symbol] = turns.ToDictionary(kv => kv.Key, kv => kv.Value);
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

            var timing = CheckWinningCompletionTiming(
                symbol,
                stack,
                collectionTurn,
                current,
                projected,
                target);
            if (timing.HasValue)
                return timing.Value;

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

        var cap = NonWinningCollectionLimit(
            symbol,
            _nearMissMinimums.TryGetValue(symbol, out var nearMissTarget) ? nearMissTarget : (int?)null,
            _settings);
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
        if (_winTargets.ContainsKey(symbol) && collectionTurn.HasValue)
        {
            if (!_winCollectionsByTurn.TryGetValue(symbol, out var turns))
                _winCollectionsByTurn[symbol] = turns = new Dictionary<int, int>();
            turns[collectionTurn.Value] = turns.GetValueOrDefault(collectionTurn.Value) + stack;
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


        if (_winningCompletionTurn.HasValue)
        {
            var completionTurn = ActualWinningCompletionTurn();
            if (completionTurn != _winningCompletionTurn)
            {
                failures.Add(new SymbolCollectionCheck(
                    SymbolCollectionStatus.WinningCompletionTurnMismatch,
                    0,
                    completionTurn ?? 0,
                    completionTurn ?? 0,
                    _winningCompletionTurn.Value,
                    $"all winning symbols complete on turn {completionTurn?.ToString() ?? "never"}, " +
                    $"required turn {_winningCompletionTurn.Value}"));
            }
        }

        return failures;
    }

    private SymbolCollectionCheck? CheckWinningCompletionTiming(
        int symbol,
        int stack,
        int? collectionTurn,
        int current,
        int projected,
        int target)
    {
        if (!_winningCompletionTurn.HasValue)
            return null;
        if (!collectionTurn.HasValue)
        {
            return new SymbolCollectionCheck(
                SymbolCollectionStatus.MissingWinCollectionTurn,
                symbol,
                current,
                projected,
                target,
                $"winning symbol {symbol} collection has no collection turn; required completion turn is {_winningCompletionTurn.Value}");
        }
        if (collectionTurn.Value > _winningCompletionTurn.Value)
        {
            return new SymbolCollectionCheck(
                SymbolCollectionStatus.WinCollectionAfterCompletionTurn,
                symbol,
                current,
                projected,
                target,
                $"winning symbol {symbol} would collect on turn {collectionTurn.Value}, after required completion turn {_winningCompletionTurn.Value}");
        }
        if (collectionTurn.Value < _winningCompletionTurn.Value
            && AllWinsWouldCompleteBeforeRequiredTurn(symbol, stack, collectionTurn.Value))
        {
            return new SymbolCollectionCheck(
                SymbolCollectionStatus.AllWinsCompleteBeforeRequiredTurn,
                symbol,
                current,
                projected,
                target,
                $"winning symbol {symbol} collection on turn {collectionTurn.Value} would make every winning target complete before required turn {_winningCompletionTurn.Value}");
        }

        return null;
    }

    private bool AllWinsWouldCompleteBeforeRequiredTurn(
        int candidateSymbol,
        int candidateStack,
        int candidateTurn)
    {
        var beforeTurn = _winningCompletionTurn!.Value - 1;
        foreach (var (symbol, target) in _winTargets)
        {
            var scheduled = ScheduledThrough(symbol, beforeTurn);
            if (symbol == candidateSymbol && candidateTurn <= beforeTurn)
                scheduled += candidateStack;
            if (scheduled < target)
                return false;
        }
        return true;
    }

    private int? ActualWinningCompletionTurn()
    {
        if (_winTargets.Count == 0)
            return null;

        var turns = _winCollectionsByTurn.Values
            .SelectMany(byTurn => byTurn.Keys)
            .Distinct()
            .OrderBy(turn => turn);
        foreach (var turn in turns)
        {
            if (_winTargets.All(target => ScheduledThrough(target.Key, turn) >= target.Value))
                return turn;
        }
        return null;
    }

    private int ScheduledThrough(int symbol, int turn)
    {
        if (!_winCollectionsByTurn.TryGetValue(symbol, out var byTurn))
            return 0;
        return byTurn.Where(kv => kv.Key <= turn).Sum(kv => kv.Value);
    }

    internal static int NonWinningCollectionLimit(
        int symbol,
        int? nearMissMinimum,
        ICustomProfileSettings settings)
    {
        var prizeCap = settings.SymbolFillCap(symbol);
        var displayCap = prizeCap > settings.FILL_CAP
            ? Math.Min(prizeCap, settings.FILL_CAP + settings.COLS)
            : Math.Min(prizeCap, settings.FILL_CAP);

        if (nearMissMinimum.HasValue)
            return Math.Min(prizeCap, Math.Max(displayCap, nearMissMinimum.Value + 1));

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
