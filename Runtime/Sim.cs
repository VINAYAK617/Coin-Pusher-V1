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
    internal static Dictionary<int, int> Run(GamePlan plan)
    {
        var totals   = new Dictionary<int, int>();
        var board    = Grid.Clone(plan.Spins[0].Board);
        int fallback = plan.FillSyms.Count > 0 ? plan.FillSyms[0] : Settings.F_COIN;

        for (int i = 0; i < plan.Spins.Count; i++)
        {
            var sp   = plan.Spins[i];
            var next = i + 1 < plan.Spins.Count ? plan.Spins[i + 1] : null;

            FlatStale(board);
            Collect(board, sp, totals);
            board = Grid.RotCW(board);
            ApplySpawns(board, sp);
            FireAll(board, sp, next, fallback);
        }
        return totals;
    }

    // ── Phase 2: Collect ───────────────────────────────────────────────────
    private static void Collect(Cell?[,] board, SpinPlan sp, Dictionary<int, int> totals)
    {
        for (int col = 0; col < Settings.COLS; col++)
        {
            if (sp.Flush[col])
            {
                var ctx = new FireCtx { Board = board, Col = col, Fp = new FP() };
                foreach (var cell in FeatReg.Get("FLUSH").Collect(ctx))
                    Acc(totals, cell.Sym, cell.Stack);
            }
            else
            {
                int push = sp.Push[col];
                for (int r = Settings.ROWS - push; r < Settings.ROWS; r++)
                {
                    if (board[r, col] != null)
                        Acc(totals, board[r, col]!.Sym, board[r, col]!.Stack);
                }

                for (int r = Settings.ROWS - 1; r >= 0; r--)
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
        FireFeaturePass(board, next, fallback, wheelPass: false);
        FireFeaturePass(board, next, fallback, wheelPass: true);
    }

    private static void FireFeaturePass(Cell?[,] board, SpinPlan? next, int fallback, bool wheelPass)
    {
        bool any;
        do
        {
            any = false;
            for (int r = 0; r < Settings.ROWS; r++)
            {
                for (int c = 0; c < Settings.COLS; c++)
                {
                    var fc = board[r, c];
                    if (fc?.IsFeat != true) continue;

                    Feat? feat = fc.FeatId != null && FeatReg.Has(fc.FeatId)
                                 ? FeatReg.Get(fc.FeatId)
                                 : FeatReg.HasSym(fc.Sym) ? FeatReg.GetSym(fc.Sym) : null;

                    var isWheel = feat?.Id == "WHEEL" || fc.Sym == Settings.F_WHEEL;
                    if (isWheel != wheelPass) continue;

                    if (feat == null) { board[r, c] = Cvt(fc); any = true; continue; }

                    feat.Fire(new FireCtx { Board=board, Col=c, Fp=fc.Fp ?? new FP { FeatId=feat.Id } });

                    board[r, c] = Cvt(fc);
                    any = true;
                }
            }
        }
        while (any && board.Cast<Cell?>().Any(x =>
        {
            if (x?.IsFeat != true) return false;
            var isWheel = x.Sym == Settings.F_WHEEL || x.FeatId == "WHEEL";
            return isWheel == wheelPass;
        }));
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
        for (int r = 0; r < Settings.ROWS; r++)
        {
            for (int c = 0; c < Settings.COLS; c++)
            {
                var cell = board[r, c];
                if (cell?.IsFeat != true) continue;
                board[r, c] = Cvt(cell);
            }
        }
    }

    private static void Acc(Dictionary<int, int> d, int sym, int n)
    {
        if (Settings.IsFeat(sym)) return;
        d.TryGetValue(sym, out int ex);
        d[sym] = ex + n;
    }

    private static Cell Cvt(Cell fc)
    {
        int id = fc.CvtSym > 0 && !Settings.IsFeat(fc.CvtSym) ? fc.CvtSym : Settings.F_COIN;
        var converted = Grid.Norm(id);
        if ((fc.Sym == Settings.F_WHEEL || fc.FeatId == "WHEEL")
            && fc.Fp?.WheelSym == id)
        {
            converted.Stack = Math.Min(
                Settings.MAX_COIN_STACK,
                Math.Max(1, fc.Fp?.WheelStack ?? 1));
        }

        return converted;
    }
}
