namespace CoinPusherEngine;

internal enum ForwardStartingBoardStatus
{
    Valid,
    MissingObjectives,
    MissingFramePlan,
    MissingLedger,
    IntentPlanningFailed,
    CellFateInvalid,
    SpawnPlanningFailed,
    BoardIncomplete,
}

internal sealed class ForwardStartingBoardResult
{
    internal ForwardStartingBoardResult(
        ForwardStartingBoardStatus status,
        string detail,
        Cell?[,]? board,
        int collectingCells,
        int residueCells,
        int winProgressCount,
        int nearMissProgressCount,
        int safeFillerCount)
    {
        Status = status;
        Detail = detail;
        Board = board;
        CollectingCells = collectingCells;
        ResidueCells = residueCells;
        WinProgressCount = winProgressCount;
        NearMissProgressCount = nearMissProgressCount;
        SafeFillerCount = safeFillerCount;
    }

    internal ForwardStartingBoardStatus Status { get; }
    internal string Detail { get; }
    internal Cell?[,]? Board { get; }
    internal int CollectingCells { get; }
    internal int ResidueCells { get; }
    internal int WinProgressCount { get; }
    internal int NearMissProgressCount { get; }
    internal int SafeFillerCount { get; }
    internal bool IsValid => Status == ForwardStartingBoardStatus.Valid;
}

internal sealed class ForwardStartingBoardPlanner
{
    private readonly Random _rng;
    private readonly ForwardCellFateAnalyzer _fateAnalyzer;

    internal ForwardStartingBoardPlanner(int seed)
    {
        _rng = new Random(seed);
        _fateAnalyzer = new ForwardCellFateAnalyzer();
    }

    internal ForwardStartingBoardResult Plan(
        ForwardObjectives? objectives,
        ForwardTurnFramePlan? framePlan,
        SymbolLedger? symbolLedger)
    {
        if (objectives == null)
            return Fail(ForwardStartingBoardStatus.MissingObjectives, "forward objectives are missing");
        if (framePlan == null)
            return Fail(ForwardStartingBoardStatus.MissingFramePlan, "turn frame plan is missing");
        if (symbolLedger == null)
            return Fail(ForwardStartingBoardStatus.MissingLedger, "symbol ledger is missing");

        var futureTurns = framePlan.FutureTurnsAfter(0);
        var positions = AllPositions().ToArray();
        var trialLedger = symbolLedger.Clone();
        var intents = new ForwardNormalIntentPlanner().Plan(
            turn: 0 + 1,
            objectives,
            positions,
            futureTurns,
            trialLedger);
        if (!intents.IsValid)
        {
            return Fail(
                ForwardStartingBoardStatus.IntentPlanningFailed,
                intents.Detail,
                collectingCells: intents.CollectingSlots,
                residueCells: intents.ResidueSlots);
        }

        var ordered = OrderPositionsByFate(positions, futureTurns);
        if (!ordered.IsValid) return ordered.Result!;

        var requests = BuildRequests(ordered.Positions, intents.Intents, futureTurns);

        var spawns = new ForwardSpawnPlanner(
            new ForwardSymbolSelector(
                trialLedger,
                objectives.WinTargets,
                objectives.NearMissTargets,
                objectives.FillSymbols,
                objectives.MaxSymbol,
                _rng,
                reservedWinCounts: FutureWheelReservations(objectives, futureTurns)),
            _fateAnalyzer).Plan(
                requests,
                futureTurns);
        if (!spawns.IsValid)
        {
            return Fail(
                ForwardStartingBoardStatus.SpawnPlanningFailed,
                spawns.Detail,
                collectingCells: intents.CollectingSlots,
                residueCells: intents.ResidueSlots);
        }

        var board = BuildBoard(spawns.Spawns);
        if (board == null)
        {
            return Fail(
                ForwardStartingBoardStatus.BoardIncomplete,
                "starting board was not fully populated",
                collectingCells: intents.CollectingSlots,
                residueCells: intents.ResidueSlots);
        }

        symbolLedger.ReplaceWith(trialLedger);
        return new ForwardStartingBoardResult(
            ForwardStartingBoardStatus.Valid,
            "ok",
            board,
            intents.CollectingSlots,
            intents.ResidueSlots,
            intents.WinProgressCount,
            intents.NearMissProgressCount,
            intents.SafeFillerCount);
    }

    private OrderedPositionResult OrderPositionsByFate(
        IReadOnlyList<(int r, int c)> positions,
        IReadOnlyList<ForwardFutureTurn> futureTurns)
    {
        var analyzed = new List<((int r, int c) Position, ForwardCellFate Fate)>(positions.Count);
        foreach (var position in positions)
        {
            var fate = _fateAnalyzer.Analyze(position.r, position.c, futureTurns);
            if (!fate.IsValid)
            {
                return OrderedPositionResult.Fail(Fail(
                    ForwardStartingBoardStatus.CellFateInvalid,
                    $"cell ({position.r},{position.c}) fate invalid: {fate.Detail}"));
            }

            analyzed.Add((position, fate));
        }

        var ordered = analyzed
            .Where(item => item.Fate.IsCollected)
            .OrderBy(item => item.Fate.CollectedTurn)
            .ThenBy(_ => _rng.Next())
            .Select(item => item.Position)
            .Concat(analyzed
                .Where(item => !item.Fate.IsCollected)
                .OrderBy(_ => _rng.Next())
                .Select(item => item.Position))
            .ToArray();

        return OrderedPositionResult.Ok(ordered);
    }

    private IReadOnlyList<ForwardSpawnCellRequest> BuildRequests(
        IReadOnlyList<(int r, int c)> positions,
        IReadOnlyList<ForwardNormalSpawnIntent> intents,
        IReadOnlyList<ForwardFutureTurn> futureTurns)
    {
        var analyzed = positions
            .Select(position => (Position: position, Fate: _fateAnalyzer.Analyze(position.r, position.c, futureTurns)))
            .ToArray();
        var collecting = analyzed
            .Where(item => item.Fate.IsCollected)
            .Select(item => item.Position)
            .OrderBy(_ => _rng.Next())
            .ToList();
        var residue = analyzed
            .Where(item => !item.Fate.IsCollected)
            .Select(item => item.Position)
            .OrderBy(_ => _rng.Next())
            .ToList();
        var requests = new List<ForwardSpawnCellRequest>(intents.Count);

        foreach (var intent in Shuffled(intents.Where(intent => intent.RequiresCollected)))
            AddCompatibleRequest(requests, collecting, analyzed, intent);
        foreach (var intent in Shuffled(intents.Where(intent => intent.RequiresResidue)))
            AddRequest(requests, residue, intent);
        foreach (var intent in Shuffled(intents.Where(intent => !intent.RequiresCollected && !intent.RequiresResidue)))
            AddRequest(requests, collecting.Count > 0 ? collecting : residue, intent);

        return OrderRequestsByCollectionTurn(requests, analyzed);
    }

    private void AddCompatibleRequest(
        List<ForwardSpawnCellRequest> requests,
        List<(int r, int c)> positions,
        IReadOnlyList<((int r, int c) Position, ForwardCellFate Fate)> analyzed,
        ForwardNormalSpawnIntent intent)
    {
        var compatible = positions
            .Where(position => CompatibleWithIntent(analyzed, position, intent))
            .ToArray();
        var pool = compatible.Length > 0
            ? compatible
            : positions.ToArray();
        var index = _rng.Next(pool.Length);
        var position = pool[index];
        positions.Remove(position);
        requests.Add(new ForwardSpawnCellRequest(
            position.r,
            position.c,
            intent.CollectIntent,
            intent.LedgerCollectionValue,
            intent.SpawnStack));
    }

    private void AddRequest(
        List<ForwardSpawnCellRequest> requests,
        List<(int r, int c)> positions,
        ForwardNormalSpawnIntent intent)
    {
        var index = _rng.Next(positions.Count);
        var position = positions[index];
        positions.RemoveAt(index);
        requests.Add(new ForwardSpawnCellRequest(
            position.r,
            position.c,
            intent.CollectIntent,
            intent.LedgerCollectionValue,
            intent.SpawnStack));
    }

    private static bool CompatibleWithIntent(
        IReadOnlyList<((int r, int c) Position, ForwardCellFate Fate)> analyzed,
        (int r, int c) position,
        ForwardNormalSpawnIntent intent)
    {
        var match = analyzed.First(item => item.Position == position);
        return intent.MatchesCollectionTurn(match.Fate.CollectedTurn);
    }

    private static IReadOnlyList<ForwardSpawnCellRequest> OrderRequestsByCollectionTurn(
        IReadOnlyList<ForwardSpawnCellRequest> requests,
        IReadOnlyList<((int r, int c) Position, ForwardCellFate Fate)> analyzed) =>
        requests
            .OrderBy(request => CollectionTurnFor(analyzed, request.Row, request.Col) ?? int.MaxValue)
            .ThenBy(request => IntentPriority(request.CollectIntent))
            .ThenBy(request => request.Row)
            .ThenBy(request => request.Col)
            .ToArray();

    private static int? CollectionTurnFor(
        IReadOnlyList<((int r, int c) Position, ForwardCellFate Fate)> analyzed,
        int row,
        int col) =>
        analyzed.First(item => item.Position == (row, col)).Fate.CollectedTurn;

    private static int IntentPriority(ForwardSymbolIntent intent) =>
        intent switch
        {
            ForwardSymbolIntent.MustProgressWin => 0,
            ForwardSymbolIntent.PreferNearMiss => 1,
            ForwardSymbolIntent.SafeFiller => 2,
            _ => 3,
        };

    private IReadOnlyList<ForwardNormalSpawnIntent> Shuffled(IEnumerable<ForwardNormalSpawnIntent> intents) =>
        intents.OrderBy(_ => _rng.Next()).ToArray();

    private Cell?[,]? BuildBoard(IReadOnlyList<ForwardSpawn> spawns)
    {
        if (spawns.Count != Settings.ROWS * Settings.COLS)
            return null;

        var board = new Cell?[Settings.ROWS, Settings.COLS];
        var seen = new HashSet<(int r, int c)>();
        foreach (var spawn in spawns)
        {
            if (spawn.Row < 0 || spawn.Row >= Settings.ROWS || spawn.Col < 0 || spawn.Col >= Settings.COLS)
                return null;
            if (!seen.Add((spawn.Row, spawn.Col)))
                return null;
            if (Settings.IsFeat(spawn.Cell.Sym))
                return null;

            board[spawn.Row, spawn.Col] = spawn.Cell.Clone();
        }

        for (var row = 0; row < Settings.ROWS; row++)
        {
            for (var col = 0; col < Settings.COLS; col++)
            {
                if (board[row, col] == null)
                    return null;
            }
        }

        return board;
    }

    private IEnumerable<(int r, int c)> AllPositions()
    {
        for (var row = 0; row < Settings.ROWS; row++)
        {
            for (var col = 0; col < Settings.COLS; col++)
                yield return (row, col);
        }
    }

    private static IReadOnlyDictionary<int, int> FutureWheelReservations(
        ForwardObjectives objectives,
        IReadOnlyList<ForwardFutureTurn> futureTurns) =>
        futureTurns
            .SelectMany(turn => turn.FeatureIntents)
            .Where(intent => intent.Kind == ForwardTimedFeatureKind.Wheel)
            .Where(intent => intent.WheelSymbol.HasValue)
            .Select(intent => intent.WheelSymbol!.Value)
            .Where(symbol => objectives.WinTargets.ContainsKey(symbol))
            .GroupBy(symbol => symbol)
            .ToDictionary(group => group.Key, group => group.Count());

    private static ForwardStartingBoardResult Fail(
        ForwardStartingBoardStatus status,
        string detail,
        int collectingCells = 0,
        int residueCells = 0) =>
        new(status, detail, null, collectingCells, residueCells, 0, 0, 0);

    private sealed class OrderedPositionResult
    {
        private OrderedPositionResult(
            IReadOnlyList<(int r, int c)> positions,
            ForwardStartingBoardResult? result)
        {
            Positions = positions;
            Result = result;
        }

        internal IReadOnlyList<(int r, int c)> Positions { get; }
        internal ForwardStartingBoardResult? Result { get; }
        internal bool IsValid => Result == null;

        internal static OrderedPositionResult Ok(IReadOnlyList<(int r, int c)> positions) =>
            new(positions, null);

        internal static OrderedPositionResult Fail(ForwardStartingBoardResult result) =>
            new(Array.Empty<(int r, int c)>(), result);
    }
}
