namespace CoinPusherEngine;

internal sealed class ForwardFeatureFireEvent
{
    internal int Row { get; init; }
    internal int Col { get; init; }
    internal int FeatureSymbol { get; init; }
    internal int ConvertToSymbol { get; init; }
    internal int? WheelSymbol { get; init; }
    internal int? WheelStack { get; init; }
    internal int? UpgradeSymbol { get; init; }
    internal int? UpgradeTier { get; init; }
    internal int ExtraGoAward { get; init; }
}

internal sealed class ForwardFeatureFireResult
{
    internal List<ForwardFeatureFireEvent> Events { get; } = new();
}

internal sealed class ForwardFeatureExecutor
{
    internal ForwardFeatureFireResult FireAll(Cell?[,] board)
    {
        var result = new ForwardFeatureFireResult();
        FirePass(board, result, wheelPass: false);
        FirePass(board, result, wheelPass: true);
        return result;
    }

    private void FirePass(Cell?[,] board, ForwardFeatureFireResult result, bool wheelPass)
    {
        bool any;
        do
        {
            any = false;
            for (var row = 0; row < Settings.ROWS; row++)
            {
                for (var col = 0; col < Settings.COLS; col++)
                {
                    var featureCell = board[row, col];
                    if (featureCell?.IsFeat != true) continue;
                    if (IsWheel(featureCell) != wheelPass) continue;

                    if (IsWheel(featureCell))
                        FireWheel(board, featureCell);

                    var convertTo = ConvertTarget(featureCell);
                    board[row, col] = ConvertCell(featureCell, convertTo);
                    result.Events.Add(new ForwardFeatureFireEvent
                    {
                        Row = row,
                        Col = col,
                        FeatureSymbol = featureCell.Sym,
                        ConvertToSymbol = convertTo,
                        WheelSymbol = IsWheel(featureCell) ? featureCell.Fp?.WheelSym : null,
                        WheelStack = IsWheel(featureCell) ? featureCell.Fp?.WheelStack : null,
                        UpgradeSymbol = featureCell.Sym == Settings.F_PRUP ? featureCell.Fp?.PrupSym : null,
                        UpgradeTier = featureCell.Sym == Settings.F_PRUP ? featureCell.Fp?.PrupTier : null,
                        ExtraGoAward = featureCell.Sym == Settings.F_XSPIN ? 1 : 0,
                    });
                    any = true;
                }
            }
        }
        while (any && HasFeatureForPass(board, wheelPass));
    }

    private void FireWheel(Cell?[,] board, Cell featureCell)
    {
        var symbol = featureCell.Fp?.WheelSym ?? 0;
        var stack = featureCell.Fp?.WheelStack ?? 1;
        if (symbol <= 0 || Settings.IsFeat(symbol) || stack <= 1) return;

        for (var row = 0; row < Settings.ROWS; row++)
        {
            for (var col = 0; col < Settings.COLS; col++)
            {
                var cell = board[row, col];
                if (cell == null || cell.IsFeat || cell.Sym != symbol) continue;
                cell.Stack = Math.Min(Settings.MAX_COIN_STACK, cell.Stack + stack - 1);
            }
        }
    }

    private bool HasFeatureForPass(Cell?[,] board, bool wheelPass)
    {
        for (var row = 0; row < Settings.ROWS; row++)
        {
            for (var col = 0; col < Settings.COLS; col++)
            {
                var cell = board[row, col];
                if (cell?.IsFeat == true && IsWheel(cell) == wheelPass)
                    return true;
            }
        }

        return false;
    }

    private bool IsWheel(Cell cell) =>
        cell.Sym == Settings.F_WHEEL || cell.FeatId == "WHEEL";

    private int ConvertTarget(Cell cell)
    {
        if (IsNormalSymbolId(cell.CvtSym))
            return cell.CvtSym;

        throw new InvalidOperationException(
            $"Feature symbol {cell.Sym} has invalid ConvertToId={cell.CvtSym}; " +
            "feature conversion must target a normal symbol.");
    }

    private static bool IsNormalSymbolId(int symbol) =>
        symbol >= 1
        && symbol <= Settings.PrizeLadderRows.Count
        && !Settings.IsFeat(symbol);

    private Cell ConvertCell(Cell featureCell, int convertTo)
    {
        var converted = Grid.Norm(convertTo);
        if (IsWheel(featureCell) && featureCell.Fp?.WheelSym == convertTo)
        {
            converted.Stack = Math.Min(
                Settings.MAX_COIN_STACK,
                Math.Max(1, featureCell.Fp?.WheelStack ?? 1));
        }

        return converted;
    }
}
