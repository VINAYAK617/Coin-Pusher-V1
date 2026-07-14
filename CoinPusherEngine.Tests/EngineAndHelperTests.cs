using Microsoft.VisualStudio.TestTools.UnitTesting;

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
        Assert.AreEqual("PRIZE_UPGRADE", combos[1].Input.Required.Single().Key);
        Assert.IsTrue(combos[1].Input.PrizeTiers!.Values.Single() > 0);
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
        Assert.AreEqual(75, CapacityAnalyzer.TotalCapacity(5, 0, 0));
        Assert.AreEqual(79, CapacityAnalyzer.TotalCapacity(5, 2, 0));
        Assert.AreEqual(69, CapacityAnalyzer.TotalCapacity(5, 2, 1));
        Assert.AreEqual(49, CapacityAnalyzer.FillerBudget(20, 5, 0, 2, 1));
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

        var single = WMath.MakeLock(2, 20, fireSpin: 3, n: 3);
        Assert.AreEqual(2, single.Sym);
        Assert.AreEqual(16, single.Post);
        Assert.AreEqual(4, single.Pre);

        var multi = WMath.MakeMultiLock(2, total: 20, spin1: 3, n1: 2, spin2: 5, n2: 1, t1: 12);
        Assert.AreEqual(2, multi.lk1.Sym);
        Assert.AreEqual(2, multi.lk2.Sym);
        Assert.IsTrue(WMath.EdfOk(4, 3, Array.Empty<PlacedFeat>(),
            new Dictionary<int, int> { [2] = 20 }, isMulti: false));
    }

    [TestMethod]
    public void GridCloneRotateAndZonesBehavePredictably()
    {
        var board = new Cell?[K.ROWS, K.COLS];
        board[0, 0] = Grid.Norm(1);
        board[0, 4] = Grid.Norm(2);
        board[4, 0] = Grid.Feat(K.F_WHEEL, 1, new FP { FeatId = "WHEEL", WheelSym = 1, WheelStack = 2 });

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
        board[0, 0] = Grid.Feat(K.F_PRUP, 3, new FP { FeatId = "PRIZE_UPGRADE", PrupSym = 2, PrupTier = 1 });

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
}
