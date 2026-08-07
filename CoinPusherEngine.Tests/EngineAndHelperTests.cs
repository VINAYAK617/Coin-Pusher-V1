using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace CoinPusherEngine.Tests;

[TestClass]
public sealed class EngineAndHelperTests
{
    [TestMethod]
    public void EngineReplayMatchesVerifiedPlanTotals()
    {
        var input = new MathInput
        {
            Targets = new Dictionary<int, int> { [2] = 18, [4] = 18 },
            BaseSpins = 5,
            Required = new Dictionary<string, int>
            {
                ["WHEEL"] = 1,
                ["FLUSH"] = 1,
                ["EXTRA_SPIN"] = 1,
            },
            MaxSym = 6,
        };
        var plan = new Planner(input, seed: 909).Plan();

        var result = new Engine(plan).Run();
        var simTotals = Sim.Run(plan);

        Assert.IsTrue(result.Win);
        Assert.AreEqual(simTotals.Count, result.Collected.Count);
        foreach (var total in simTotals)
            Assert.AreEqual(total.Value, result.Collected[total.Key]);
        foreach (var target in input.Targets)
        {
            Assert.AreEqual(target.Value, result.Collected[target.Key]);
            Assert.IsTrue(result.SymbolsHit[target.Key]);
        }
    }

    [TestMethod]
    public void EngineRejectsUnverifiedPlans()
    {
        var plan = new GamePlan { Verified = false };

        Assert.ThrowsException<ArgumentException>(() => new Engine(plan));
    }

    [TestMethod]
    public void PrizeCombinatorDecidesFreshAndUpgradeCombos()
    {
        var prizes = new[]
        {
            new Prize { Amount = 1, Target = 12 },
            new Prize { Amount = 5, Target = 15 },
            new Prize { Amount = 10, Target = 18 },
        };
        var combinator = new PrizeCombinator(new PrizeCombinatorOptions
        {
            MinSym = 1,
            MaxSym = 3,
            UpgradeProbability = 1,
            Seed = 5,
        });

        var combos = combinator.Decide(prizes);

        Assert.AreEqual(3, combos.Count);
        Assert.IsFalse(combos[0].IsUpgrade);
        Assert.IsTrue(combos.Skip(1).All(c => c.IsUpgrade));
        Assert.IsTrue(combos.All(c => c.Input.BaseSpins == 5));
        Assert.IsTrue(combos.All(c => c.Input.MaxSym == 3));
        Assert.AreEqual("PRIZE_UPGRADE", combos[1].Input.Required.Single().Key);
        Assert.IsTrue(combos[1].Input.PrizeTiers!.Values.Single() > 0);
    }

    [TestMethod]
    public void VerifierRejectsIllegalNormalPushValues()
    {
        var plan = new Planner(new MathInput
        {
            Targets = new Dictionary<int, int> { [2] = 12 },
            BaseSpins = 5,
            MaxSym = 6,
        }, seed: 1701).Plan();

        var firstNormalCol = Enumerable.Range(0, Settings.Default.COLS)
            .First(col => !plan.Spins[0].Flush[col]);
        plan.Spins[0].Push[firstNormalCol] = Settings.Default.MAX_PUSH + 1;

        var ex = Assert.ThrowsException<InvalidOperationException>(() => Verifier.Check(plan));
        StringAssert.Contains(ex.Message, "push=");
    }

    [TestMethod]
    public void PrizeCombinatorReturnsEmptyForEmptyPrizeList()
    {
        var combos = new PrizeCombinator(new PrizeCombinatorOptions { Seed = 1 })
            .Decide(Array.Empty<Prize>());

        Assert.AreEqual(0, combos.Count);
    }

    [TestMethod]
    public void CapacityAnalyzerCalculatesCapacityAndFeasibility()
    {
        var normalCapacity = Settings.Default.BASE_SPINS * Settings.Default.MixedPushCapacity(Settings.Default.COLS);
        var flushCapacity = normalCapacity
            + 2 * (Settings.Default.ROWS + Settings.Default.MixedPushCapacity(Settings.Default.COLS - 1) - Settings.Default.MixedPushCapacity(Settings.Default.COLS));

        Assert.AreEqual(normalCapacity, CapacityAnalyzer.TotalCapacity(5, 0, 0));
        Assert.AreEqual(flushCapacity, CapacityAnalyzer.TotalCapacity(5, 2, 0));
        Assert.AreEqual(flushCapacity, CapacityAnalyzer.TotalCapacity(5, 2, 1));
        Assert.AreEqual(flushCapacity - 20, CapacityAnalyzer.FillerBudget(20, 5, 0, 2, 1));
        Assert.IsTrue(CapacityAnalyzer.IsFeasible(20, 5, 4, tokenLoad: 0, flushTokens: 2, wheelFireSpins: 1));
        Assert.IsTrue(CapacityAnalyzer.IsFeasible(20, 5, new[] { 1, 5, 6 }, tokenLoad: 0, flushTokens: 2, wheelFireSpins: 1));
        Assert.AreEqual(0, CapacityAnalyzer.MinExtraSpins(20, 4));

        var physical = CapacityAnalyzer.PhysicalWins(
            new Dictionary<int, int> { [1] = 20, [2] = 10 },
            wheelCount: 1);
        Assert.IsTrue(physical < 30);
    }

    [TestMethod]
    public void WheelMathBuildsLocksAndChecksEdf()
    {
        var valid = WMath.ValidStackValues(20).ToArray();
        CollectionAssert.Contains(valid, 1);
        CollectionAssert.Contains(valid, 2);
        CollectionAssert.Contains(valid, 3);

        Assert.AreEqual(4, WMath.StackFromValue(3));
        Assert.AreEqual(4, WMath.Zone(20, 4));
        Assert.AreEqual(3, WMath.CollectibleZone(20, 4));
        Assert.IsFalse(WMath.EdfOk(8, 1, Array.Empty<PlacedFeat>(),
            new Dictionary<int, int> { [2] = 20 }, isMulti: false));

        var single = WMath.MakeLock(2, 20, fireSpin: 3, n: 3);
        Assert.AreEqual(2, single.Sym);
        Assert.AreEqual(12, single.Post);
        Assert.AreEqual(8, single.Pre);

        var multi = WMath.MakeMultiLock(2, total: 20, spin1: 3, n1: 2, spin2: 5, n2: 1, t1: 12);
        Assert.AreEqual(2, multi.lk1.Sym);
        Assert.AreEqual(2, multi.lk2.Sym);
        Assert.IsTrue(WMath.EdfOk(4, 3, Array.Empty<PlacedFeat>(),
            new Dictionary<int, int> { [2] = 20 }, isMulti: false));
    }

    [TestMethod]
    public void GridCloneRotateAndZonesBehavePredictably()
    {
        var board = new Cell?[Settings.Default.ROWS, Settings.Default.COLS];
        board[0, 0] = Grid.Norm(1);
        board[0, 4] = Grid.Norm(2);
        board[4, 0] = Grid.Feat(Settings.Default.F_WHEEL, 1, new FP { FeatId = "WHEEL", WheelSym = 1, WheelStack = 2 });

        var clone = Grid.Clone(board);
        clone[0, 0]!.Sym = 9;

        Assert.AreEqual(1, board[0, 0]!.Sym);
        Assert.AreEqual(9, clone[0, 0]!.Sym);

        var cw = Grid.RotCW(board);
        var ccw = Grid.RotCCW(cw);

        Assert.AreEqual(1, ccw[0, 0]!.Sym);
        Assert.AreEqual(2, ccw[0, 4]!.Sym);
        Assert.IsTrue(ccw[4, 0]!.IsFeat);

        CollectionAssert.AreEqual(new[] { 2, 3, 4 }, Grid.ZoneRows(3));
        var zone = Grid.ZoneSet(new[] { 1, 2, 3, 1, 2 }, new[] { false, true, false, false, false });
        Assert.IsTrue(zone.Contains((4, 0)));
        Assert.IsTrue(zone.Contains((0, 1)));
        Assert.IsTrue(zone.Contains((2, 2)));
    }

    [TestMethod]
    public void SimFlattensStaleFeaturesAndPrinterEmitsTrace()
    {
        var input = new MathInput
        {
            Targets = new Dictionary<int, int> { [2] = 12 },
            BaseSpins = 5,
            MaxSym = 6,
        };
        var plan = new Planner(input, seed: 707).Plan();

        var board = Grid.Clone(plan.Spins[0].Board);
        board[0, 0] = Grid.Feat(Settings.Default.F_PRUP, 3, new FP { FeatId = "PRIZE_UPGRADE", PrupSym = 2, PrupTier = 1 });

        Sim.FlatStale(board);

        Assert.AreEqual(3, board[0, 0]!.Sym);
        Assert.IsFalse(board[0, 0]!.IsFeat);

        using var writer = new StringWriter();
        var original = Console.Out;
        try
        {
            Console.SetOut(writer);
            BoardPrinter.TraceGame(plan);
        }
        finally
        {
            Console.SetOut(original);
        }

        var trace = writer.ToString();
        StringAssert.Contains(trace, "GAME TRACE");
        StringAssert.Contains(trace, "FINAL TOTALS");
    }

    [TestMethod]
    public void WheelResidueSurvivesOutsideImmediateCollectionZone()
    {
        var board = new Cell?[Settings.Default.ROWS, Settings.Default.COLS];
        board[0, 0] = Grid.Feat(Settings.Default.F_WHEEL, 1, new FP
        {
            FeatId = "WHEEL",
            WheelSym = 2,
            WheelStack = 3,
        });
        board[0, 1] = Grid.Norm(2);

        var next = new SpinPlan
        {
            Push = new[] { 1, 1, 1, 1, 1 },
            Flush = new[] { false, false, false, false, false },
        };

        Sim.FireAll(board, new SpinPlan(), next, fallback: 1);

        Assert.AreEqual(2, board[0, 1]!.Sym);
        Assert.AreEqual(3, board[0, 1]!.Stack);
    }

    [TestMethod]
    public void WheelPlanningSupportsImmediateDelayedAndPermanentResidueBuckets()
    {
        var input = new MathInput
        {
            Targets = new Dictionary<int, int> { [2] = 24 },
            BaseSpins = 5,
            Required = new Dictionary<string, int> { ["WHEEL"] = 1 },
            WheelSymOrder = new[] { 2 },
            MaxSym = 6,
        };

        var foundResidue = false;
        for (var seed = 4242; seed < 4300; seed++)
        {
            var plan = new Planner(input, seed).Plan();
            var audit = AuditWheelResidue(plan, wheelSym: 2);

            Assert.IsTrue(audit.ImmediateStacked > 0);
            Assert.AreEqual(24, Sim.Run(plan)[2]);

            if (audit.DelayedCollected || audit.PermanentResidue > 0)
            {
                foundResidue = true;
                break;
            }
        }

        Assert.IsTrue(foundResidue);
    }

    [TestMethod]
    public void RepeatedWheelSymbolsCanStackAndStillVerifyExactly()
    {
        var input = new MathInput
        {
            Targets = new Dictionary<int, int> { [2] = 30 },
            BaseSpins = 5,
            Required = new Dictionary<string, int> { ["WHEEL"] = 2 },
            WheelSymOrder = new[] { 2, 2 },
            MaxSym = 6,
        };

        var plan = new Planner(input, seed: 9191).Plan();
        var wheelTokens = plan.Spins
            .SelectMany(spin => spin.Spawns.Values)
            .Where(cell => cell.IsFeat && cell.Sym == Settings.Default.F_WHEEL && cell.Fp?.WheelSym == 2)
            .ToArray();
        var maxStackSeen = MaxStackSeenDuringReplay(plan, 2);

        Assert.AreEqual(2, wheelTokens.Length);
        Assert.IsTrue(maxStackSeen > 1);
        Assert.IsTrue(maxStackSeen <= Settings.Default.MAX_COIN_STACK);
        Assert.AreEqual(30, Sim.Run(plan)[2]);

        var ticket = JsonConvert.DeserializeObject<TicketSerializer.TicketDto>(TicketSerializer.ToJson(plan))!;
        var report = TicketChecker.CheckTicket(ticket);
        Assert.IsTrue(report.IsValid, string.Join(Environment.NewLine,
            report.Checks
                .Where(c => c.Result == TicketChecker.Status.Fail)
                .Select(c => $"{c.Category}/{c.Name}: {c.Detail}")));
    }

    private static (int ImmediateStacked, bool DelayedCollected, int PermanentResidue) AuditWheelResidue(
        GamePlan plan,
        int wheelSym)
    {
        var board = Grid.Clone(plan.Spins[0].Board);
        var totals = new Dictionary<int, int>();
        var wheelIndex = plan.Spins.FindIndex(spin =>
            spin.Spawns.Values.Any(cell => cell.IsFeat && cell.Sym == Settings.Default.F_WHEEL && cell.Fp?.WheelSym == wheelSym));
        var immediateStacked = 0;
        var delayedCollected = false;

        for (var i = 0; i < plan.Spins.Count; i++)
        {
            var sp = plan.Spins[i];
            Sim.FlatStale(board);
            CollectForAudit(board, sp, totals, wheelSym, i > wheelIndex + 1, ref delayedCollected);
            board = Grid.RotCW(board);
            foreach (var kv in sp.Spawns)
                board[kv.Key.Item1, kv.Key.Item2] = kv.Value.Clone();
            var next = i + 1 < plan.Spins.Count ? plan.Spins[i + 1] : null;
            Sim.FireAll(board, sp, next, plan.FillSyms.Count > 0 ? plan.FillSyms[0] : Settings.Default.F_COIN);

            if (i == wheelIndex && next != null)
            {
                var zone = Grid.ZoneSet(next.Push, next.Flush);
                immediateStacked = CountStackedInZone(board, wheelSym, zone);
            }
        }

        var permanentResidue = board.Cast<Cell?>()
            .Count(cell => cell != null && !cell.IsFeat && cell.Sym == wheelSym && cell.Stack > 1);

        return (immediateStacked, delayedCollected, permanentResidue);
    }

    private static int CountStackedInZone(Cell?[,] board, int sym, HashSet<(int, int)> zone)
    {
        var count = 0;
        for (var r = 0; r < Settings.Default.ROWS; r++)
        {
            for (var c = 0; c < Settings.Default.COLS; c++)
            {
                var cell = board[r, c];
                if (cell != null && !cell.IsFeat && cell.Sym == sym && cell.Stack > 1 && zone.Contains((r, c)))
                    count++;
            }
        }
        return count;
    }

    private static void CollectForAudit(
        Cell?[,] board,
        SpinPlan sp,
        Dictionary<int, int> totals,
        int wheelSym,
        bool afterImmediateTurn,
        ref bool delayedCollected)
    {
        for (var col = 0; col < Settings.Default.COLS; col++)
        {
            if (sp.Flush[col])
            {
                for (var r = 0; r < Settings.Default.ROWS; r++)
                    CollectCell(board, r, col, totals, wheelSym, afterImmediateTurn, ref delayedCollected);
                continue;
            }

            var push = sp.Push[col];
            for (var r = Settings.Default.ROWS - push; r < Settings.Default.ROWS; r++)
                CollectCell(board, r, col, totals, wheelSym, afterImmediateTurn, ref delayedCollected);

            for (var r = Settings.Default.ROWS - 1; r >= 0; r--)
            {
                var src = r - push;
                board[r, col] = src >= 0 ? board[src, col]?.Clone() : null;
            }
        }
    }

    private static void CollectCell(
        Cell?[,] board,
        int row,
        int col,
        Dictionary<int, int> totals,
        int wheelSym,
        bool afterImmediateTurn,
        ref bool delayedCollected)
    {
        var cell = board[row, col];
        if (cell == null || Settings.Default.IsFeat(cell.Sym)) return;
        totals[cell.Sym] = totals.GetValueOrDefault(cell.Sym) + cell.Stack;
        if (afterImmediateTurn && cell.Sym == wheelSym && cell.Stack > 1)
            delayedCollected = true;
    }

    private static int MaxStackSeenDuringReplay(GamePlan plan, int sym)
    {
        var board = Grid.Clone(plan.Spins[0].Board);
        var maxStack = 1;

        for (var i = 0; i < plan.Spins.Count; i++)
        {
            var sp = plan.Spins[i];
            Sim.FlatStale(board);
            for (var col = 0; col < Settings.Default.COLS; col++)
            {
                if (sp.Flush[col])
                {
                    for (var r = 0; r < Settings.Default.ROWS; r++) board[r, col] = null;
                    continue;
                }

                var push = sp.Push[col];
                for (var r = Settings.Default.ROWS - 1; r >= 0; r--)
                {
                    var src = r - push;
                    board[r, col] = src >= 0 ? board[src, col]?.Clone() : null;
                }
            }

            board = Grid.RotCW(board);
            foreach (var kv in sp.Spawns)
                board[kv.Key.Item1, kv.Key.Item2] = kv.Value.Clone();

            var next = i + 1 < plan.Spins.Count ? plan.Spins[i + 1] : null;
            Sim.FireAll(board, sp, next, plan.FillSyms.Count > 0 ? plan.FillSyms[0] : Settings.Default.F_COIN);
            maxStack = Math.Max(maxStack, board.Cast<Cell?>()
                .Where(cell => cell != null && !cell.IsFeat && cell.Sym == sym)
                .Select(cell => cell!.Stack)
                .DefaultIfEmpty(1)
                .Max());
        }

        return maxStack;
    }
}
