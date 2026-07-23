namespace CoinPusherEngine;

internal static class Verifier
{
    internal static void Check(GamePlan plan)
    {
        var got = Sim.Run(plan);

        foreach (var spin in plan.Spins)
        {
            for (var col = 0; col < K.COLS; col++)
            {
                var push = spin.Push[col];
                if (spin.Flush[col])
                {
                    if (push != K.ROWS)
                        throw new InvalidOperationException(
                            $"VERIFY FAIL spin={spin.Spin} col={col} FLUSH push={push} want={K.ROWS}");
                }
                else if (push < K.MIN_PUSH || push > K.MAX_PUSH)
                {
                    throw new InvalidOperationException(
                        $"VERIFY FAIL spin={spin.Spin} col={col} push={push} outside {K.MIN_PUSH}..{K.MAX_PUSH}");
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
            var cap = K.SymbolFillCap(sym);
            if (g < target || g >= cap)
                throw new InvalidOperationException(
                    $"VERIFY FAIL nonwin sym={sym} got={g} want>={target} and <{cap}");
        }

        if (plan.WinSyms.Count > 0
            && !plan.WinSyms.Any(sym => plan.Spins[^1].Alloc.GetValueOrDefault(sym) > 0))
            throw new InvalidOperationException("VERIFY FAIL last spin has no win alloc");

        if (plan.Spins[^1].Spawns.Values.Any(cell => cell.IsFeat && cell.Sym == K.F_XSPIN))
            throw new InvalidOperationException("VERIFY FAIL EXTRA_SPIN token in final spin spawns");

        if (plan.Spins[^1].Spawns.Values.Any(cell => cell.IsFeat && cell.Sym == K.F_WHEEL))
            throw new InvalidOperationException("VERIFY FAIL WHEEL token in final spin spawns");

        var extraSpinTokens = plan.Spins
            .SelectMany(spin => spin.Spawns.Values)
            .Count(cell => cell.IsFeat && cell.Sym == K.F_XSPIN);
        if (plan.TotalSpins != K.BASE_SPINS + extraSpinTokens)
        {
            throw new InvalidOperationException(
                $"VERIFY FAIL TotalSpins={plan.TotalSpins} but BASE_SPINS+EXTRA_SPIN={K.BASE_SPINS + extraSpinTokens}");
        }

        foreach (var (spin, index) in plan.Spins.Select((spin, index) => (spin, index)))
        {
            var extrasThisTurn = spin.Spawns.Values.Count(cell => cell.IsFeat && cell.Sym == K.F_XSPIN);
            if (extrasThisTurn > K.MAX_EXTRA_GO_PER_TURN)
            {
                throw new InvalidOperationException(
                    $"VERIFY FAIL spin {index + 1} has {extrasThisTurn} EXTRA_SPIN tokens");
            }
        }

        var topPrizeSym = TopPrizeSymbol(plan);
        if (topPrizeSym > 0)
        {
            var upgradesTopPrize = plan.Spins
                .SelectMany(spin => spin.Spawns.Values)
                .Any(cell => cell.IsFeat && cell.Sym == K.F_PRUP && cell.Fp?.PrupSym == topPrizeSym);
            if (upgradesTopPrize)
                throw new InvalidOperationException($"VERIFY FAIL top prize sym={topPrizeSym} received PRIZE_UPGRADE");
        }

        foreach (var cell in plan.Spins.SelectMany(spin => spin.Spawns.Values))
        {
            if (cell.Stack > K.MAX_COIN_STACK)
                throw new InvalidOperationException($"VERIFY FAIL cell stack {cell.Stack} > {K.MAX_COIN_STACK}");
            if (cell.IsFeat && cell.Sym == K.F_WHEEL)
            {
                var publicValue = (cell.Fp?.WheelStack ?? 1) - 1;
                if (publicValue < K.MIN_WHEEL_STACK_VALUE || publicValue > K.MAX_WHEEL_STACK_VALUE)
                    throw new InvalidOperationException($"VERIFY FAIL WheelStackValue={publicValue}");
            }
        }

        foreach (var (sym, count) in got)
        {
            if (count == 0 || plan.Targets.ContainsKey(sym) || K.IsFeat(sym)) continue;
            var cap = K.SymbolFillCap(sym);
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
