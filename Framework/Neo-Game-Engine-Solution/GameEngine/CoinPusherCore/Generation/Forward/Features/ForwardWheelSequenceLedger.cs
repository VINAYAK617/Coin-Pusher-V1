namespace CoinPusherEngine;

internal enum ForwardWheelSequenceStatus
{
    Valid,
    MissingBoard,
    MissingRequests,
    MissingFutureTurns,
    MissingLedger,
    InvalidBoardDimensions,
    InvalidBoardCell,
    InvalidNormalSpawn,
    InvalidFeatureRequest,
    DuplicateFeaturePosition,
    FeaturePositionOccupied,
    CellFateInvalid,
    WheelHasNoTarget,
    StackOverflow,
    CollectionInvalid,
}

internal sealed class ForwardWheelSequenceResult
{
    internal ForwardWheelSequenceResult(
        ForwardWheelSequenceStatus status,
        string detail,
        SymbolLedger? ledger,
        int collectedWheelBonus)
    {
        Status = status;
        Detail = detail;
        Ledger = ledger;
        CollectedWheelBonus = collectedWheelBonus;
    }

    internal ForwardWheelSequenceStatus Status { get; }
    internal string Detail { get; }
    internal SymbolLedger? Ledger { get; }
    internal int CollectedWheelBonus { get; }
    internal bool IsValid => Status == ForwardWheelSequenceStatus.Valid;
}

internal sealed class ForwardWheelSequenceLedger
{
    private readonly ICustomProfileSettings _settings;
    private readonly ForwardCellFateAnalyzer _fateAnalyzer;

    internal ForwardWheelSequenceLedger(ICustomProfileSettings settings)
    {
        _settings = settings;
        _fateAnalyzer = new ForwardCellFateAnalyzer(settings);
    }

    internal ForwardWheelSequenceResult Rebuild(
        Cell?[,]? boardAfterPushRotate,
        IReadOnlyList<ForwardFeatureSpawnRequest>? featureRequests,
        IReadOnlyList<ForwardFutureTurn>? futureTurns,
        SymbolLedger? turnStartLedger,
        int topPrizeSymbol,
        int plannedTotalTurns,
        IReadOnlyList<ForwardSpawn>? sameTurnNormalSpawns = null)
    {
        if (boardAfterPushRotate == null)
            return Fail(ForwardWheelSequenceStatus.MissingBoard, "board after push and rotate is missing");
        if (featureRequests == null)
            return Fail(ForwardWheelSequenceStatus.MissingRequests, "same-turn feature requests are missing");
        if (futureTurns == null)
            return Fail(ForwardWheelSequenceStatus.MissingFutureTurns, "future turns are missing");
        if (turnStartLedger == null)
            return Fail(ForwardWheelSequenceStatus.MissingLedger, "turn-start symbol ledger is missing");
        if (boardAfterPushRotate.GetLength(0) != _settings.ROWS
            || boardAfterPushRotate.GetLength(1) != _settings.COLS)
        {
            return Fail(
                ForwardWheelSequenceStatus.InvalidBoardDimensions,
                $"board dimensions must be {_settings.ROWS}x{_settings.COLS}");
        }

        var cells = BuildExistingCells(boardAfterPushRotate, futureTurns);
        if (!cells.IsValid)
            return cells.Failure!;

        var sequenceCells = cells.Cells!;
        var workingLedger = turnStartLedger.Clone();
        var collectedWheelBonus = 0;
        var positions = new HashSet<(int Row, int Col)>();
        foreach (var spawn in sameTurnNormalSpawns ?? Array.Empty<ForwardSpawn>())
        {
            var spawnCheck = ValidateNormalSpawn(spawn, boardAfterPushRotate, positions);
            if (spawnCheck != null)
                return spawnCheck;

            var fate = _fateAnalyzer.Analyze(spawn.Row, spawn.Col, futureTurns);
            if (!fate.IsValid)
                return Fail(ForwardWheelSequenceStatus.CellFateInvalid, fate.Detail);

            if (fate.IsCollected)
            {
                var baseCollection = workingLedger.Collect(
                    spawn.Cell.Sym,
                    spawn.Cell.Stack,
                    fate.CollectedTurn,
                    topPrizeSymbol,
                    plannedTotalTurns);
                if (!baseCollection.IsValid)
                    return CollectionFailure(baseCollection);
            }

            sequenceCells.Add(SequenceCell.Normal(
                spawn.Row,
                spawn.Col,
                spawn.Cell.Sym,
                spawn.Cell.Stack,
                fate));
        }

        foreach (var request in featureRequests)
        {
            var requestCheck = ValidateRequest(request, boardAfterPushRotate, positions);
            if (requestCheck != null)
                return requestCheck;

            var fate = _fateAnalyzer.Analyze(request.Row, request.Col, futureTurns);
            if (!fate.IsValid)
                return Fail(ForwardWheelSequenceStatus.CellFateInvalid, fate.Detail);

            if (fate.IsCollected)
            {
                var baseCollection = workingLedger.Collect(
                    request.ConvertToSymbol,
                    stack: 1,
                    fate.CollectedTurn,
                    topPrizeSymbol,
                    plannedTotalTurns);
                if (!baseCollection.IsValid)
                    return CollectionFailure(baseCollection);
            }

            sequenceCells.Add(request.Kind == ForwardFeatureKind.Wheel
                ? SequenceCell.CreateWheel(request, fate)
                : SequenceCell.Normal(request.Row, request.Col, request.ConvertToSymbol, stack: 1, fate));
        }

        foreach (var wheel in featureRequests
                     .Where(request => request.Kind == ForwardFeatureKind.Wheel)
                     .OrderBy(request => request.Row)
                     .ThenBy(request => request.Col))
        {
            var wheelSymbol = wheel.WheelSymbol.GetValueOrDefault();
            var wheelCell = sequenceCells.Single(cell => cell.Row == wheel.Row && cell.Col == wheel.Col);
            var stackAdd = wheel.WheelStack.GetValueOrDefault() - 1;
            var affected = sequenceCells
                .Where(cell => !cell.IsWheel && cell.Symbol == wheelSymbol)
                .ToArray();
            if (affected.Length == 0)
            {
                return Fail(
                    ForwardWheelSequenceStatus.WheelHasNoTarget,
                    $"WHEEL ({wheel.Row},{wheel.Col}) has no symbol {wheelSymbol} available when it fires");
            }

            foreach (var cell in affected)
            {
                var nextStack = cell.Stack + stackAdd;
                if (nextStack > _settings.MAX_COIN_STACK)
                {
                    return Fail(
                        ForwardWheelSequenceStatus.StackOverflow,
                        $"WHEEL ({wheel.Row},{wheel.Col}) would change symbol {cell.Symbol} at ({cell.Row},{cell.Col}) " +
                        $"from stack {cell.Stack} to {nextStack}, above max {_settings.MAX_COIN_STACK}");
                }

                var reserve = ReserveBonus(
                    workingLedger,
                    cell.Symbol,
                    stackAdd,
                    cell.Fate,
                    topPrizeSymbol,
                    plannedTotalTurns);
                if (reserve != null)
                    return reserve;
                if (cell.Fate.IsCollected)
                    collectedWheelBonus += stackAdd;

                cell.Stack = nextStack;
            }

            wheelCell.ConvertWheel();
            var conversionBonus = wheelCell.Stack - 1;
            var conversionReserve = ReserveBonus(
                workingLedger,
                wheelCell.Symbol,
                conversionBonus,
                wheelCell.Fate,
                topPrizeSymbol,
                plannedTotalTurns);
            if (conversionReserve != null)
                return conversionReserve;
            if (wheelCell.Fate.IsCollected)
                collectedWheelBonus += conversionBonus;
        }

        return new ForwardWheelSequenceResult(
            ForwardWheelSequenceStatus.Valid,
            "same-turn WHEEL sequence is valid",
            workingLedger,
            collectedWheelBonus);
    }

    private CellBuildResult BuildExistingCells(
        Cell?[,] board,
        IReadOnlyList<ForwardFutureTurn> futureTurns)
    {
        var cells = new List<SequenceCell>();
        for (var row = 0; row < _settings.ROWS; row++)
        {
            for (var col = 0; col < _settings.COLS; col++)
            {
                var cell = board[row, col];
                if (cell == null)
                    continue;
                if (cell.IsFeat || cell.Sym <= 0 || cell.Stack <= 0 || cell.Stack > _settings.MAX_COIN_STACK)
                {
                    return CellBuildResult.Fail(Fail(
                        ForwardWheelSequenceStatus.InvalidBoardCell,
                        $"board cell ({row},{col}) has invalid symbol={cell.Sym}, stack={cell.Stack}, feature={cell.IsFeat}"));
                }

                var fate = _fateAnalyzer.Analyze(row, col, futureTurns);
                if (!fate.IsValid)
                    return CellBuildResult.Fail(Fail(ForwardWheelSequenceStatus.CellFateInvalid, fate.Detail));

                cells.Add(SequenceCell.Normal(row, col, cell.Sym, cell.Stack, fate));
            }
        }

        return CellBuildResult.Ok(cells);
    }

    private ForwardWheelSequenceResult? ValidateNormalSpawn(
        ForwardSpawn spawn,
        Cell?[,] board,
        HashSet<(int Row, int Col)> positions)
    {
        if (spawn.Row < 0 || spawn.Row >= _settings.ROWS
            || spawn.Col < 0 || spawn.Col >= _settings.COLS
            || spawn.Cell.Sym < 1
            || spawn.Cell.Sym > _settings.PrizeLadderRows.Count
            || _settings.IsFeat(spawn.Cell.Sym)
            || spawn.Cell.Stack <= 0
            || spawn.Cell.Stack > _settings.MAX_COIN_STACK)
        {
            return Fail(
                ForwardWheelSequenceStatus.InvalidNormalSpawn,
                $"invalid normal anchor at ({spawn.Row},{spawn.Col}) with symbol={spawn.Cell.Sym}, stack={spawn.Cell.Stack}");
        }

        if (board[spawn.Row, spawn.Col] != null || !positions.Add((spawn.Row, spawn.Col)))
        {
            return Fail(
                ForwardWheelSequenceStatus.InvalidNormalSpawn,
                $"normal anchor position ({spawn.Row},{spawn.Col}) is occupied or duplicated");
        }

        return null;
    }

    private ForwardWheelSequenceResult? ValidateRequest(
        ForwardFeatureSpawnRequest request,
        Cell?[,] board,
        HashSet<(int Row, int Col)> positions)
    {
        if (request.Row < 0 || request.Row >= _settings.ROWS
            || request.Col < 0 || request.Col >= _settings.COLS
            || request.Kind == ForwardFeatureKind.Unknown
            || !ValidSymbol(request.ConvertToSymbol))
        {
            return Fail(
                ForwardWheelSequenceStatus.InvalidFeatureRequest,
                $"invalid {request.Kind} request at ({request.Row},{request.Col}) with ConvertToId={request.ConvertToSymbol}");
        }

        if (!positions.Add((request.Row, request.Col)))
        {
            return Fail(
                ForwardWheelSequenceStatus.DuplicateFeaturePosition,
                $"feature position ({request.Row},{request.Col}) appears more than once");
        }

        if (board[request.Row, request.Col] != null)
        {
            return Fail(
                ForwardWheelSequenceStatus.FeaturePositionOccupied,
                $"feature position ({request.Row},{request.Col}) is occupied before spawning");
        }

        if (request.Kind != ForwardFeatureKind.Wheel)
            return null;

        var minStack = _settings.MIN_WHEEL_STACK_VALUE + 1;
        var maxStack = Math.Min(_settings.MAX_COIN_STACK, Math.Min(_settings.MAX_WHEEL_STACK_VALUE, 3) + 1);
        if (!request.WheelSymbol.HasValue
            || !ValidSymbol(request.WheelSymbol.Value)
            || !request.WheelStack.HasValue
            || request.WheelStack.Value < minStack
            || request.WheelStack.Value > maxStack)
        {
            return Fail(
                ForwardWheelSequenceStatus.InvalidFeatureRequest,
                $"invalid WHEEL request at ({request.Row},{request.Col}) with " +
                $"symbol={request.WheelSymbol}, stack={request.WheelStack}");
        }

        return null;
    }

    private ForwardWheelSequenceResult? ReserveBonus(
        SymbolLedger ledger,
        int symbol,
        int bonus,
        ForwardCellFate fate,
        int topPrizeSymbol,
        int plannedTotalTurns)
    {
        if (bonus <= 0 || !fate.IsCollected)
            return null;

        var collection = ledger.Collect(
            symbol,
            bonus,
            fate.CollectedTurn,
            topPrizeSymbol,
            plannedTotalTurns);
        return collection.IsValid ? null : CollectionFailure(collection);
    }

    private bool ValidSymbol(int symbol) =>
        symbol >= 1
        && symbol <= _settings.PrizeLadderRows.Count
        && !_settings.IsFeat(symbol);

    private static ForwardWheelSequenceResult CollectionFailure(SymbolCollectionCheck collection) =>
        Fail(ForwardWheelSequenceStatus.CollectionInvalid, collection.Detail);

    private static ForwardWheelSequenceResult Fail(ForwardWheelSequenceStatus status, string detail) =>
        new(status, detail, null, 0);

    private sealed class SequenceCell
    {
        private SequenceCell(
            int row,
            int col,
            int symbol,
            int stack,
            ForwardCellFate fate,
            ForwardFeatureSpawnRequest? wheel)
        {
            Row = row;
            Col = col;
            Symbol = symbol;
            Stack = stack;
            Fate = fate;
            Wheel = wheel;
        }

        internal int Row { get; }
        internal int Col { get; }
        internal int Symbol { get; private set; }
        internal int Stack { get; set; }
        internal ForwardCellFate Fate { get; }
        internal ForwardFeatureSpawnRequest? Wheel { get; private set; }
        internal bool IsWheel => Wheel.HasValue;

        internal static SequenceCell Normal(
            int row,
            int col,
            int symbol,
            int stack,
            ForwardCellFate fate) =>
            new(row, col, symbol, stack, fate, null);

        internal static SequenceCell CreateWheel(
            ForwardFeatureSpawnRequest request,
            ForwardCellFate fate) =>
            new(request.Row, request.Col, 0, 0, fate, request);

        internal void ConvertWheel()
        {
            var request = Wheel!.Value;
            Symbol = request.ConvertToSymbol;
            Stack = request.WheelSymbol == request.ConvertToSymbol
                ? request.WheelStack!.Value
                : 1;
            Wheel = null;
        }
    }

    private sealed class CellBuildResult
    {
        private CellBuildResult(
            IReadOnlyList<SequenceCell>? cells,
            ForwardWheelSequenceResult? failure)
        {
            Cells = cells?.ToList();
            Failure = failure;
        }

        internal List<SequenceCell>? Cells { get; }
        internal ForwardWheelSequenceResult? Failure { get; }
        internal bool IsValid => Failure == null;

        internal static CellBuildResult Ok(IReadOnlyList<SequenceCell> cells) =>
            new(cells, null);

        internal static CellBuildResult Fail(ForwardWheelSequenceResult failure) =>
            new(null, failure);
    }
}
