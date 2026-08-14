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
    private readonly Settings _settings;
    private readonly Random _rng;
    private readonly HashSet<int> _blockedCollectSymbols;
    private readonly int _topPrizeSymbol;
    private readonly int _finalTurn;

    internal ForwardSymbolSelector(
        SymbolLedger ledger,
        IReadOnlyDictionary<int, int> winTargets,
        IReadOnlyDictionary<int, int> nearMissMinimums,
        IReadOnlyList<int> fillerSymbols,
        int maxSymbol,
        Settings settings,
        Random rng,
        IReadOnlySet<int>? blockedCollectSymbols = null,
        int topPrizeSymbol = 0,
        int finalTurn = 0)
    {
        _ledger = ledger;
        _winSymbols = winTargets.Keys.ToHashSet();
        _nearMissMinimums = nearMissMinimums.ToDictionary(kv => kv.Key, kv => kv.Value);
        _fillerSymbols = fillerSymbols.ToArray();
        _maxSymbol = maxSymbol;
        _settings = settings;
        _rng = rng;
        _blockedCollectSymbols = blockedCollectSymbols?.ToHashSet() ?? new HashSet<int>();
        _topPrizeSymbol = topPrizeSymbol;
        _finalTurn = finalTurn;
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
                $"no legal symbol for intent {intent}");
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

        var check = _ledger.Collect(symbol, stack, collectionTurn, _topPrizeSymbol, _finalTurn);
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

        if (symbol < 1 || symbol > _maxSymbol || _settings.IsFeat(symbol))
        {
            return new ForwardSymbolSelection(
                ForwardSymbolSelectionStatus.NoLegalSymbol,
                0,
                $"symbol {symbol} is outside 1..{_maxSymbol} or is a feature symbol");
        }

        var check = _ledger.Collect(symbol, stack, collectionTurn, _topPrizeSymbol, _finalTurn);
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
        var topPrizeFinal = TopPrizeFinalCandidate(stackForSymbol, collectionTurn);
        if (topPrizeFinal.Length > 0)
            return topPrizeFinal;

        if (intent == ForwardSymbolIntent.MustProgressWin)
        {
            var wins = LegalWinSymbols(stackForSymbol, collectionTurn).ToArray();
            if (wins.Length > 0)
                return wins;

            if (IsBlockedOnlyByEarlyTopPrizeCompletion(stackForSymbol, collectionTurn))
            {
                var fillers = WithoutBlocked(LegalFillers(stackForSymbol, collectionTurn)).ToArray();
                return fillers.Length > 0 ? fillers : WithoutBlocked(LegalNearMissSymbols(stackForSymbol, collectionTurn));
            }

            return wins;
        }

        if (intent == ForwardSymbolIntent.PreferNearMiss)
        {
            var nearMiss = WithoutBlocked(LegalNearMissBelowMinimum(stackForSymbol, collectionTurn)).ToArray();
            return nearMiss.Length > 0 ? nearMiss : WithoutBlocked(LegalFillers(stackForSymbol, collectionTurn));
        }

        if (intent == ForwardSymbolIntent.SafeFiller)
        {
            var fillers = WithoutBlocked(LegalFillers(stackForSymbol, collectionTurn)).ToArray();
            return fillers.Length > 0 ? fillers : WithoutBlocked(LegalNearMissSymbols(stackForSymbol, collectionTurn));
        }

        return Enumerable.Empty<int>();
    }

    private IEnumerable<int> LegalWinSymbols(
        Func<int, int> stackForSymbol,
        int? collectionTurn)
    {
        var legal = _winSymbols
            .Where(symbol => _ledger.RemainingWinCount(symbol) > 0)
            .Where(symbol => stackForSymbol(symbol) > 0)
            .Where(symbol => _ledger.CheckCollect(symbol, stackForSymbol(symbol), collectionTurn, _topPrizeSymbol, _finalTurn).IsValid)
            .OrderByDescending(symbol => _ledger.RemainingWinCount(symbol))
            .ThenBy(symbol => symbol)
            .ToArray();

        if (IsFinalCollection(collectionTurn)
            && _topPrizeSymbol > 0
            && legal.Contains(_topPrizeSymbol)
            && _ledger.RemainingWinCount(_topPrizeSymbol) > 0)
        {
            return new[] { _topPrizeSymbol };
        }

        return legal;
    }

    private IEnumerable<int> LegalNearMissBelowMinimum(
        Func<int, int> stackForSymbol,
        int? collectionTurn) =>
        _nearMissMinimums
            .Where(kv => _ledger.CollectedCount(kv.Key) < kv.Value)
            .Select(kv => kv.Key)
            .Where(symbol => stackForSymbol(symbol) > 0)
            .Where(symbol => _ledger.CheckCollect(symbol, stackForSymbol(symbol), collectionTurn, _topPrizeSymbol, _finalTurn).IsValid)
            .OrderByDescending(symbol => _nearMissMinimums[symbol] - _ledger.CollectedCount(symbol))
            .ThenBy(symbol => symbol);

    private IEnumerable<int> LegalNearMissSymbols(
        Func<int, int> stackForSymbol,
        int? collectionTurn) =>
        _nearMissMinimums.Keys
            .Where(symbol => stackForSymbol(symbol) > 0)
            .Where(symbol => _ledger.CheckCollect(symbol, stackForSymbol(symbol), collectionTurn, _topPrizeSymbol, _finalTurn).IsValid)
            .OrderBy(symbol => symbol);

    private IEnumerable<int> LegalFillers(
        Func<int, int> stackForSymbol,
        int? collectionTurn) =>
        _fillerSymbols
            .Where(symbol => !_winSymbols.Contains(symbol))
            .Where(symbol => !_nearMissMinimums.ContainsKey(symbol))
            .Where(symbol => stackForSymbol(symbol) > 0)
            .Where(symbol => _ledger.CheckCollect(symbol, stackForSymbol(symbol), collectionTurn, _topPrizeSymbol, _finalTurn).IsValid)
            .OrderBy(symbol => symbol);

    private IEnumerable<int> AllVisualCandidates() =>
        _winSymbols
            .Concat(_nearMissMinimums.Keys)
            .Concat(_fillerSymbols)
            .Where(symbol => symbol >= 1 && symbol <= _maxSymbol)
            .Where(symbol => !_settings.IsFeat(symbol))
            .Distinct()
            .OrderBy(symbol => symbol);

    private IEnumerable<int> WithoutBlocked(IEnumerable<int> symbols) =>
        symbols.Where(symbol => !_blockedCollectSymbols.Contains(symbol));

    private int[] TopPrizeFinalCandidate(
        Func<int, int> stackForSymbol,
        int? collectionTurn)
    {
        if (!IsFinalCollection(collectionTurn)
            || _topPrizeSymbol <= 0
            || !_winSymbols.Contains(_topPrizeSymbol)
            || _ledger.RemainingWinCount(_topPrizeSymbol) <= 0)
        {
            return Array.Empty<int>();
        }

        var stack = stackForSymbol(_topPrizeSymbol);
        return stack > 0
            && _ledger.CheckCollect(_topPrizeSymbol, stack, collectionTurn, _topPrizeSymbol, _finalTurn).IsValid
            ? new[] { _topPrizeSymbol }
            : Array.Empty<int>();
    }

    private bool IsBlockedOnlyByEarlyTopPrizeCompletion(
        Func<int, int> stackForSymbol,
        int? collectionTurn)
    {
        if (IsFinalCollection(collectionTurn)
            || _topPrizeSymbol <= 0
            || !_winSymbols.Contains(_topPrizeSymbol)
            || _ledger.RemainingWinCount(_topPrizeSymbol) <= 0)
        {
            return false;
        }

        var stack = stackForSymbol(_topPrizeSymbol);
        if (stack <= 0)
            return false;

        var untimed = _ledger.CheckCollect(_topPrizeSymbol, stack);
        if (!untimed.IsValid)
            return false;

        return _ledger.CheckCollect(_topPrizeSymbol, stack, collectionTurn, _topPrizeSymbol, _finalTurn).Status
            == SymbolCollectionStatus.TopPrizeCompletesBeforeFinalTurn;
    }

    private bool IsFinalCollection(int? collectionTurn) =>
        collectionTurn.HasValue
        && _finalTurn > 0
        && collectionTurn.Value == _finalTurn;

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
}
