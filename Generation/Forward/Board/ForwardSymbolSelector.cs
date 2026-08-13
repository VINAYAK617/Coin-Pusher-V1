namespace CoinPusherEngine;

internal enum ForwardSymbolIntent
{
    MustProgressWin,
    PreferNearMiss,
    SafeFiller,
    ResidueOnly,
}

internal enum ForwardSymbolSelectionStatus
{
    Valid,
    NoLegalSymbol,
    InvalidStack,
    NoCandidates,
}

internal readonly struct ForwardSymbolSelection
{
    internal ForwardSymbolSelection(
        ForwardSymbolSelectionStatus status,
        int symbol,
        string detail)
    {
        Status = status;
        Symbol = symbol;
        Detail = detail;
    }

    internal ForwardSymbolSelectionStatus Status { get; }
    internal int Symbol { get; }
    internal string Detail { get; }
    internal bool IsValid => Status == ForwardSymbolSelectionStatus.Valid;
}

internal sealed class ForwardSymbolSelector
{
    private readonly SymbolLedger _ledger;
    private readonly HashSet<int> _winSymbols;
    private readonly Dictionary<int, int> _nearMissMinimums;
    private readonly int[] _fillerSymbols;
    private readonly int _maxSymbol;
    private readonly Random _rng;
    private readonly HashSet<int> _blockedCollectSymbols;
    private readonly Dictionary<int, int> _reservedWinCounts;

    internal ForwardSymbolSelector(
        SymbolLedger ledger,
        IReadOnlyDictionary<int, int> winTargets,
        IReadOnlyDictionary<int, int> nearMissMinimums,
        IReadOnlyList<int> fillerSymbols,
        int maxSymbol,
        Random rng,
        IReadOnlySet<int>? blockedCollectSymbols = null,
        IReadOnlyDictionary<int, int>? reservedWinCounts = null)
    {
        _ledger = ledger;
        _winSymbols = winTargets.Keys.ToHashSet();
        _nearMissMinimums = nearMissMinimums.ToDictionary(kv => kv.Key, kv => kv.Value);
        _fillerSymbols = fillerSymbols.ToArray();
        _maxSymbol = maxSymbol;
        _rng = rng;
        _blockedCollectSymbols = blockedCollectSymbols?.ToHashSet() ?? new HashSet<int>();
        _reservedWinCounts = reservedWinCounts?.ToDictionary(kv => kv.Key, kv => kv.Value)
            ?? new Dictionary<int, int>();
    }

    internal ForwardSymbolSelection ChooseAndCollect(
        ForwardSymbolIntent intent,
        int stack = 1,
        int? collectionTurn = null)
    {
        return ChooseAndCollect(
            intent,
            _ => stack,
            collectionTurn);
    }

    internal ForwardSymbolSelection ChooseAndCollect(
        ForwardSymbolIntent intent,
        Func<int, int> stackForSymbol,
        int? collectionTurn = null)
    {
        if (intent == ForwardSymbolIntent.ResidueOnly)
            return ChooseResidue();

        var candidates = CandidateOrder(intent, stackForSymbol, collectionTurn).ToArray();
        if (candidates.Length == 0)
        {
            return new ForwardSymbolSelection(
                ForwardSymbolSelectionStatus.NoLegalSymbol,
                0,
                $"no legal symbol for intent {intent}; {DebugCandidateState(stackForSymbol, collectionTurn)}");
        }

        var symbol = PickForIntent(intent, candidates, stack: 1);
        var stack = stackForSymbol(symbol);
        if (stack <= 0)
        {
            return new ForwardSymbolSelection(
                ForwardSymbolSelectionStatus.InvalidStack,
                0,
                $"stack {stack} must be positive");
        }

        var check = _ledger.Collect(symbol, stack, collectionTurn);
        if (!check.IsValid)
        {
            return new ForwardSymbolSelection(
                ForwardSymbolSelectionStatus.NoLegalSymbol,
                0,
                $"selected symbol {symbol} became illegal: {check.Detail}");
        }

        return new ForwardSymbolSelection(
            ForwardSymbolSelectionStatus.Valid,
            symbol,
            "ok");
    }

    internal ForwardSymbolSelection ChooseResidue()
    {
        var candidates = AllVisualCandidates().ToArray();
        if (candidates.Length == 0)
        {
            return new ForwardSymbolSelection(
                ForwardSymbolSelectionStatus.NoCandidates,
                0,
                "no visual residue symbols available");
        }

        return new ForwardSymbolSelection(
            ForwardSymbolSelectionStatus.Valid,
            Pick(candidates),
            "ok");
    }

    internal ForwardSymbolSelection TryCollectSpecific(
        int symbol,
        int stack = 1,
        int? collectionTurn = null)
    {
        if (stack <= 0)
        {
            return new ForwardSymbolSelection(
                ForwardSymbolSelectionStatus.InvalidStack,
                0,
                $"stack {stack} must be positive");
        }

        if (symbol < 1 || symbol > _maxSymbol || Settings.IsFeat(symbol))
        {
            return new ForwardSymbolSelection(
                ForwardSymbolSelectionStatus.NoLegalSymbol,
                0,
                $"symbol {symbol} is outside 1..{_maxSymbol} or is a feature symbol");
        }

        if (_blockedCollectSymbols.Contains(symbol))
        {
            return new ForwardSymbolSelection(
                ForwardSymbolSelectionStatus.NoLegalSymbol,
                0,
                $"symbol {symbol} is blocked for same-turn WHEEL collection");
        }

        if (!RespectsReservedWinCount(symbol, stack))
        {
            return new ForwardSymbolSelection(
                ForwardSymbolSelectionStatus.NoLegalSymbol,
                0,
                $"symbol {symbol} must keep {_reservedWinCounts.GetValueOrDefault(symbol)} reserved collection(s) for a future WHEEL");
        }

        var check = _ledger.Collect(symbol, stack, collectionTurn);
        if (!check.IsValid)
        {
            return new ForwardSymbolSelection(
                ForwardSymbolSelectionStatus.NoLegalSymbol,
                0,
                check.Detail);
        }

        return new ForwardSymbolSelection(
            ForwardSymbolSelectionStatus.Valid,
            symbol,
            "ok");
    }

    private IEnumerable<int> CandidateOrder(
        ForwardSymbolIntent intent,
        Func<int, int> stackForSymbol,
        int? collectionTurn)
    {
        if (intent == ForwardSymbolIntent.MustProgressWin)
            return LegalWinSymbols(stackForSymbol, collectionTurn);

        if (intent == ForwardSymbolIntent.PreferNearMiss)
        {
            var nearMiss = WithoutBlocked(LegalNearMissBelowMinimum(stackForSymbol, collectionTurn)).ToArray();
            if (nearMiss.Length > 0) return nearMiss;

            var fillers = WithoutBlocked(LegalFillers(stackForSymbol, collectionTurn)).ToArray();
            if (fillers.Length > 0) return fillers;

            var nearMissSafe = WithoutBlocked(LegalNearMissSymbols(stackForSymbol, collectionTurn)).ToArray();
            return nearMissSafe.Length > 0
                ? nearMissSafe
                : LegalWinSymbols(stackForSymbol, collectionTurn);
        }

        if (intent == ForwardSymbolIntent.SafeFiller)
        {
            var fillers = WithoutBlocked(LegalFillers(stackForSymbol, collectionTurn)).ToArray();
            if (fillers.Length > 0) return fillers;

            var nearMiss = WithoutBlocked(LegalNearMissSymbols(stackForSymbol, collectionTurn)).ToArray();
            return nearMiss.Length > 0 ? nearMiss : LegalWinSymbols(stackForSymbol, collectionTurn);
        }

        return Enumerable.Empty<int>();
    }

    private IEnumerable<int> LegalWinSymbols(
        Func<int, int> stackForSymbol,
        int? collectionTurn) =>
        _winSymbols
            .Where(symbol => _ledger.RemainingWinCount(symbol) > 0)
            .Where(symbol => !_blockedCollectSymbols.Contains(symbol))
            .Where(symbol => stackForSymbol(symbol) > 0)
            .Where(symbol => RespectsReservedWinCount(symbol, stackForSymbol(symbol)))
            .Where(symbol => _ledger.CheckCollect(symbol, stackForSymbol(symbol), collectionTurn).IsValid)
            .OrderByDescending(symbol => _ledger.RemainingWinCount(symbol))
            .ThenBy(symbol => symbol);

    private IEnumerable<int> LegalNearMissBelowMinimum(
        Func<int, int> stackForSymbol,
        int? collectionTurn) =>
        _nearMissMinimums
            .Where(kv => _ledger.CollectedCount(kv.Key) < kv.Value)
            .Select(kv => kv.Key)
            .Where(symbol => stackForSymbol(symbol) > 0)
            .Where(symbol => _ledger.CheckCollect(symbol, stackForSymbol(symbol), collectionTurn).IsValid)
            .OrderByDescending(symbol => _nearMissMinimums[symbol] - _ledger.CollectedCount(symbol))
            .ThenBy(symbol => symbol);

    private IEnumerable<int> LegalNearMissSymbols(
        Func<int, int> stackForSymbol,
        int? collectionTurn) =>
        _nearMissMinimums.Keys
            .Where(symbol => stackForSymbol(symbol) > 0)
            .Where(symbol => _ledger.CheckCollect(symbol, stackForSymbol(symbol), collectionTurn).IsValid)
            .OrderBy(symbol => symbol);

    private IEnumerable<int> LegalFillers(
        Func<int, int> stackForSymbol,
        int? collectionTurn) =>
        _fillerSymbols
            .Where(symbol => !_winSymbols.Contains(symbol))
            .Where(symbol => !_nearMissMinimums.ContainsKey(symbol))
            .Where(symbol => stackForSymbol(symbol) > 0)
            .Where(symbol => _ledger.CheckCollect(symbol, stackForSymbol(symbol), collectionTurn).IsValid)
            .OrderBy(symbol => symbol);

    private IEnumerable<int> AllVisualCandidates() =>
        _winSymbols
            .Concat(_nearMissMinimums.Keys)
            .Concat(_fillerSymbols)
            .Where(symbol => symbol >= 1 && symbol <= _maxSymbol)
            .Where(symbol => !Settings.IsFeat(symbol))
            .Distinct()
            .OrderBy(symbol => symbol);

    private IEnumerable<int> WithoutBlocked(IEnumerable<int> symbols) =>
        symbols.Where(symbol => !_blockedCollectSymbols.Contains(symbol));

    private bool RespectsReservedWinCount(int symbol, int stack)
    {
        if (!_winSymbols.Contains(symbol))
            return true;

        var reserved = _reservedWinCounts.GetValueOrDefault(symbol);
        if (reserved <= 0)
            return true;

        return _ledger.RemainingWinCount(symbol) - stack >= reserved;
    }

    private int Pick(IReadOnlyList<int> symbols) =>
        symbols.Count == 1 ? symbols[0] : symbols[_rng.Next(symbols.Count)];

    private int PickForIntent(
        ForwardSymbolIntent intent,
        IReadOnlyList<int> symbols,
        int stack)
    {
        if (symbols.Count == 1)
            return symbols[0];

        var weighted = symbols
            .Select(symbol => (Symbol: symbol, Weight: SymbolWeight(intent, symbol, stack)))
            .Where(item => item.Weight > 0.0)
            .ToArray();
        if (weighted.Length == 0)
            return Pick(symbols);

        var total = weighted.Sum(item => item.Weight);
        var roll = _rng.NextDouble() * total;
        var acc = 0.0;
        foreach (var item in weighted)
        {
            acc += item.Weight;
            if (roll <= acc)
                return item.Symbol;
        }

        return weighted[^1].Symbol;
    }

    private double SymbolWeight(
        ForwardSymbolIntent intent,
        int symbol,
        int stack)
    {
        _ = stack;
        if (intent == ForwardSymbolIntent.MustProgressWin)
            return Math.Max(1, _ledger.RemainingWinCount(symbol));

        if (intent == ForwardSymbolIntent.PreferNearMiss
            && _nearMissMinimums.TryGetValue(symbol, out var target))
        {
            return Math.Max(1, target - _ledger.CollectedCount(symbol));
        }

        return 1.0;
    }

    private string DebugCandidateState(
        Func<int, int> stackForSymbol,
        int? collectionTurn)
    {
        var wins = _winSymbols
            .OrderBy(symbol => symbol)
            .Select(symbol =>
            {
                var stack = stackForSymbol(symbol);
                var check = _ledger.CheckCollect(symbol, stack, collectionTurn);
                return $"{symbol}:rem={_ledger.RemainingWinCount(symbol)},stack={stack},blocked={_blockedCollectSymbols.Contains(symbol)},reserved={_reservedWinCounts.GetValueOrDefault(symbol)},check={check.Status}";
            });
        return $"wins=[{string.Join(";", wins)}], collectionTurn={collectionTurn?.ToString() ?? "_"}";
    }
}
