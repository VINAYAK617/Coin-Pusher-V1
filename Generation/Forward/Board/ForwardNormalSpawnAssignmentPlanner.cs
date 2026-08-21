namespace CoinPusherEngine;

internal readonly struct ForwardCollectingSpawnRequest
{
    internal ForwardCollectingSpawnRequest(
        ForwardSpawnCellRequest cell,
        ForwardCellFate fate,
        int order)
    {
        Cell = cell;
        Fate = fate;
        Order = order;
    }

    internal ForwardSpawnCellRequest Cell { get; }
    internal ForwardCellFate Fate { get; }
    internal int Order { get; }
}

internal sealed class ForwardNormalSpawnAssignmentResult
{
    internal ForwardNormalSpawnAssignmentResult(
        bool isValid,
        string detail,
        IReadOnlyDictionary<int, int> symbolsByOrder)
    {
        IsValid = isValid;
        Detail = detail;
        SymbolsByOrder = symbolsByOrder;
    }

    internal bool IsValid { get; }
    internal string Detail { get; }
    internal IReadOnlyDictionary<int, int> SymbolsByOrder { get; }
}

internal sealed class ForwardNormalSpawnAssignmentPlanner
{
    private readonly ForwardSymbolSelector _selector;
    private readonly Dictionary<AssignmentStateKey, SearchResult> _memo = new();
    private IReadOnlyList<ForwardCollectingSpawnRequest> _cells = Array.Empty<ForwardCollectingSpawnRequest>();
    private Func<ForwardCollectingSpawnRequest, int, int> _collectionValue = (_, _) => 0;

    internal ForwardNormalSpawnAssignmentPlanner(ForwardSymbolSelector selector)
    {
        _selector = selector;
    }

    internal ForwardNormalSpawnAssignmentResult Plan(
        IReadOnlyList<ForwardCollectingSpawnRequest> cells,
        Func<ForwardCollectingSpawnRequest, int, int> collectionValue)
    {
        if (cells.Count == 0)
        {
            return new ForwardNormalSpawnAssignmentResult(
                true,
                "no collecting cells require assignment",
                new Dictionary<int, int>());
        }

        _cells = cells;
        _collectionValue = collectionValue;
        _memo.Clear();

        var initial = _selector.SnapshotLedger();
        var linear = TryLinearAssignment(initial);
        if (linear.IsValid)
            return linear;

        _selector.RestoreLedger(initial);
        var solution = Search(index: 0);
        _selector.RestoreLedger(initial);
        if (!solution.IsValid)
        {
            var collectionState = string.Join(",", initial.Collected
                .OrderBy(entry => entry.Key)
                .Select(entry => $"{entry.Key}:{entry.Value}"));
            var cellState = string.Join(";", cells.Select(cell =>
                $"#{cell.Order}:{cell.Cell.CollectIntent}@{cell.Fate.CollectedTurn?.ToString() ?? "residue"}"));
            return new ForwardNormalSpawnAssignmentResult(
                false,
                "no complete legal symbol assignment exists for all collecting cells in this turn; " +
                $"ledger=[{collectionState}], cells=[{cellState}]",
                new Dictionary<int, int>());
        }

        var assigned = new Dictionary<int, int>(cells.Count);
        for (var index = 0; index < cells.Count; index++)
        {
            var cell = cells[index];
            var symbol = solution.Symbols[index];
            var value = collectionValue(cell, symbol);
            var collected = _selector.TryCollectSpecific(symbol, value, cell.Fate.CollectedTurn);
            if (!collected.IsValid)
            {
                _selector.RestoreLedger(initial);
                return new ForwardNormalSpawnAssignmentResult(
                    false,
                    $"verified assignment became invalid at cell ({cell.Cell.Row},{cell.Cell.Col}): {collected.Detail}",
                    new Dictionary<int, int>());
            }

            assigned[cell.Order] = symbol;
        }

        return new ForwardNormalSpawnAssignmentResult(
            true,
            $"assigned {assigned.Count} collecting cell(s) atomically",
            assigned);
    }

    private ForwardNormalSpawnAssignmentResult TryLinearAssignment(SymbolLedger initial)
    {
        var requiredBefore = _selector.RemainingRequiredCount();
        var requiredUpperBound = OptimisticRequiredProgress(startIndex: 0);
        var symbolIndependent = CollectionValuesAreSymbolIndependent();
        var assigned = new Dictionary<int, int>(_cells.Count);

        foreach (var cell in _cells)
        {
            var selection = _selector.ChooseAndCollect(
                cell.Cell.CollectIntent,
                symbol => _collectionValue(cell, symbol),
                cell.Fate.CollectedTurn);
            if (!selection.IsValid)
            {
                _selector.RestoreLedger(initial);
                return Invalid("linear assignment could not fill every collecting cell");
            }

            assigned[cell.Order] = selection.Symbol;
        }

        var progress = requiredBefore - _selector.RemainingRequiredCount();
        if (!symbolIndependent && progress < requiredUpperBound)
        {
            _selector.RestoreLedger(initial);
            return Invalid(
                $"linear assignment progressed {progress}, below safe upper bound {requiredUpperBound}");
        }

        return new ForwardNormalSpawnAssignmentResult(
            true,
            $"assigned {assigned.Count} collecting cell(s) transactionally",
            assigned);
    }

    private bool CollectionValuesAreSymbolIndependent()
    {
        foreach (var cell in _cells)
        {
            var expected = _collectionValue(cell, 1);
            for (var symbol = 2; symbol <= _selector.MaxSymbol; symbol++)
            {
                if (_collectionValue(cell, symbol) != expected)
                    return false;
            }
        }

        return true;
    }

    private static ForwardNormalSpawnAssignmentResult Invalid(string detail) =>
        new(false, detail, new Dictionary<int, int>());

    private SearchResult Search(int index)
    {
        if (index == _cells.Count)
            return SearchResult.Success(Array.Empty<int>(), requiredProgress: 0, preference: 0);

        var canMemoize = TryStateKey(index, out var stateKey);
        if (canMemoize && _memo.TryGetValue(stateKey, out var cached))
            return cached;

        var cell = _cells[index];
        var candidates = _selector.CollectCandidates(
            cell.Cell.CollectIntent,
            symbol => _collectionValue(cell, symbol),
            cell.Fate.CollectedTurn)
            .OrderByDescending(candidate => RequiredProgress(cell, candidate.Symbol))
            .ThenByDescending(candidate => candidate.Preference)
            .ToArray();
        var requiredUpperBound = OptimisticRequiredProgress(index);
        var best = SearchResult.Failure;

        foreach (var candidate in candidates)
        {
            var value = _collectionValue(cell, candidate.Symbol);
            if (value <= 0)
                continue;

            var before = _selector.RemainingRequiredCount();
            var snapshot = _selector.SnapshotLedger();
            var collected = _selector.TryCollectSpecific(
                candidate.Symbol,
                value,
                cell.Fate.CollectedTurn);
            if (!collected.IsValid)
            {
                _selector.RestoreLedger(snapshot);
                continue;
            }

            var progress = before - _selector.RemainingRequiredCount();
            var suffix = Search(index + 1);
            _selector.RestoreLedger(snapshot);
            if (!suffix.IsValid)
                continue;

            var proposed = SearchResult.Prepend(
                candidate.Symbol,
                suffix,
                progress,
                candidate.Preference);
            if (proposed.IsBetterThan(best))
                best = proposed;
            if (best.RequiredProgress >= requiredUpperBound)
                break;
        }

        if (canMemoize)
            _memo[stateKey] = best;
        return best;
    }

    private int OptimisticRequiredProgress(int startIndex)
    {
        var cellBound = 0;
        for (var index = startIndex; index < _cells.Count; index++)
        {
            var cell = _cells[index];
            var maxCellValue = 0;
            for (var symbol = 1; symbol <= _selector.MaxSymbol; symbol++)
                maxCellValue = Math.Max(maxCellValue, _collectionValue(cell, symbol));
            cellBound += maxCellValue;
        }

        return Math.Min(cellBound, _selector.RemainingRequiredCount());
    }

    private int RequiredProgress(ForwardCollectingSpawnRequest cell, int symbol)
    {
        var value = _collectionValue(cell, symbol);
        return value <= 0 ? 0 : Math.Min(value, _selector.RequiredRemaining(symbol));
    }

    private bool TryStateKey(int index, out AssignmentStateKey key)
    {
        if (_selector.TryGetPackedCollectionState(out var low, out var high))
        {
            key = new AssignmentStateKey(index, low, high);
            return true;
        }

        key = default;
        return false;
    }

    private readonly struct AssignmentStateKey : IEquatable<AssignmentStateKey>
    {
        internal AssignmentStateKey(int index, ulong low, ulong high)
        {
            Index = index;
            Low = low;
            High = high;
        }

        private int Index { get; }
        private ulong Low { get; }
        private ulong High { get; }

        public bool Equals(AssignmentStateKey other) =>
            Index == other.Index && Low == other.Low && High == other.High;

        public override bool Equals(object? obj) => obj is AssignmentStateKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Index, Low, High);
    }

    private sealed class SearchResult
    {
        private SearchResult(bool isValid, int[] symbols, int requiredProgress, int preference)
        {
            IsValid = isValid;
            Symbols = symbols;
            RequiredProgress = requiredProgress;
            Preference = preference;
        }

        internal static SearchResult Failure { get; } = new(false, Array.Empty<int>(), 0, 0);

        internal bool IsValid { get; }
        internal int[] Symbols { get; }
        internal int RequiredProgress { get; }
        private int Preference { get; }

        internal static SearchResult Success(int[] symbols, int requiredProgress, int preference) =>
            new(true, symbols, requiredProgress, preference);

        internal static SearchResult Prepend(
            int symbol,
            SearchResult suffix,
            int requiredProgress,
            int preference)
        {
            var symbols = new int[suffix.Symbols.Length + 1];
            symbols[0] = symbol;
            Array.Copy(suffix.Symbols, 0, symbols, 1, suffix.Symbols.Length);
            return new SearchResult(
                true,
                symbols,
                requiredProgress + suffix.RequiredProgress,
                preference + suffix.Preference);
        }

        internal bool IsBetterThan(SearchResult other) =>
            !other.IsValid
            || RequiredProgress > other.RequiredProgress
            || (RequiredProgress == other.RequiredProgress && Preference > other.Preference);
    }
}
