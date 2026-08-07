namespace CoinPusherEngine;

internal static class Verifier
{
    internal static void Check(GamePlan plan)
    {
        var got = Sim.Run(plan);

        foreach (var spin in plan.Spins)
        {
            for (var col = 0; col < Settings.Default.COLS; col++)
            {
                var push = spin.Push[col];
                if (spin.Flush[col])
                {
                    if (push != Settings.Default.ROWS)
                        throw new InvalidOperationException(
                            $"VERIFY FAIL spin={spin.Spin} col={col} FLUSH push={push} want={Settings.Default.ROWS}");
                }
                else if (push < Settings.Default.MIN_PUSH || push > Settings.Default.MAX_PUSH)
                {
                    throw new InvalidOperationException(
                        $"VERIFY FAIL spin={spin.Spin} col={col} push={push} outside {Settings.Default.MIN_PUSH}..{Settings.Default.MAX_PUSH}");
                }
            }
        }

        foreach (var (sym, target) in plan.Targets)
        {
            got.TryGetValue(sym, out int g);
            if (g != target)
                throw new InvalidOperationException(
                    $"VERIFY FAIL sym={sym} got={g} want={target}");
        }

        foreach (var (sym, target) in plan.NonWinTargets)
        {
            got.TryGetValue(sym, out int g);
            var cap = Settings.Default.SymbolFillCap(sym);
            if (g < target || g >= cap)
                throw new InvalidOperationException(
                    $"VERIFY FAIL nonwin sym={sym} got={g} want>={target} and <{cap}");
        }

        if (plan.WinSyms.Count > 0
            && !plan.WinSyms.Any(sym => plan.Spins[^1].Alloc.GetValueOrDefault(sym) > 0))
            throw new InvalidOperationException("VERIFY FAIL last spin has no win alloc");

        var finalFeature = plan.Spins[^1].Spawns.Values.FirstOrDefault(cell => cell.IsFeat);
        if (finalFeature != null)
            throw new InvalidOperationException(
                $"VERIFY FAIL feature token {finalFeature.Sym} in final spin spawns");

        var extraSpinTokens = plan.Spins
            .SelectMany(spin => spin.Spawns.Values)
            .Count(cell => cell.IsFeat && cell.Sym == Settings.Default.F_XSPIN);
        if (plan.TotalSpins != Settings.Default.BASE_SPINS + extraSpinTokens)
        {
            throw new InvalidOperationException(
                $"VERIFY FAIL TotalSpins={plan.TotalSpins} but BASE_SPINS+EXTRA_SPIN={Settings.Default.BASE_SPINS + extraSpinTokens}");
        }

        foreach (var (spin, index) in plan.Spins.Select((spin, index) => (spin, index)))
        {
            var extrasThisTurn = spin.Spawns.Values.Count(cell => cell.IsFeat && cell.Sym == Settings.Default.F_XSPIN);
            var remainingFutureTurns = plan.Spins.Count - (index + 1);
            var maxExtrasThisTurn = Math.Min(Settings.Default.MAX_EXTRA_GO_PER_TURN, remainingFutureTurns);
            if (extrasThisTurn > maxExtrasThisTurn)
            {
                throw new InvalidOperationException(
                    $"VERIFY FAIL spin {index + 1} has {extrasThisTurn} EXTRA_SPIN tokens but only {remainingFutureTurns} future turns remain");
            }
        }

        var topPrizeSym = TopPrizeSymbol(plan);
        if (topPrizeSym > 0)
        {
            if (plan.Targets.ContainsKey(topPrizeSym)
                && plan.Spins[^1].Alloc.GetValueOrDefault(topPrizeSym) <= 0)
            {
                throw new InvalidOperationException(
                    $"VERIFY FAIL top prize sym={topPrizeSym} does not complete on final spin");
            }

            var upgradesTopPrize = plan.Spins
                .SelectMany(spin => spin.Spawns.Values)
                .Any(cell => cell.IsFeat && cell.Sym == Settings.Default.F_PRUP && cell.Fp?.PrupSym == topPrizeSym);
            if (upgradesTopPrize)
                throw new InvalidOperationException($"VERIFY FAIL top prize sym={topPrizeSym} received PRIZE_UPGRADE");
        }

        foreach (var cell in plan.Spins.SelectMany(spin => spin.Spawns.Values))
        {
            if (cell.Stack > Settings.Default.MAX_COIN_STACK)
                throw new InvalidOperationException($"VERIFY FAIL cell stack {cell.Stack} > {Settings.Default.MAX_COIN_STACK}");
            if (cell.IsFeat && cell.Sym == Settings.Default.F_WHEEL)
            {
                var publicValue = (cell.Fp?.WheelStack ?? 1) - 1;
                if (publicValue < Settings.Default.MIN_WHEEL_STACK_VALUE || publicValue > Settings.Default.MAX_WHEEL_STACK_VALUE)
                    throw new InvalidOperationException($"VERIFY FAIL WheelStackValue={publicValue}");
            }
        }

        foreach (var (sym, count) in got)
        {
            if (count == 0 || plan.Targets.ContainsKey(sym) || Settings.Default.IsFeat(sym)) continue;
            var cap = Settings.Default.SymbolFillCap(sym);
            if (count >= cap)
                throw new InvalidOperationException(
                    $"VERIFY FAIL filler sym={sym} count={count} >= cap={cap}");
        }
    }

    private static int TopPrizeSymbol(GamePlan plan)
    {
        if (plan.PrizeValues.Count == 0) return 0;
        return plan.PrizeValues
            .Select(kv => (Sym: kv.Key, Value: kv.Value.Values.DefaultIfEmpty(0m).Max()))
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Sym)
            .First().Sym;
    }
}
