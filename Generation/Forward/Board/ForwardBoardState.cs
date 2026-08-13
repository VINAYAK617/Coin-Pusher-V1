namespace CoinPusherEngine;

internal enum ForwardBoardAdvanceStatus
{
    Valid,
    MissingShape,
    SpawnCountMismatch,
    SpawnOutOfRange,
    DuplicateSpawnPosition,
    SpawnOverwritesOccupiedCell,
    EmptyCellsRemainAfterSpawns,
}

internal enum ForwardBoardFeatureFireStatus
{
    Valid,
    MissingExecutor,
    FeatureFireLeftFeatureCells,
    FeatureFireLeftEmptyCells,
}

internal readonly struct ForwardBoardFeatureFireApplyResult
{
    internal ForwardBoardFeatureFireApplyResult(
        ForwardBoardFeatureFireStatus status,
        string detail,
        IReadOnlyList<ForwardFeatureFireEvent> events)
    {
        Status = status;
        Detail = detail;
        Events = events;
    }

    internal ForwardBoardFeatureFireStatus Status { get; }
    internal string Detail { get; }
    internal IReadOnlyList<ForwardFeatureFireEvent> Events { get; }
    internal bool IsValid => Status == ForwardBoardFeatureFireStatus.Valid;
}

internal readonly struct ForwardBoardPreviewResult
{
    internal ForwardBoardPreviewResult(
        ForwardBoardAdvanceStatus status,
        string detail,
        IReadOnlyDictionary<int, int> collected,
        IReadOnlyList<(int r, int c)> emptyPositions,
        Cell?[,] boardAfterPushRotate)
    {
        Status = status;
        Detail = detail;
        Collected = collected;
        EmptyPositions = emptyPositions;
        BoardAfterPushRotate = boardAfterPushRotate;
    }

    internal ForwardBoardAdvanceStatus Status { get; }
    internal string Detail { get; }
    internal IReadOnlyDictionary<int, int> Collected { get; }
    internal IReadOnlyList<(int r, int c)> EmptyPositions { get; }
    internal Cell?[,] BoardAfterPushRotate { get; }
    internal bool IsValid => Status == ForwardBoardAdvanceStatus.Valid;
}

internal readonly struct ForwardSpawn
{
    internal ForwardSpawn(int row, int col, Cell cell)
    {
        Row = row;
        Col = col;
        Cell = cell;
    }

    internal int Row { get; }
    internal int Col { get; }
    internal Cell Cell { get; }
}

internal readonly struct ForwardBoardAdvanceResult
{
    internal ForwardBoardAdvanceResult(
        ForwardBoardAdvanceStatus status,
        string detail,
        IReadOnlyDictionary<int, int> collected)
    {
        Status = status;
        Detail = detail;
        Collected = collected;
    }

    internal ForwardBoardAdvanceStatus Status { get; }
    internal string Detail { get; }
    internal IReadOnlyDictionary<int, int> Collected { get; }
    internal bool IsValid => Status == ForwardBoardAdvanceStatus.Valid;
}

internal sealed class ForwardBoardState
{
    private Cell?[,] _board;

    internal ForwardBoardState(Cell?[,] board)
    {
        _board = CloneBoard(board, Settings);
    }

    internal Cell?[,] Snapshot() => CloneBoard(_board, Settings);

    internal ForwardBoardFeatureFireApplyResult ApplyFeatureFire(ForwardFeatureExecutor? executor)
    {
        if (executor == null)
        {
            return new ForwardBoardFeatureFireApplyResult(
                ForwardBoardFeatureFireStatus.MissingExecutor,
                "feature executor is missing",
                Array.Empty<ForwardFeatureFireEvent>());
        }

        var working = CloneBoard(_board, Settings);
        var fire = executor.FireAll(working);

        if (HasFeatureCells(working))
        {
            return new ForwardBoardFeatureFireApplyResult(
                ForwardBoardFeatureFireStatus.FeatureFireLeftFeatureCells,
                "feature fire left one or more feature cells on the board",
                fire.Events);
        }

        if (CountEmptyCells(working) != 0)
        {
            return new ForwardBoardFeatureFireApplyResult(
                ForwardBoardFeatureFireStatus.FeatureFireLeftEmptyCells,
                "feature fire left one or more empty cells on the board",
                fire.Events);
        }

        _board = working;
        return new ForwardBoardFeatureFireApplyResult(
            ForwardBoardFeatureFireStatus.Valid,
            "ok",
            fire.Events);
    }

    internal ForwardBoardPreviewResult PreviewAfterPushRotate(ForwardTurnShape shape)
    {
        if (shape == null)
        {
            return new ForwardBoardPreviewResult(
                ForwardBoardAdvanceStatus.MissingShape,
                "turn shape is missing",
                new Dictionary<int, int>(),
                Array.Empty<(int r, int c)>(),
                Snapshot());
        }

        var collected = new Dictionary<int, int>();
        var working = CloneBoard(_board, Settings);
        CollectAndShift(working, shape, collected);
        working = RotateClockwise(working, Settings);

        return new ForwardBoardPreviewResult(
            ForwardBoardAdvanceStatus.Valid,
            "ok",
            collected,
            EmptyPositions(working),
            CloneBoard(working, Settings));
    }

    internal ForwardBoardAdvanceResult Advance(
        ForwardTurnShape shape,
        IReadOnlyList<ForwardSpawn> spawns)
    {
        if (shape == null)
        {
            return new ForwardBoardAdvanceResult(
                ForwardBoardAdvanceStatus.MissingShape,
                "turn shape is missing",
                new Dictionary<int, int>());
        }

        var collected = new Dictionary<int, int>();
        var working = CloneBoard(_board, Settings);

        CollectAndShift(working, shape, collected);
        working = RotateClockwise(working, Settings);

        var empty = CountEmptyCells(working);
        if (spawns.Count != empty)
        {
            return new ForwardBoardAdvanceResult(
                ForwardBoardAdvanceStatus.SpawnCountMismatch,
                $"spawns={spawns.Count}, empty cells after push+rotate={empty}",
                collected);
        }

        var seen = new HashSet<(int r, int c)>();
        foreach (var spawn in spawns)
        {
            if (spawn.Row < 0 || spawn.Row >= Settings.ROWS || spawn.Col < 0 || spawn.Col >= Settings.COLS)
            {
                return new ForwardBoardAdvanceResult(
                    ForwardBoardAdvanceStatus.SpawnOutOfRange,
                    $"spawn ({spawn.Row},{spawn.Col}) outside board",
                    collected);
            }

            var key = (spawn.Row, spawn.Col);
            if (!seen.Add(key))
            {
                return new ForwardBoardAdvanceResult(
                    ForwardBoardAdvanceStatus.DuplicateSpawnPosition,
                    $"spawn ({spawn.Row},{spawn.Col}) appears more than once",
                    collected);
            }

            if (working[spawn.Row, spawn.Col] != null)
            {
                return new ForwardBoardAdvanceResult(
                    ForwardBoardAdvanceStatus.SpawnOverwritesOccupiedCell,
                    $"spawn ({spawn.Row},{spawn.Col}) overwrites occupied cell",
                    collected);
            }

            working[spawn.Row, spawn.Col] = spawn.Cell.Clone();
        }

        if (CountEmptyCells(working) != 0)
        {
            return new ForwardBoardAdvanceResult(
                ForwardBoardAdvanceStatus.EmptyCellsRemainAfterSpawns,
                "board still has empty cells after applying all spawns",
                collected);
        }

        _board = working;
        return new ForwardBoardAdvanceResult(
            ForwardBoardAdvanceStatus.Valid,
            "ok",
            collected);
    }

    private void CollectAndShift(Cell?[,] board, ForwardTurnShape shape, Dictionary<int, int> collected)
    {
        for (var col = 0; col < Settings.COLS; col++)
        {
            var pusher = shape.Pushers[col];
            if (pusher.IsFlush())
            {
                for (var row = 0; row < Settings.ROWS; row++)
                {
                    Acc(collected, board[row, col]);
                    board[row, col] = null;
                }
                continue;
            }

            var push = pusher.PushValue;
            for (var row = Settings.ROWS - push; row < Settings.ROWS; row++)
                Acc(collected, board[row, col]);

            for (var row = Settings.ROWS - 1; row >= 0; row--)
            {
                var source = row - push;
                board[row, col] = source >= 0 ? board[source, col]?.Clone() : null;
            }
        }
    }

    private void Acc(Dictionary<int, int> collected, Cell? cell)
    {
        if (cell == null || Settings.IsFeat(cell.Sym)) return;
        collected[cell.Sym] = collected.GetValueOrDefault(cell.Sym) + Math.Max(1, cell.Stack);
    }

    private int CountEmptyCells(Cell?[,] board)
    {
        var count = 0;
        for (var row = 0; row < Settings.ROWS; row++)
        {
            for (var col = 0; col < Settings.COLS; col++)
            {
                if (board[row, col] == null) count++;
            }
        }
        return count;
    }

    private bool HasFeatureCells(Cell?[,] board)
    {
        for (var row = 0; row < Settings.ROWS; row++)
        {
            for (var col = 0; col < Settings.COLS; col++)
            {
                if (board[row, col]?.IsFeat == true)
                    return true;
            }
        }

        return false;
    }

    private IReadOnlyList<(int r, int c)> EmptyPositions(Cell?[,] board)
    {
        var positions = new List<(int r, int c)>();
        for (var row = 0; row < Settings.ROWS; row++)
        {
            for (var col = 0; col < Settings.COLS; col++)
            {
                if (board[row, col] == null)
                    positions.Add((row, col));
            }
        }

        return positions;
    }

    private static Cell?[,] CloneBoard(Cell?[,] source, GameEngine.ICustomProfileSettings settings)
    {
        var clone = new Cell?[settings.ROWS, settings.COLS];
        for (var row = 0; row < settings.ROWS; row++)
        {
            for (var col = 0; col < settings.COLS; col++)
                clone[row, col] = source[row, col]?.Clone();
        }
        return clone;
    }

    private static Cell?[,] RotateClockwise(Cell?[,] source, GameEngine.ICustomProfileSettings settings)
    {
        var rotated = new Cell?[settings.ROWS, settings.COLS];
        for (var row = 0; row < settings.ROWS; row++)
        {
            for (var col = 0; col < settings.COLS; col++)
                rotated[col, settings.ROWS - 1 - row] = source[row, col]?.Clone();
        }
        return rotated;
    }
}
