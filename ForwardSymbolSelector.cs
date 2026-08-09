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

    internal ForwardSymbolSelector(
        SymbolLedger ledger,
        IReadOnlyDictionary<int, int> winTargets,
        IReadOnlyDictionary<int, int> nearMissMinimums,
        IReadOnlyList<int> fillerSymbols,
        int maxSymbol,
        Settings settings,
        Random rng)
    {
        _ledger = ledger;
        _winSymbols = winTargets.Keys.ToHashSet();
        _nearMissMinimums = nearMissMinimums.ToDictionary(kv => kv.Key, kv => kv.Value);
        _fillerSymbols = fillerSymbols.ToArray();
        _maxSymbol = maxSymbol;
        _settings = settings;
        _rng = rng;
    }

    internal ForwardSymbolSelection ChooseAndCollect(ForwardSymbolIntent intent, int stack = 1)
    {
        if (intent == ForwardSymbolIntent.ResidueOnly)
            return ChooseResidue();

        if (stack <= 0)
            return new ForwardSymbolSelection(
                ForwardSymbolSelectionStatus.InvalidStack,
                0,
                $"stack {stack} must be positive");

        var candidates = CandidateOrder(intent, stack).ToArray();
        if (candidates.Length == 0)
        {
            return new ForwardSymbolSelection(
                ForwardSymbolSelectionStatus.NoLegalSymbol,
                0,
                $"no legal symbol for intent {intent} and stack {stack}");
        }

        var symbol = Pick(candidates);
        var check = _ledger.Collect(symbol, stack);
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

    internal ForwardSymbolSelection TryCollectSpecific(int symbol, int stack = 1)
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

        var check = _ledger.Collect(symbol, stack);
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

    private IEnumerable<int> CandidateOrder(ForwardSymbolIntent intent, int stack)
    {
        if (intent == ForwardSymbolIntent.MustProgressWin)
            return LegalWinSymbols(stack);

        if (intent == ForwardSymbolIntent.PreferNearMiss)
        {
            var nearMiss = LegalNearMissBelowMinimum(stack).ToArray();
            return nearMiss.Length > 0 ? nearMiss : LegalFillers(stack);
        }

        if (intent == ForwardSymbolIntent.SafeFiller)
        {
            var fillers = LegalFillers(stack).ToArray();
            return fillers.Length > 0 ? fillers : LegalNearMissSymbols(stack);
        }

        return Enumerable.Empty<int>();
    }

    private IEnumerable<int> LegalWinSymbols(int stack) =>
        _winSymbols
            .Where(symbol => _ledger.RemainingWinCount(symbol) > 0)
            .Where(symbol => _ledger.CheckCollect(symbol, stack).IsValid)
            .OrderByDescending(symbol => _ledger.RemainingWinCount(symbol))
            .ThenBy(symbol => symbol);

    private IEnumerable<int> LegalNearMissBelowMinimum(int stack) =>
        _nearMissMinimums
            .Where(kv => _ledger.CollectedCount(kv.Key) < kv.Value)
            .Select(kv => kv.Key)
            .Where(symbol => _ledger.CheckCollect(symbol, stack).IsValid)
            .OrderByDescending(symbol => _nearMissMinimums[symbol] - _ledger.CollectedCount(symbol))
            .ThenBy(symbol => symbol);

    private IEnumerable<int> LegalNearMissSymbols(int stack) =>
        _nearMissMinimums.Keys
            .Where(symbol => _ledger.CheckCollect(symbol, stack).IsValid)
            .OrderBy(symbol => symbol);

    private IEnumerable<int> LegalFillers(int stack) =>
        _fillerSymbols
            .Where(symbol => !_winSymbols.Contains(symbol))
            .Where(symbol => !_nearMissMinimums.ContainsKey(symbol))
            .Where(symbol => _ledger.CheckCollect(symbol, stack).IsValid)
            .OrderBy(symbol => symbol);

    private IEnumerable<int> AllVisualCandidates() =>
        _winSymbols
            .Concat(_nearMissMinimums.Keys)
            .Concat(_fillerSymbols)
            .Where(symbol => symbol >= 1 && symbol <= _maxSymbol)
            .Where(symbol => !_settings.IsFeat(symbol))
            .Distinct()
            .OrderBy(symbol => symbol);

    private int Pick(IReadOnlyList<int> symbols) =>
        symbols.Count == 1 ? symbols[0] : symbols[_rng.Next(symbols.Count)];
}
