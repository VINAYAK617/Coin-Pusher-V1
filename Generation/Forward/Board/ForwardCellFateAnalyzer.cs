namespace CoinPusherEngine;

internal enum ForwardCellFateStatus
{
    Valid,
    InvalidStartPosition,
    InvalidTurnNumber,
    InvalidTurnShape,
}

internal enum ForwardCellFateKind
{
    Collected,
    Survives,
}

internal readonly struct ForwardFutureTurn
{
    internal ForwardFutureTurn(int turnNumber, ForwardTurnShape shape)
    {
        TurnNumber = turnNumber;
        Shape = shape;
    }

    internal int TurnNumber { get; }
    internal ForwardTurnShape Shape { get; }
}

internal readonly struct ForwardCellFate
{
    internal ForwardCellFate(
        ForwardCellFateStatus status,
        ForwardCellFateKind kind,
        int startRow,
        int startCol,
        int finalRow,
        int finalCol,
        int? collectedTurn,
        string detail)
    {
        Status = status;
        Kind = kind;
        StartRow = startRow;
        StartCol = startCol;
        FinalRow = finalRow;
        FinalCol = finalCol;
        CollectedTurn = collectedTurn;
        Detail = detail;
    }

    internal ForwardCellFateStatus Status { get; }
    internal ForwardCellFateKind Kind { get; }
    internal int StartRow { get; }
    internal int StartCol { get; }
    internal int FinalRow { get; }
    internal int FinalCol { get; }
    internal int? CollectedTurn { get; }
    internal string Detail { get; }
    internal bool IsValid => Status == ForwardCellFateStatus.Valid;
    internal bool IsCollected => IsValid && Kind == ForwardCellFateKind.Collected;
}

internal sealed class ForwardCellFateAnalyzer
{
    private readonly ICustomProfileSettings _settings;

    internal ForwardCellFateAnalyzer(ICustomProfileSettings settings)
    {
        _settings = settings;
    }

    internal ForwardCellFate Analyze(
        int row,
        int col,
        IReadOnlyList<ForwardFutureTurn> futureTurns)
    {
        if (row < 0 || row >= _settings.ROWS || col < 0 || col >= _settings.COLS)
        {
            return new ForwardCellFate(
                ForwardCellFateStatus.InvalidStartPosition,
                ForwardCellFateKind.Survives,
                row,
                col,
                row,
                col,
                null,
                $"start position ({row},{col}) outside board");
        }

        var currentRow = row;
        var currentCol = col;
        var previousTurn = 0;

        foreach (var future in futureTurns)
        {
            if (future.Shape == null)
            {
                return new ForwardCellFate(
                    ForwardCellFateStatus.InvalidTurnShape,
                    ForwardCellFateKind.Survives,
                    row,
                    col,
                    currentRow,
                    currentCol,
                    null,
                    $"turn {future.TurnNumber} has no shape");
            }

            if (future.TurnNumber <= previousTurn)
            {
                return new ForwardCellFate(
                    ForwardCellFateStatus.InvalidTurnNumber,
                    ForwardCellFateKind.Survives,
                    row,
                    col,
                    currentRow,
                    currentCol,
                    null,
                    $"turn {future.TurnNumber} is not after previous turn {previousTurn}");
            }

            previousTurn = future.TurnNumber;
            var pusher = future.Shape.Pushers[currentCol];
            if (pusher.IsFlush(_settings))
            {
                return Collected(row, col, currentRow, currentCol, future.TurnNumber);
            }

            if (currentRow >= _settings.ROWS - pusher.PushValue)
            {
                return Collected(row, col, currentRow, currentCol, future.TurnNumber);
            }

            currentRow += pusher.PushValue;
            (currentRow, currentCol) = RotateClockwise(currentRow, currentCol);
        }

        return new ForwardCellFate(
            ForwardCellFateStatus.Valid,
            ForwardCellFateKind.Survives,
            row,
            col,
            currentRow,
            currentCol,
            null,
            $"survives through {futureTurns.Count} future turn(s)");
    }

    private ForwardCellFate Collected(
        int startRow,
        int startCol,
        int row,
        int col,
        int turn) =>
        new(
            ForwardCellFateStatus.Valid,
            ForwardCellFateKind.Collected,
            startRow,
            startCol,
            row,
            col,
            turn,
            $"collected on turn {turn} at ({row},{col})");

    private (int row, int col) RotateClockwise(int row, int col) =>
        (col, _settings.ROWS - 1 - row);
}
