namespace CoinPusherEngine;

internal enum ForwardSpawnPlanStatus
{
    Valid,
    MissingCellRequests,
    InvalidSpawnPosition,
    DuplicateSpawnPosition,
    InvalidStack,
    CellFateInvalid,
    NoSafeSymbolForCollectingCell,
    ResidueSelectionFailed,
}

internal readonly struct ForwardSpawnCellRequest
{
    internal ForwardSpawnCellRequest(
        int row,
        int col,
        ForwardSymbolIntent collectIntent = ForwardSymbolIntent.SafeFiller,
        int ledgerCollectionValue = 1,
        int spawnStack = 1)
    {
        Row = row;
        Col = col;
        CollectIntent = collectIntent;
        LedgerCollectionValue = ledgerCollectionValue;
        SpawnStack = spawnStack;
    }

    internal int Row { get; }
    internal int Col { get; }
    internal ForwardSymbolIntent CollectIntent { get; }
    internal int LedgerCollectionValue { get; }
    internal int SpawnStack { get; }
}

internal sealed class ForwardSpawnPlanResult
{
    internal ForwardSpawnPlanResult(
        ForwardSpawnPlanStatus status,
        string detail,
        IReadOnlyList<ForwardSpawn> spawns)
    {
        Status = status;
        Detail = detail;
        Spawns = spawns;
    }

    internal ForwardSpawnPlanStatus Status { get; }
    internal string Detail { get; }
    internal IReadOnlyList<ForwardSpawn> Spawns { get; }
    internal bool IsValid => Status == ForwardSpawnPlanStatus.Valid;
}

internal sealed class ForwardSpawnPlanner
{
    private readonly ForwardSymbolSelector _selector;
    private readonly ForwardCellFateAnalyzer _fateAnalyzer;
    private readonly Settings _settings;

    internal ForwardSpawnPlanner(
        ForwardSymbolSelector selector,
        ForwardCellFateAnalyzer fateAnalyzer,
        Settings settings)
    {
        _selector = selector;
        _fateAnalyzer = fateAnalyzer;
        _settings = settings;
    }

    internal ForwardSpawnPlanResult Plan(
        IReadOnlyList<ForwardSpawnCellRequest>? cells,
        IReadOnlyList<ForwardFutureTurn> futureTurns,
        int spawnTurn = 0,
        IReadOnlyList<ForwardWheelImpact>? wheelImpacts = null)
    {
        if (cells == null)
            return Fail(ForwardSpawnPlanStatus.MissingCellRequests, "cell request list is null");

        var seen = new HashSet<(int r, int c)>();
        var orderedCells = new List<(ForwardSpawnCellRequest Cell, ForwardCellFate Fate, int Order)>(cells.Count);
        var order = 0;
        foreach (var cell in cells)
        {
            var validation = ValidateCell(cell, seen);
            if (validation.Status != ForwardSpawnPlanStatus.Valid)
                return validation;

            var fate = _fateAnalyzer.Analyze(cell.Row, cell.Col, futureTurns);
            if (!fate.IsValid)
            {
                return Fail(
                    ForwardSpawnPlanStatus.CellFateInvalid,
                    $"cell ({cell.Row},{cell.Col}) fate invalid: {fate.Detail}");
            }

            orderedCells.Add((cell, fate, order++));
        }

        var spawns = new List<ForwardSpawn>(cells.Count);
        foreach (var (cell, fate, _) in orderedCells
                     .OrderBy(item => item.Fate.IsCollected ? item.Fate.CollectedTurn!.Value : int.MaxValue)
                     .ThenBy(item => item.Order))
        {
            var selection = fate.IsCollected
                ? _selector.ChooseAndCollect(
                    cell.CollectIntent,
                    symbol => EffectiveCollectionValue(
                        symbol,
                        cell.LedgerCollectionValue,
                        spawnTurn,
                        fate.CollectedTurn,
                        wheelImpacts),
                    fate.CollectedTurn)
                : _selector.ChooseResidue();

            if (!selection.IsValid)
            {
                return Fail(
                    fate.IsCollected
                        ? ForwardSpawnPlanStatus.NoSafeSymbolForCollectingCell
                        : ForwardSpawnPlanStatus.ResidueSelectionFailed,
                    $"cell ({cell.Row},{cell.Col}) {fate.Detail}: {selection.Detail}",
                    spawns);
            }

            var spawnCell = Grid.Norm(selection.Symbol);
            spawnCell.Stack = cell.SpawnStack;
            spawns.Add(new ForwardSpawn(cell.Row, cell.Col, spawnCell));
        }

        return new ForwardSpawnPlanResult(
            ForwardSpawnPlanStatus.Valid,
            $"planned {spawns.Count} normal spawn(s)",
            spawns);
    }

    private int EffectiveCollectionValue(
        int symbol,
        int baseValue,
        int spawnTurn,
        int? collectionTurn,
        IReadOnlyList<ForwardWheelImpact>? wheelImpacts)
    {
        if (!collectionTurn.HasValue || wheelImpacts == null || wheelImpacts.Count == 0)
            return baseValue;

        var value = baseValue;
        foreach (var wheel in wheelImpacts)
        {
            if (wheel.Symbol != symbol) continue;
            if (wheel.FireTurn < spawnTurn) continue;
            if (wheel.FireTurn >= collectionTurn.Value) continue;

            value = Math.Min(_settings.MAX_COIN_STACK, value + wheel.StackAdd);
        }

        return value;
    }

    private ForwardSpawnPlanResult ValidateCell(
        ForwardSpawnCellRequest cell,
        HashSet<(int r, int c)> seen)
    {
        if (cell.Row < 0 || cell.Row >= _settings.ROWS || cell.Col < 0 || cell.Col >= _settings.COLS)
        {
            return Fail(
                ForwardSpawnPlanStatus.InvalidSpawnPosition,
                $"cell ({cell.Row},{cell.Col}) outside board");
        }

        if (!seen.Add((cell.Row, cell.Col)))
        {
            return Fail(
                ForwardSpawnPlanStatus.DuplicateSpawnPosition,
                $"cell ({cell.Row},{cell.Col}) appears more than once");
        }

        if (cell.LedgerCollectionValue <= 0 || cell.SpawnStack <= 0)
        {
            return Fail(
                ForwardSpawnPlanStatus.InvalidStack,
                $"cell ({cell.Row},{cell.Col}) has LedgerCollectionValue={cell.LedgerCollectionValue}, SpawnStack={cell.SpawnStack}");
        }

        return new ForwardSpawnPlanResult(ForwardSpawnPlanStatus.Valid, "ok", Array.Empty<ForwardSpawn>());
    }

    private static ForwardSpawnPlanResult Fail(
        ForwardSpawnPlanStatus status,
        string detail,
        IReadOnlyList<ForwardSpawn>? partialSpawns = null) =>
        new(status, detail, partialSpawns ?? Array.Empty<ForwardSpawn>());
}
