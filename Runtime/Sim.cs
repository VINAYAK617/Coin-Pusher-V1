namespace CoinPusherEngine;

/// <summary>
/// Shared forward simulation. Identical logic used by Verifier (plan-time)
/// and Engine (runtime). Any divergence between them is caught at ticket generation.
///
/// Pipeline per spin:
///   1. FlatStale  — convert lingering feature tokens from last spin to their CvtSym
///   2. Collect    — push cells off the board; accumulate win/filler totals
///   3. RotateCW   — 90-degree clockwise board rotation
///   4. ApplySpawns — write planned cells onto the rotated board
///   5. FireAll    — fire feature tokens
/// </summary>
internal static class Sim
{
    internal static Dictionary<int, int> Run(GamePlan plan) =>
        Run(plan, Settings);

    internal static Dictionary<int, int> Run(GamePlan plan, ICustomProfileSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        var totals   = new Dictionary<int, int>();
        var board    = CloneBoard(plan.Spins[0].Board, settings);
        int fallback = plan.FillSyms.Count > 0 ? plan.FillSyms[0] : settings.F_COIN;

        for (int i = 0; i < plan.Spins.Count; i++)
        {
            var sp   = plan.Spins[i];
            var next = i + 1 < plan.Spins.Count ? plan.Spins[i + 1] : null;

            FlatStale(board, settings);
            Collect(board, sp, totals, settings);
            board = RotCW(board, settings);
            ApplySpawns(board, sp);
            FireAll(board, sp, next, fallback, settings);
        }
        return totals;
    }

    // ── Phase 2: Collect ───────────────────────────────────────────────────
    private static void Collect(
        Cell?[,] board,
        SpinPlan sp,
        Dictionary<int, int> totals,
        ICustomProfileSettings settings)
    {
        for (int col = 0; col < settings.COLS; col++)
        {
            if (sp.Flush[col])
            {
                for (int row = 0; row < settings.ROWS; row++)
                {
                    Acc(totals, board[row, col], settings);
                    board[row, col] = null;
                }
            }
            else
            {
                int push = sp.Push[col];
                for (int r = settings.ROWS - push; r < settings.ROWS; r++)
                    Acc(totals, board[r, col], settings);

                for (int r = settings.ROWS - 1; r >= 0; r--)
                {
                    int src = r - push;
                    board[r, col] = src >= 0 ? board[src, col]?.Clone() : null;
                }
            }
        }
    }

    // ── Phase 5: FireAll ───────────────────────────────────────────────────
    internal static void FireAll(Cell?[,] board, SpinPlan sp, SpinPlan? next, int fallback)
    {
        FireAll(board, sp, next, fallback, Settings);
    }

    internal static void FireAll(
        Cell?[,] board,
        SpinPlan sp,
        SpinPlan? next,
        int fallback,
        ICustomProfileSettings settings)
    {
        FireFeaturePass(board, next, fallback, wheelPass: false, settings);
        FireFeaturePass(board, next, fallback, wheelPass: true, settings);
    }

    private static void FireFeaturePass(
        Cell?[,] board,
        SpinPlan? next,
        int fallback,
        bool wheelPass,
        ICustomProfileSettings settings)
    {
        bool any;
        do
        {
            any = false;
            for (int r = 0; r < settings.ROWS; r++)
            {
                for (int c = 0; c < settings.COLS; c++)
                {
                    var fc = board[r, c];
                    if (fc?.IsFeat != true) continue;

                    var isWheel = fc.Sym == settings.F_WHEEL || fc.FeatId == "WHEEL";
                    if (isWheel != wheelPass) continue;

                    if (isWheel)
                        FireWheel(board, fc.Fp, settings);

                    board[r, c] = Cvt(fc, settings);
                    any = true;
                }
            }
        }
        while (any && board.Cast<Cell?>().Any(x =>
        {
            if (x?.IsFeat != true) return false;
            var isWheel = x.Sym == settings.F_WHEEL || x.FeatId == "WHEEL";
            return isWheel == wheelPass;
        }));
    }

    private static void FireWheel(Cell?[,] board, FP? fp, ICustomProfileSettings settings)
    {
        int sym = fp?.WheelSym ?? 0;
        int stack = fp?.WheelStack ?? 1;
        if (sym == 0 || stack <= 1) return;

        for (int row = 0; row < settings.ROWS; row++)
        {
            for (int col = 0; col < settings.COLS; col++)
            {
                var cell = board[row, col];
                if (cell != null && !cell.IsFeat && cell.Sym == sym)
                    cell.Stack = Math.Min(settings.MAX_COIN_STACK, cell.Stack + stack - 1);
            }
        }
    }

    private static void ApplySpawns(Cell?[,] board, SpinPlan sp)
    {
        foreach (var kv in sp.Spawns)
        {
            board[kv.Key.Item1, kv.Key.Item2] = kv.Value.Clone();
        }
    }

    // ── Phase 1: FlatStale ─────────────────────────────────────────────────
    internal static void FlatStale(Cell?[,] board)
    {
        FlatStale(board, Settings);
    }

    internal static void FlatStale(Cell?[,] board, ICustomProfileSettings settings)
    {
        for (int r = 0; r < settings.ROWS; r++)
        {
            for (int c = 0; c < settings.COLS; c++)
            {
                var cell = board[r, c];
                if (cell?.IsFeat != true) continue;
                board[r, c] = Cvt(cell, settings);
            }
        }
    }

    private static void Acc(Dictionary<int, int> totals, Cell? cell, ICustomProfileSettings settings)
    {
        if (cell == null || settings.IsFeat(cell.Sym)) return;
        totals.TryGetValue(cell.Sym, out int existing);
        totals[cell.Sym] = existing + Math.Max(1, cell.Stack);
    }

    private static Cell Cvt(Cell fc, ICustomProfileSettings settings)
    {
        int id = RequireConvertSymbol(fc, settings);
        var converted = Grid.Norm(id);
        if ((fc.Sym == settings.F_WHEEL || fc.FeatId == "WHEEL")
            && fc.Fp?.WheelSym == id)
        {
            converted.Stack = Math.Min(
                settings.MAX_COIN_STACK,
                Math.Max(1, fc.Fp?.WheelStack ?? 1));
        }

        return converted;
    }

    private static int RequireConvertSymbol(Cell fc, ICustomProfileSettings settings)
    {
        if (fc.CvtSym > 0 && !settings.IsFeat(fc.CvtSym))
            return fc.CvtSym;

        throw new InvalidOperationException(
            $"Feature symbol {fc.Sym} has invalid ConvertToId={fc.CvtSym}; " +
            "feature conversion must target a normal symbol.");
    }

    private static Cell?[,] CloneBoard(Cell?[,] source, ICustomProfileSettings settings)
    {
        var clone = new Cell?[settings.ROWS, settings.COLS];
        for (int row = 0; row < settings.ROWS; row++)
        {
            for (int col = 0; col < settings.COLS; col++)
                clone[row, col] = source[row, col]?.Clone();
        }
        return clone;
    }

    private static Cell?[,] RotCW(Cell?[,] source, ICustomProfileSettings settings)
    {
        var rotated = new Cell?[settings.ROWS, settings.COLS];
        for (int row = 0; row < settings.ROWS; row++)
        {
            for (int col = 0; col < settings.COLS; col++)
                rotated[col, settings.ROWS - 1 - row] = source[row, col]?.Clone();
        }
        return rotated;
    }
}
