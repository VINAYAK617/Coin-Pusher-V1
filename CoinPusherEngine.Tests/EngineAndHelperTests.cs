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
            Targets = new Dictionary<int, int>
            {
                [2] = Settings.Default.SymbolFillCap(2),
                [4] = Settings.Default.SymbolFillCap(4),
            },
            BaseSpins = 5,
            Required = new Dictionary<string, int>
            {
                ["WHEEL"] = 1,
                ["FLUSH"] = 1,
                ["EXTRA_SPIN"] = 1,
            },
            PrizeValues = PrizeValues(Settings.Default.PrizeLadderRows.Count, tiers: 3),
            MaxSym = 6,
        };
        var plan = ForwardPlan(input, seed: 909);

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
    public void SymbolFillCapComesFromPrizeLadderRows()
    {
        var settings = new Settings
        {
            FILL_CAP = 99,
            PrizeLadderRows = new[]
            {
                new PrizeLadderRow { Target = 17, Tiers = new decimal[] { 1 } },
                new PrizeLadderRow { Target = 23, Tiers = new decimal[] { 2 } },
                new PrizeLadderRow { Target = 31, Tiers = new decimal[] { 5 } },
            },
        };

        Assert.AreEqual(17, settings.SymbolFillCap(1));
        Assert.AreEqual(23, settings.SymbolFillCap(2));
        Assert.AreEqual(31, settings.SymbolFillCap(3));
        Assert.AreEqual(99, settings.SymbolFillCap(4));
    }

    [TestMethod]
    public void SymbolLedgerAllowsExactWinButRejectsOverCollection()
    {
        var ledger = new SymbolLedger(
            new Dictionary<int, int> { [2] = 5 },
            new Dictionary<int, int>(),
            maxSymbol: 6,
            settings: Settings.Default);

        Assert.AreEqual(SymbolCollectionStatus.Valid, ledger.Collect(2, stack: 3).Status);
        Assert.AreEqual(2, ledger.RemainingWinCount(2));
        Assert.AreEqual(SymbolCollectionStatus.Valid, ledger.Collect(2, stack: 2).Status);
        Assert.AreEqual(0, ledger.RemainingWinCount(2));

        var extra = ledger.CheckCollect(2);
        Assert.AreEqual(SymbolCollectionStatus.WouldExceedWinTarget, extra.Status);
        Assert.AreEqual(5, extra.Current);
        Assert.AreEqual(6, extra.Projected);
        Assert.AreEqual(5, ledger.CollectedCount(2));
    }

    [TestMethod]
    public void SymbolLedgerUsesLadderCapForNonWinningSymbols()
    {
        var settings = new Settings
        {
            PrizeLadderRows = new[]
            {
                new PrizeLadderRow { Target = 4, Tiers = new decimal[] { 1 } },
                new PrizeLadderRow { Target = 7, Tiers = new decimal[] { 2 } },
            },
        };
        var ledger = new SymbolLedger(
            new Dictionary<int, int>(),
            new Dictionary<int, int> { [1] = 3 },
            maxSymbol: 2,
            settings: settings);

        Assert.AreEqual(SymbolCollectionStatus.Valid, ledger.Collect(1, stack: 3).Status);

        var capCross = ledger.CheckCollect(1);
        Assert.AreEqual(SymbolCollectionStatus.WouldExceedNonWinCap, capCross.Status);
        Assert.AreEqual(3, capCross.Current);
        Assert.AreEqual(4, capCross.Projected);
        Assert.AreEqual(4, capCross.Limit);
    }

    [TestMethod]
    public void SymbolLedgerFinalValidationReportsShortWinAndNearMiss()
    {
        var ledger = new SymbolLedger(
            new Dictionary<int, int> { [2] = 5 },
            new Dictionary<int, int> { [1] = 3 },
            maxSymbol: 6,
            settings: Settings.Default);

        ledger.Collect(2, stack: 4);
        ledger.Collect(1, stack: 2);

        var failures = ledger.ValidateFinal();

        Assert.IsTrue(failures.Any(f => f.Symbol == 2 && f.Status == SymbolCollectionStatus.WinTargetNotReached));
        Assert.IsTrue(failures.Any(f => f.Symbol == 1 && f.Status == SymbolCollectionStatus.NearMissMinimumNotReached));
    }

    [TestMethod]
    public void SymbolLedgerRejectsInvalidSymbolsAndStacks()
    {
        var ledger = new SymbolLedger(
            new Dictionary<int, int>(),
            new Dictionary<int, int>(),
            maxSymbol: 6,
            settings: Settings.Default);

        Assert.AreEqual(SymbolCollectionStatus.InvalidStack, ledger.CheckCollect(1, stack: 0).Status);
        Assert.AreEqual(SymbolCollectionStatus.UnknownSymbol, ledger.CheckCollect(7).Status);
        Assert.AreEqual(SymbolCollectionStatus.UnknownSymbol, ledger.CheckCollect(Settings.Default.F_WHEEL).Status);
    }

    [TestMethod]
    public void ForwardSymbolSelectorHonorsIntentPriorityBeforeRandomChoice()
    {
        var settings = Settings.Default;
        var nearLedger = new SymbolLedger(
            new Dictionary<int, int>(),
            new Dictionary<int, int> { [2] = 3 },
            maxSymbol: 6,
            settings);
        var nearSelector = new ForwardSymbolSelector(
            nearLedger,
            new Dictionary<int, int>(),
            new Dictionary<int, int> { [2] = 3 },
            new[] { 1, 2, 3, 4, 5, 6 },
            maxSymbol: 6,
            settings,
            new Random(1));

        for (var i = 0; i < 3; i++)
            Assert.AreEqual(ForwardSymbolSelectionStatus.Valid, nearSelector.ChooseAndCollect(ForwardSymbolIntent.PreferNearMiss).Status);

        Assert.AreEqual(3, nearLedger.CollectedCount(2));

        var fillerLedger = new SymbolLedger(
            new Dictionary<int, int>(),
            new Dictionary<int, int> { [2] = 3 },
            maxSymbol: 6,
            settings);
        var fillerSelector = new ForwardSymbolSelector(
            fillerLedger,
            new Dictionary<int, int>(),
            new Dictionary<int, int> { [2] = 3 },
            new[] { 1, 2, 3, 4, 5, 6 },
            maxSymbol: 6,
            settings,
            new Random(2));

        var filler = fillerSelector.ChooseAndCollect(ForwardSymbolIntent.SafeFiller);

        Assert.AreEqual(ForwardSymbolSelectionStatus.Valid, filler.Status);
        Assert.AreNotEqual(2, filler.Symbol);
        Assert.AreEqual(0, fillerLedger.CollectedCount(2));
    }

    [TestMethod]
    public void ForwardTurnShapeCountsMixedPushAndFlushCells()
    {
        var pushers = new[]
        {
            new ForwardPusher(1),
            new ForwardPusher(2),
            new ForwardPusher(3),
            new ForwardPusher(4),
            new ForwardPusher(Settings.Default.ROWS, Settings.Default.F_FLUSH_ID),
        };

        var (shape, check) = ForwardTurnShape.TryCreate(pushers, Settings.Default);

        Assert.AreEqual(ForwardTurnShapeStatus.Valid, check.Status);
        Assert.IsNotNull(shape);
        Assert.AreEqual(15, shape!.PoppedCellCount);
    }

    [TestMethod]
    public void ForwardTurnShapeReportsInvalidPusherShapes()
    {
        var tooFew = ForwardTurnShape.Validate(
            new[] { new ForwardPusher(1) },
            Settings.Default);
        Assert.AreEqual(ForwardTurnShapeStatus.WrongColumnCount, tooFew.Status);

        var invalidNormal = ForwardTurnShape.Validate(
            new[] { new ForwardPusher(0), new ForwardPusher(1), new ForwardPusher(1), new ForwardPusher(1), new ForwardPusher(1) },
            Settings.Default);
        Assert.AreEqual(ForwardTurnShapeStatus.InvalidNormalPush, invalidNormal.Status);

        var invalidFeature = ForwardTurnShape.Validate(
            new[] { new ForwardPusher(1, Settings.Default.F_FLUSH_ID), new ForwardPusher(1), new ForwardPusher(1), new ForwardPusher(1), new ForwardPusher(1) },
            Settings.Default);
        Assert.AreEqual(ForwardTurnShapeStatus.InvalidFeaturePush, invalidFeature.Status);
    }

    [TestMethod]
    public void ForwardTurnShapeReportsExactCollectionCells()
    {
        var pushers = new[]
        {
            new ForwardPusher(1),
            new ForwardPusher(2),
            new ForwardPusher(3),
            new ForwardPusher(4),
            new ForwardPusher(Settings.Default.ROWS, Settings.Default.F_FLUSH_ID),
        };
        var (shape, _) = ForwardTurnShape.TryCreate(pushers, Settings.Default);

        var cells = shape!.CollectionCells().ToHashSet();

        Assert.AreEqual(shape.PoppedCellCount, cells.Count);
        Assert.IsTrue(cells.Contains((4, 0)));
        Assert.IsFalse(cells.Contains((3, 0)));
        Assert.IsTrue(cells.Contains((3, 1)));
        Assert.IsTrue(cells.Contains((2, 2)));
        Assert.IsTrue(cells.Contains((1, 3)));
        Assert.IsTrue(cells.Contains((0, 4)));
    }

    [TestMethod]
    public void ForwardBoardStateCollectsShiftsRotatesAndRequiresExactSpawns()
    {
        var state = new ForwardBoardState(NumberedBoard(), Settings.Default);
        var (shape, _) = ForwardTurnShape.TryCreate(
            Enumerable.Repeat(new ForwardPusher(1), Settings.Default.COLS).ToArray(),
            Settings.Default);

        var mismatch = state.Advance(shape!, Array.Empty<ForwardSpawn>());

        Assert.AreEqual(ForwardBoardAdvanceStatus.SpawnCountMismatch, mismatch.Status);
        Assert.AreEqual(5, mismatch.Collected.Values.Sum());
        for (var sym = 21; sym <= 25; sym++)
            Assert.AreEqual(1, mismatch.Collected[sym]);
    }

    [TestMethod]
    public void ForwardBoardStateRejectsDuplicateAndOverwriteSpawns()
    {
        var (shape, _) = ForwardTurnShape.TryCreate(
            Enumerable.Repeat(new ForwardPusher(1), Settings.Default.COLS).ToArray(),
            Settings.Default);

        var duplicateState = new ForwardBoardState(NumberedBoard(), Settings.Default);
        var duplicateSpawns = new[]
        {
            new ForwardSpawn(0, 4, Grid.Norm(1)),
            new ForwardSpawn(0, 4, Grid.Norm(2)),
            new ForwardSpawn(1, 4, Grid.Norm(3)),
            new ForwardSpawn(2, 4, Grid.Norm(4)),
            new ForwardSpawn(3, 4, Grid.Norm(5)),
        };
        var duplicate = duplicateState.Advance(shape!, duplicateSpawns);
        Assert.AreEqual(ForwardBoardAdvanceStatus.DuplicateSpawnPosition, duplicate.Status);

        var overwriteState = new ForwardBoardState(NumberedBoard(), Settings.Default);
        var overwriteSpawns = new[]
        {
            new ForwardSpawn(0, 0, Grid.Norm(1)),
            new ForwardSpawn(1, 0, Grid.Norm(2)),
            new ForwardSpawn(2, 0, Grid.Norm(3)),
            new ForwardSpawn(3, 0, Grid.Norm(4)),
            new ForwardSpawn(4, 0, Grid.Norm(5)),
        };
        var overwrite = overwriteState.Advance(shape!, overwriteSpawns);
        Assert.AreEqual(ForwardBoardAdvanceStatus.SpawnOverwritesOccupiedCell, overwrite.Status);
    }

    [TestMethod]
    public void ForwardBoardStateAppliesSpawnsOnlyIntoEmptyCells()
    {
        var state = new ForwardBoardState(NumberedBoard(), Settings.Default);
        var (shape, _) = ForwardTurnShape.TryCreate(
            Enumerable.Repeat(new ForwardPusher(1), Settings.Default.COLS).ToArray(),
            Settings.Default);
        var spawns = Enumerable.Range(0, Settings.Default.COLS)
            .Select(row => new ForwardSpawn(row, 4, Grid.Norm(100 + row)))
            .ToArray();

        var result = state.Advance(shape!, spawns);
        var board = state.Snapshot();

        Assert.AreEqual(ForwardBoardAdvanceStatus.Valid, result.Status);
        for (var row = 0; row < Settings.Default.ROWS; row++)
            Assert.AreEqual(100 + row, board[row, 4]!.Sym);
    }

    [TestMethod]
    public void ForwardBoardStatePreviewDoesNotMutateAndReportsExactEmptyPositions()
    {
        var state = new ForwardBoardState(NumberedBoard(), Settings.Default);
        var before = BoardSignature(state.Snapshot());
        var shape = Shape(1, 1, 1, 1, 1);

        var preview = state.PreviewAfterPushRotate(shape);

        Assert.AreEqual(ForwardBoardAdvanceStatus.Valid, preview.Status);
        Assert.AreEqual(5, preview.EmptyPositions.Count);
        CollectionAssert.AreEqual(
            Enumerable.Range(0, Settings.Default.ROWS).Select(row => (row, Settings.Default.COLS - 1)).ToArray(),
            preview.EmptyPositions.ToArray());
        Assert.AreEqual(before, BoardSignature(state.Snapshot()));
    }

    [TestMethod]
    public void ForwardBoardStateAdvanceDoesNotMutateOnInvalidSpawns()
    {
        var state = new ForwardBoardState(NumberedBoard(), Settings.Default);
        var before = BoardSignature(state.Snapshot());

        var result = state.Advance(Shape(1, 1, 1, 1, 1), Array.Empty<ForwardSpawn>());

        Assert.AreEqual(ForwardBoardAdvanceStatus.SpawnCountMismatch, result.Status);
        Assert.AreEqual(before, BoardSignature(state.Snapshot()));
    }

    [TestMethod]
    public void ForwardBoardStateRejectsTooManyAndOutOfRangeSpawns()
    {
        var (shape, _) = ForwardTurnShape.TryCreate(
            Enumerable.Repeat(new ForwardPusher(1), Settings.Default.COLS).ToArray(),
            Settings.Default);

        var tooManyState = new ForwardBoardState(NumberedBoard(), Settings.Default);
        var tooMany = Enumerable.Range(0, Settings.Default.COLS + 1)
            .Select(i => new ForwardSpawn(Math.Min(i, Settings.Default.ROWS - 1), 4, Grid.Norm(1)))
            .ToArray();
        var tooManyResult = tooManyState.Advance(shape!, tooMany);
        Assert.AreEqual(ForwardBoardAdvanceStatus.SpawnCountMismatch, tooManyResult.Status);

        var outOfRangeState = new ForwardBoardState(NumberedBoard(), Settings.Default);
        var outOfRange = Enumerable.Range(0, Settings.Default.COLS)
            .Select(row => new ForwardSpawn(row, 4, Grid.Norm(1)))
            .ToArray();
        outOfRange[0] = new ForwardSpawn(-1, 4, Grid.Norm(1));
        var outOfRangeResult = outOfRangeState.Advance(shape!, outOfRange);
        Assert.AreEqual(ForwardBoardAdvanceStatus.SpawnOutOfRange, outOfRangeResult.Status);
    }

    [TestMethod]
    public void ForwardBoardStateCollectsFlushColumnsStacksAndIgnoresFeatureSymbols()
    {
        var board = NumberedBoard();
        board[0, 0] = Grid.Norm(2);
        board[0, 0]!.Stack = 3;
        board[1, 0] = Grid.Feat(Settings.Default.F_WHEEL, 1, new FP { FeatId = "WHEEL", WheelSym = 2, WheelStack = 2 });
        board[2, 0] = Grid.Norm(4);
        board[2, 0]!.Stack = 2;

        var state = new ForwardBoardState(board, Settings.Default);
        var (shape, _) = ForwardTurnShape.TryCreate(new[]
        {
            new ForwardPusher(Settings.Default.ROWS, Settings.Default.F_FLUSH_ID),
            new ForwardPusher(1),
            new ForwardPusher(1),
            new ForwardPusher(1),
            new ForwardPusher(1),
        }, Settings.Default);

        var spawns = new List<ForwardSpawn>();
        for (var row = 0; row < Settings.Default.ROWS; row++)
            spawns.Add(new ForwardSpawn(row, 4, Grid.Norm(50 + row)));
        for (var col = 0; col < Settings.Default.COLS - 1; col++)
            spawns.Add(new ForwardSpawn(0, col, Grid.Norm(60 + col)));

        var result = state.Advance(shape!, spawns);

        Assert.AreEqual(ForwardBoardAdvanceStatus.Valid, result.Status);
        Assert.AreEqual(3, result.Collected[2]);
        Assert.AreEqual(2, result.Collected[4]);
        Assert.IsFalse(result.Collected.ContainsKey(Settings.Default.F_WHEEL));
    }

    [TestMethod]
    public void ForwardBoardStateClonesInputAndSnapshots()
    {
        var board = NumberedBoard();
        var state = new ForwardBoardState(board, Settings.Default);

        board[0, 0]!.Sym = 999;
        Assert.AreNotEqual(999, state.Snapshot()[0, 0]!.Sym);

        var snapshot = state.Snapshot();
        snapshot[0, 0]!.Sym = 888;
        Assert.AreNotEqual(888, state.Snapshot()[0, 0]!.Sym);
    }

    [TestMethod]
    public void ForwardFeatureExecutorFiresNonWheelBeforeWheelAndConvertsCells()
    {
        var board = EmptyBoard();
        board[0, 0] = Grid.Feat(Settings.Default.F_PRUP, 2, new FP { FeatId = "PRIZE_UPGRADE", PrupSym = 2, PrupTier = 1 });
        board[0, 1] = Grid.Feat(Settings.Default.F_WHEEL, 3, new FP { FeatId = "WHEEL", WheelSym = 2, WheelStack = 2 });
        board[1, 1] = Grid.Norm(2);

        var result = new ForwardFeatureExecutor(Settings.Default).FireAll(board);

        Assert.AreEqual(2, result.Events.Count);
        Assert.AreEqual(Settings.Default.F_PRUP, result.Events[0].FeatureSymbol);
        Assert.AreEqual(Settings.Default.F_WHEEL, result.Events[1].FeatureSymbol);
        Assert.AreEqual(2, result.Events[0].UpgradeSymbol);
        Assert.AreEqual(1, result.Events[0].UpgradeTier);
        Assert.AreEqual(2, result.Events[1].WheelSymbol);
        Assert.AreEqual(2, result.Events[1].WheelStack);
        Assert.AreEqual(2, board[0, 0]!.Sym);
        Assert.AreEqual(3, board[0, 1]!.Sym);
        Assert.AreEqual(2, board[1, 1]!.Stack);
    }

    [TestMethod]
    public void ForwardFeatureExecutorEmitsCompleteExtraGoEvent()
    {
        var board = EmptyBoard();
        board[0, 0] = Grid.Feat(Settings.Default.F_XSPIN, 4, new FP { FeatId = "EXTRA_SPIN" });

        var result = new ForwardFeatureExecutor(Settings.Default).FireAll(board);

        Assert.AreEqual(1, result.Events.Count);
        Assert.AreEqual(Settings.Default.F_XSPIN, result.Events[0].FeatureSymbol);
        Assert.AreEqual(4, result.Events[0].ConvertToSymbol);
        Assert.AreEqual(1, result.Events[0].ExtraGoAward);
        Assert.IsNull(result.Events[0].WheelSymbol);
        Assert.IsNull(result.Events[0].UpgradeSymbol);
        Assert.AreEqual(4, board[0, 0]!.Sym);
    }

    [TestMethod]
    public void ForwardFeatureExecutorWheelStacksOnlyMatchingNormalSymbolsAndClamps()
    {
        var board = EmptyBoard();
        board[0, 0] = Grid.Feat(Settings.Default.F_WHEEL, 1, new FP { FeatId = "WHEEL", WheelSym = 2, WheelStack = 4 });
        board[1, 0] = Grid.Norm(2);
        board[1, 0]!.Stack = Settings.Default.MAX_COIN_STACK - 1;
        board[1, 1] = Grid.Norm(3);
        board[1, 2] = Grid.Feat(Settings.Default.F_PRUP, 3, new FP { FeatId = "PRIZE_UPGRADE", PrupSym = 3, PrupTier = 1 });

        new ForwardFeatureExecutor(Settings.Default).FireAll(board);

        Assert.AreEqual(Settings.Default.MAX_COIN_STACK, board[1, 0]!.Stack);
        Assert.AreEqual(1, board[1, 1]!.Stack);
        Assert.AreEqual(3, board[1, 2]!.Sym);
        Assert.AreEqual(1, board[1, 2]!.Stack);
    }

    [TestMethod]
    public void ForwardFeatureExecutorConvertsBadTargetsToCoinSymbol()
    {
        var board = EmptyBoard();
        board[0, 0] = Grid.Feat(Settings.Default.F_XSPIN, Settings.Default.F_WHEEL, new FP { FeatId = "EXTRA_SPIN" });
        board[0, 1] = new Cell { Sym = 99, IsFeat = true, FeatId = "UNKNOWN", CvtSym = 0 };

        var result = new ForwardFeatureExecutor(Settings.Default).FireAll(board);

        Assert.AreEqual(2, result.Events.Count);
        Assert.AreEqual(Settings.Default.F_COIN, board[0, 0]!.Sym);
        Assert.AreEqual(Settings.Default.F_COIN, board[0, 1]!.Sym);
    }

    [TestMethod]
    public void ForwardBoardStateAppliesFeatureFireTransactionally()
    {
        var settings = Settings.Default;
        var board = FilledBoard(6);
        board[0, 0] = Grid.Feat(settings.F_PRUP, 2, new FP { FeatId = "PRIZE_UPGRADE", PrupSym = 2, PrupTier = 1 });
        board[0, 1] = Grid.Feat(settings.F_WHEEL, 3, new FP { FeatId = "WHEEL", WheelSym = 2, WheelStack = 2 });
        board[1, 1] = Grid.Norm(2);
        var state = new ForwardBoardState(board, settings);

        var result = state.ApplyFeatureFire(new ForwardFeatureExecutor(settings));
        var after = state.Snapshot();

        Assert.AreEqual(ForwardBoardFeatureFireStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(2, result.Events.Count);
        Assert.AreEqual(settings.F_PRUP, result.Events[0].FeatureSymbol);
        Assert.AreEqual(settings.F_WHEEL, result.Events[1].FeatureSymbol);
        Assert.AreEqual(2, after[0, 0]!.Sym);
        Assert.AreEqual(3, after[0, 1]!.Sym);
        Assert.AreEqual(2, after[1, 1]!.Stack);
        Assert.IsFalse(after.Cast<Cell?>().Any(cell => cell?.IsFeat == true));
    }

    [TestMethod]
    public void ForwardBoardStateRejectsMissingFeatureExecutorWithoutMutation()
    {
        var settings = Settings.Default;
        var board = FilledBoard(6);
        board[0, 0] = Grid.Feat(settings.F_XSPIN, 2, new FP { FeatId = "EXTRA_SPIN" });
        var state = new ForwardBoardState(board, settings);
        var before = BoardSignature(state.Snapshot());

        var result = state.ApplyFeatureFire(null);

        Assert.AreEqual(ForwardBoardFeatureFireStatus.MissingExecutor, result.Status);
        Assert.AreEqual(before, BoardSignature(state.Snapshot()));
    }

    [TestMethod]
    public void ForwardFeatureFireIntegratorFiresCurrentBoardAndReportsCounts()
    {
        var settings = Settings.Default;
        var board = FilledBoard(6);
        board[0, 0] = Grid.Feat(settings.F_XSPIN, 2, new FP { FeatId = "EXTRA_SPIN" });
        board[0, 1] = Grid.Feat(settings.F_WHEEL, 3, new FP { FeatId = "WHEEL", WheelSym = 2, WheelStack = 2 });
        board[1, 1] = Grid.Norm(2);
        var state = new ForwardBoardState(board, settings);

        var result = new ForwardFeatureFireIntegrator(settings).FireCurrentBoard(state);
        var after = state.Snapshot();

        Assert.AreEqual(ForwardFeatureFireIntegrationStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(2, result.Events.Count);
        Assert.AreEqual(1, result.ExtraGoAwardCount);
        Assert.AreEqual(1, result.WheelFireCount);
        Assert.AreEqual(0, result.PrizeUpgradeFireCount);
        Assert.AreEqual(2, after[0, 0]!.Sym);
        Assert.AreEqual(3, after[0, 1]!.Sym);
        Assert.AreEqual(2, after[1, 1]!.Stack);
    }

    [TestMethod]
    public void ForwardFeatureFireIntegratorAcceptsBoardWithNoFeatures()
    {
        var settings = Settings.Default;
        var state = new ForwardBoardState(FilledBoard(6), settings);
        var before = BoardSignature(state.Snapshot());

        var result = new ForwardFeatureFireIntegrator(settings).FireCurrentBoard(state);

        Assert.AreEqual(ForwardFeatureFireIntegrationStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(0, result.Events.Count);
        Assert.AreEqual(before, BoardSignature(state.Snapshot()));
    }

    [TestMethod]
    public void ForwardExtraSpinLedgerAcceptsBaseGameWithoutExtras()
    {
        var ledger = new ForwardExtraSpinLedger(Settings.Default.BASE_SPINS, Settings.Default);

        for (var turn = 1; turn <= Settings.Default.BASE_SPINS; turn++)
            Assert.AreEqual(ForwardExtraSpinStatus.Valid, ledger.BeginTurn(turn).Status);

        Assert.AreEqual(ForwardExtraSpinStatus.Valid, ledger.ValidateFinal().Status);
        Assert.AreEqual(0, ledger.LogicalExtraGoAwards);
    }

    [TestMethod]
    public void ForwardExtraSpinLedgerAcceptsEarnedBonusTurns()
    {
        var ledger = new ForwardExtraSpinLedger(plannedTotalTurns: 8, Settings.Default);

        Assert.AreEqual(ForwardExtraSpinStatus.Valid, ledger.BeginTurn(1).Status);
        Assert.AreEqual(ForwardExtraSpinStatus.Valid, ledger.AwardExtraGo(1, 2).Status);
        Assert.AreEqual(ForwardExtraSpinStatus.Valid, ledger.BeginTurn(6).Status);
        Assert.AreEqual(ForwardExtraSpinStatus.Valid, ledger.AwardExtraGo(6, 1).Status);
        Assert.AreEqual(ForwardExtraSpinStatus.Valid, ledger.BeginTurn(8).Status);

        Assert.AreEqual(ForwardExtraSpinStatus.Valid, ledger.ValidateFinal().Status);
        Assert.AreEqual(3, ledger.LogicalExtraGoAwards);
    }

    [TestMethod]
    public void ForwardExtraSpinLedgerRejectsMissingAwardsAndUnearnedTurns()
    {
        var missing = new ForwardExtraSpinLedger(plannedTotalTurns: 8, Settings.Default);
        missing.AwardExtraGo(1, 2);

        Assert.AreEqual(ForwardExtraSpinStatus.TurnBeforeEarned, missing.BeginTurn(8).Status);
        Assert.AreEqual(ForwardExtraSpinStatus.EarnedTurnMismatch, missing.ValidateFinal().Status);
    }

    [TestMethod]
    public void ForwardExtraSpinLedgerRejectsFinalTurnAndOverAwardExtras()
    {
        var finalTurn = new ForwardExtraSpinLedger(plannedTotalTurns: 6, Settings.Default);
        finalTurn.AwardExtraGo(1, 1);
        Assert.AreEqual(ForwardExtraSpinStatus.ExtraGoOnFinalTurn, finalTurn.AwardExtraGo(6, 1).Status);

        var overAward = new ForwardExtraSpinLedger(plannedTotalTurns: 6, Settings.Default);
        Assert.AreEqual(ForwardExtraSpinStatus.ExtraGoOverAwardsPlannedTurns, overAward.AwardExtraGo(1, 2).Status);
    }

    [TestMethod]
    public void ForwardExtraSpinLedgerRejectsInvalidPlannedTotalsAndCounts()
    {
        var below = new ForwardExtraSpinLedger(Settings.Default.BASE_SPINS - 1, Settings.Default);
        Assert.AreEqual(ForwardExtraSpinStatus.PlannedTotalBelowBase, below.ValidatePlanBounds().Status);

        var above = new ForwardExtraSpinLedger(Settings.Default.MAX_SPINS + 1, Settings.Default);
        Assert.AreEqual(ForwardExtraSpinStatus.PlannedTotalAboveMax, above.ValidatePlanBounds().Status);

        var invalidCount = new ForwardExtraSpinLedger(Settings.Default.BASE_SPINS, Settings.Default);
        Assert.AreEqual(ForwardExtraSpinStatus.InvalidExtraGoCount, invalidCount.AwardExtraGo(1, -1).Status);
    }

    [TestMethod]
    public void ForwardPrizeUpgradeLedgerAcceptsSequentialUpgradeToTarget()
    {
        var ledger = new ForwardPrizeUpgradeLedger(
            new Dictionary<int, int> { [2] = 2 },
            PrizeValues(3, tiers: 3),
            maxSymbol: 3,
            settings: Settings.Default);

        Assert.AreEqual(ForwardPrizeUpgradeStatus.Valid, ledger.ApplyUpgrade(2, 1).Status);
        Assert.AreEqual(1, ledger.CurrentTier(2));
        Assert.AreEqual(ForwardPrizeUpgradeStatus.Valid, ledger.ApplyUpgrade(2, 2).Status);
        Assert.AreEqual(2, ledger.CurrentTier(2));
        Assert.AreEqual(0, ledger.ValidateFinal().Count);
    }

    [TestMethod]
    public void ForwardPrizeUpgradeLedgerRejectsInvalidUpgradeRequests()
    {
        var ledger = new ForwardPrizeUpgradeLedger(
            new Dictionary<int, int> { [2] = 1 },
            PrizeValues(3, tiers: 2),
            maxSymbol: 3,
            settings: Settings.Default);

        Assert.AreEqual(ForwardPrizeUpgradeStatus.UnknownSymbol, ledger.ApplyUpgrade(4, 1).Status);
        Assert.AreEqual(ForwardPrizeUpgradeStatus.UpgradeNotPlanned, ledger.ApplyUpgrade(1, 1).Status);
        Assert.AreEqual(ForwardPrizeUpgradeStatus.TierJump, ledger.ApplyUpgrade(2, 2).Status);
        Assert.AreEqual(ForwardPrizeUpgradeStatus.Valid, ledger.ApplyUpgrade(2, 1).Status);
        Assert.AreEqual(ForwardPrizeUpgradeStatus.ExceedsTargetTier, ledger.ApplyUpgrade(2, 2).Status);
    }

    [TestMethod]
    public void ForwardPrizeUpgradeLedgerRejectsMissingPrizeValuesAndFinalMismatch()
    {
        var missingValue = new ForwardPrizeUpgradeLedger(
            new Dictionary<int, int> { [2] = 2 },
            PrizeValues(3, tiers: 1),
            maxSymbol: 3,
            settings: Settings.Default);

        Assert.AreEqual(ForwardPrizeUpgradeStatus.PrizeValueMissing, missingValue.ApplyUpgrade(2, 1).Status);

        var finalMismatch = new ForwardPrizeUpgradeLedger(
            new Dictionary<int, int> { [2] = 2 },
            PrizeValues(3, tiers: 3),
            maxSymbol: 3,
            settings: Settings.Default);

        finalMismatch.ApplyUpgrade(2, 1);
        var failure = finalMismatch.ValidateFinal().Single();
        Assert.AreEqual(ForwardPrizeUpgradeStatus.FinalTierMismatch, failure.Status);
        Assert.AreEqual(1, failure.CurrentTier);
        Assert.AreEqual(2, failure.TargetTier);
    }

    [TestMethod]
    public void ForwardSymbolSelectorProgressesWinsOnlyWhenLedgerAllows()
    {
        var ledger = new SymbolLedger(
            new Dictionary<int, int> { [2] = 2 },
            new Dictionary<int, int>(),
            maxSymbol: 3,
            settings: Settings.Default);
        var selector = new ForwardSymbolSelector(
            ledger,
            new Dictionary<int, int> { [2] = 2 },
            new Dictionary<int, int>(),
            new[] { 1, 3 },
            maxSymbol: 3,
            settings: Settings.Default,
            rng: new Random(1));

        var selected = selector.ChooseAndCollect(ForwardSymbolIntent.MustProgressWin, stack: 2);
        Assert.AreEqual(ForwardSymbolSelectionStatus.Valid, selected.Status);
        Assert.AreEqual(2, selected.Symbol);
        Assert.AreEqual(2, ledger.CollectedCount(2));

        var extra = selector.ChooseAndCollect(ForwardSymbolIntent.MustProgressWin);
        Assert.AreEqual(ForwardSymbolSelectionStatus.NoLegalSymbol, extra.Status);
        Assert.AreEqual(2, ledger.CollectedCount(2));
    }

    [TestMethod]
    public void ForwardSymbolSelectorPrefersNearMissThenFallsBackToFiller()
    {
        var ledger = new SymbolLedger(
            new Dictionary<int, int>(),
            new Dictionary<int, int> { [1] = 3 },
            maxSymbol: 3,
            settings: Settings.Default);
        var selector = new ForwardSymbolSelector(
            ledger,
            new Dictionary<int, int>(),
            new Dictionary<int, int> { [1] = 3 },
            new[] { 1, 3 },
            maxSymbol: 3,
            settings: Settings.Default,
            rng: new Random(1));

        Assert.AreEqual(1, selector.ChooseAndCollect(ForwardSymbolIntent.PreferNearMiss, stack: 2).Symbol);
        Assert.AreEqual(1, selector.ChooseAndCollect(ForwardSymbolIntent.PreferNearMiss).Symbol);
        Assert.AreEqual(3, ledger.CollectedCount(1));

        var fallback = selector.ChooseAndCollect(ForwardSymbolIntent.PreferNearMiss);
        Assert.AreEqual(ForwardSymbolSelectionStatus.Valid, fallback.Status);
        Assert.AreEqual(3, fallback.Symbol);
        Assert.AreEqual(1, ledger.CollectedCount(3));
    }

    [TestMethod]
    public void ForwardSymbolSelectorReturnsFailureWhenNoCandidateIsSafe()
    {
        var settings = new Settings
        {
            PrizeLadderRows = new[]
            {
                new PrizeLadderRow { Target = 3, Tiers = new decimal[] { 1 } },
            },
        };
        var ledger = new SymbolLedger(
            new Dictionary<int, int>(),
            new Dictionary<int, int>(),
            maxSymbol: 1,
            settings: settings);
        ledger.Collect(1, stack: 2);
        var selector = new ForwardSymbolSelector(
            ledger,
            new Dictionary<int, int>(),
            new Dictionary<int, int>(),
            new[] { 1 },
            maxSymbol: 1,
            settings: settings,
            rng: new Random(1));

        var result = selector.ChooseAndCollect(ForwardSymbolIntent.SafeFiller);

        Assert.AreEqual(ForwardSymbolSelectionStatus.NoLegalSymbol, result.Status);
        Assert.AreEqual(2, ledger.CollectedCount(1));
    }

    [TestMethod]
    public void ForwardSymbolSelectorResidueDoesNotMutateLedger()
    {
        var ledger = new SymbolLedger(
            new Dictionary<int, int> { [2] = 1 },
            new Dictionary<int, int> { [1] = 1 },
            maxSymbol: 3,
            settings: Settings.Default);
        var selector = new ForwardSymbolSelector(
            ledger,
            new Dictionary<int, int> { [2] = 1 },
            new Dictionary<int, int> { [1] = 1 },
            new[] { 1, 3 },
            maxSymbol: 3,
            settings: Settings.Default,
            rng: new Random(1));

        var residue = selector.ChooseAndCollect(ForwardSymbolIntent.ResidueOnly, stack: 7);

        Assert.AreEqual(ForwardSymbolSelectionStatus.Valid, residue.Status);
        Assert.AreEqual(0, ledger.CollectedCount(residue.Symbol));
    }

    [TestMethod]
    public void ForwardSymbolSelectorCollectsSpecificSymbolOnlyWhenSafe()
    {
        var ledger = new SymbolLedger(
            new Dictionary<int, int> { [2] = 1 },
            new Dictionary<int, int>(),
            maxSymbol: 3,
            settings: Settings.Default);
        var selector = new ForwardSymbolSelector(
            ledger,
            new Dictionary<int, int> { [2] = 1 },
            new Dictionary<int, int>(),
            new[] { 1, 3 },
            maxSymbol: 3,
            Settings.Default,
            new Random(7));

        var first = selector.TryCollectSpecific(2);
        var second = selector.TryCollectSpecific(2);
        var invalid = selector.TryCollectSpecific(Settings.Default.F_WHEEL);

        Assert.AreEqual(ForwardSymbolSelectionStatus.Valid, first.Status);
        Assert.AreEqual(2, first.Symbol);
        Assert.AreEqual(1, ledger.CollectedCount(2));
        Assert.AreEqual(ForwardSymbolSelectionStatus.NoLegalSymbol, second.Status);
        Assert.AreEqual(1, ledger.CollectedCount(2));
        Assert.AreEqual(ForwardSymbolSelectionStatus.NoLegalSymbol, invalid.Status);
    }

    [TestMethod]
    public void SymbolLedgerCloneKeepsTrialCollectionsSeparate()
    {
        var ledger = new SymbolLedger(
            new Dictionary<int, int> { [2] = 2 },
            new Dictionary<int, int>(),
            maxSymbol: 3,
            settings: Settings.Default);
        var clone = ledger.Clone();

        clone.Collect(2);

        Assert.AreEqual(0, ledger.CollectedCount(2));
        Assert.AreEqual(1, clone.CollectedCount(2));
    }

    [TestMethod]
    public void ForwardCellFateAnalyzerDetectsImmediateCollection()
    {
        var analyzer = new ForwardCellFateAnalyzer(Settings.Default);
        var fate = analyzer.Analyze(
            row: 4,
            col: 0,
            new[] { new ForwardFutureTurn(2, Shape(1, 1, 1, 1, 1)) });

        Assert.AreEqual(ForwardCellFateStatus.Valid, fate.Status);
        Assert.AreEqual(ForwardCellFateKind.Collected, fate.Kind);
        Assert.AreEqual(2, fate.CollectedTurn);
        Assert.AreEqual(4, fate.FinalRow);
        Assert.AreEqual(0, fate.FinalCol);
    }

    [TestMethod]
    public void ForwardCellFateAnalyzerDetectsDelayedCollectionAfterRotation()
    {
        var analyzer = new ForwardCellFateAnalyzer(Settings.Default);
        var fate = analyzer.Analyze(
            row: 0,
            col: 0,
            new[]
            {
                new ForwardFutureTurn(1, Shape(1, 1, 1, 1, 1)),
                new ForwardFutureTurn(2, Shape(1, 1, 1, 4, 1)),
                new ForwardFutureTurn(3, Shape(4, 1, 1, 1, 1)),
            });

        Assert.AreEqual(ForwardCellFateStatus.Valid, fate.Status);
        Assert.IsTrue(fate.IsCollected);
        Assert.AreEqual(3, fate.CollectedTurn);
        Assert.AreEqual(3, fate.FinalRow);
        Assert.AreEqual(0, fate.FinalCol);
    }

    [TestMethod]
    public void ForwardCellFateAnalyzerReportsSurvivalAndFinalCoordinate()
    {
        var analyzer = new ForwardCellFateAnalyzer(Settings.Default);
        var fate = analyzer.Analyze(
            row: 0,
            col: 0,
            new[] { new ForwardFutureTurn(1, Shape(1, 1, 1, 1, 1)) });

        Assert.AreEqual(ForwardCellFateStatus.Valid, fate.Status);
        Assert.AreEqual(ForwardCellFateKind.Survives, fate.Kind);
        Assert.IsNull(fate.CollectedTurn);
        Assert.AreEqual(0, fate.FinalRow);
        Assert.AreEqual(3, fate.FinalCol);
    }

    [TestMethod]
    public void ForwardCellFateAnalyzerDetectsFlushCollection()
    {
        var analyzer = new ForwardCellFateAnalyzer(Settings.Default);
        var fate = analyzer.Analyze(
            row: 0,
            col: 2,
            new[] { new ForwardFutureTurn(4, Shape(1, 1, 5, 1, 1)) });

        Assert.AreEqual(ForwardCellFateStatus.Valid, fate.Status);
        Assert.AreEqual(ForwardCellFateKind.Collected, fate.Kind);
        Assert.AreEqual(4, fate.CollectedTurn);
        Assert.AreEqual(0, fate.FinalRow);
        Assert.AreEqual(2, fate.FinalCol);
    }

    [TestMethod]
    public void ForwardCellFateAnalyzerRejectsInvalidInputs()
    {
        var analyzer = new ForwardCellFateAnalyzer(Settings.Default);

        Assert.AreEqual(
            ForwardCellFateStatus.InvalidStartPosition,
            analyzer.Analyze(-1, 0, Array.Empty<ForwardFutureTurn>()).Status);

        Assert.AreEqual(
            ForwardCellFateStatus.InvalidTurnNumber,
            analyzer.Analyze(0, 0, new[]
            {
                new ForwardFutureTurn(2, Shape(1, 1, 1, 1, 1)),
                new ForwardFutureTurn(2, Shape(1, 1, 1, 1, 1)),
            }).Status);

        Assert.AreEqual(
            ForwardCellFateStatus.InvalidTurnShape,
            analyzer.Analyze(0, 0, new[] { default(ForwardFutureTurn) }).Status);
    }

    [TestMethod]
    public void ForwardSpawnPlannerPlansOneSpawnPerCellAndUpdatesLedgerForCollectedCells()
    {
        var ledger = new SymbolLedger(
            new Dictionary<int, int>(),
            new Dictionary<int, int> { [1] = 2 },
            maxSymbol: 3,
            settings: Settings.Default);
        var planner = SpawnPlanner(
            ledger,
            new Dictionary<int, int>(),
            new Dictionary<int, int> { [1] = 2 },
            new[] { 1, 2, 3 },
            maxSymbol: 3,
            seed: 1);

        var result = planner.Plan(
            new[]
            {
                new ForwardSpawnCellRequest(0, 4, ForwardSymbolIntent.PreferNearMiss),
                new ForwardSpawnCellRequest(1, 4, ForwardSymbolIntent.PreferNearMiss),
            },
            new[] { new ForwardFutureTurn(2, Shape(1, 1, 1, 1, 5)) });

        Assert.AreEqual(ForwardSpawnPlanStatus.Valid, result.Status);
        Assert.AreEqual(2, result.Spawns.Count);
        Assert.IsTrue(result.Spawns.All(spawn => spawn.Cell.Sym == 1));
        Assert.AreEqual(2, ledger.CollectedCount(1));
    }

    [TestMethod]
    public void ForwardSpawnPlannerResidueCellsDoNotMutateLedger()
    {
        var ledger = new SymbolLedger(
            new Dictionary<int, int> { [2] = 1 },
            new Dictionary<int, int> { [1] = 1 },
            maxSymbol: 3,
            settings: Settings.Default);
        var planner = SpawnPlanner(
            ledger,
            new Dictionary<int, int> { [2] = 1 },
            new Dictionary<int, int> { [1] = 1 },
            new[] { 1, 3 },
            maxSymbol: 3,
            seed: 1);

        var result = planner.Plan(
            new[]
            {
                new ForwardSpawnCellRequest(0, 0, ForwardSymbolIntent.PreferNearMiss),
                new ForwardSpawnCellRequest(0, 1, ForwardSymbolIntent.MustProgressWin),
            },
            Array.Empty<ForwardFutureTurn>());

        Assert.AreEqual(ForwardSpawnPlanStatus.Valid, result.Status);
        Assert.AreEqual(2, result.Spawns.Count);
        Assert.AreEqual(0, ledger.CollectedCount(1));
        Assert.AreEqual(0, ledger.CollectedCount(2));
    }

    [TestMethod]
    public void ForwardSpawnPlannerTreatsFutureRotatedCollectionAsCollectible()
    {
        var ledger = new SymbolLedger(
            new Dictionary<int, int>(),
            new Dictionary<int, int> { [1] = 1 },
            maxSymbol: 3,
            settings: Settings.Default);
        var planner = SpawnPlanner(
            ledger,
            new Dictionary<int, int>(),
            new Dictionary<int, int> { [1] = 1 },
            new[] { 1, 2, 3 },
            maxSymbol: 3,
            seed: 1);

        var result = planner.Plan(
            new[] { new ForwardSpawnCellRequest(0, 0, ForwardSymbolIntent.PreferNearMiss) },
            new[]
            {
                new ForwardFutureTurn(1, Shape(1, 1, 1, 1, 1)),
                new ForwardFutureTurn(2, Shape(1, 1, 1, 5, 1)),
            });

        Assert.AreEqual(ForwardSpawnPlanStatus.Valid, result.Status);
        Assert.AreEqual(1, ledger.CollectedCount(1));
    }

    [TestMethod]
    public void ForwardSpawnPlannerRejectsInvalidDuplicateAndUnsafeCells()
    {
        var settings = new Settings
        {
            PrizeLadderRows = new[]
            {
                new PrizeLadderRow { Target = 3, Tiers = new decimal[] { 1 } },
            },
        };
        var ledger = new SymbolLedger(
            new Dictionary<int, int>(),
            new Dictionary<int, int>(),
            maxSymbol: 1,
            settings: settings);
        ledger.Collect(1, stack: 2);
        var planner = SpawnPlanner(
            ledger,
            new Dictionary<int, int>(),
            new Dictionary<int, int>(),
            new[] { 1 },
            maxSymbol: 1,
            seed: 1,
            settings: settings);

        Assert.AreEqual(
            ForwardSpawnPlanStatus.InvalidSpawnPosition,
            planner.Plan(new[] { new ForwardSpawnCellRequest(-1, 0) }, Array.Empty<ForwardFutureTurn>()).Status);

        Assert.AreEqual(
            ForwardSpawnPlanStatus.DuplicateSpawnPosition,
            planner.Plan(new[]
            {
                new ForwardSpawnCellRequest(0, 0),
                new ForwardSpawnCellRequest(0, 0),
            }, Array.Empty<ForwardFutureTurn>()).Status);

        Assert.AreEqual(
            ForwardSpawnPlanStatus.InvalidStack,
            planner.Plan(new[] { new ForwardSpawnCellRequest(0, 0, ledgerCollectionValue: 0) }, Array.Empty<ForwardFutureTurn>()).Status);

        var unsafeResult = planner.Plan(
            new[] { new ForwardSpawnCellRequest(0, 4, ForwardSymbolIntent.SafeFiller) },
            new[] { new ForwardFutureTurn(2, Shape(1, 1, 1, 1, 5)) });
        Assert.AreEqual(ForwardSpawnPlanStatus.NoSafeSymbolForCollectingCell, unsafeResult.Status);
        Assert.AreEqual(2, ledger.CollectedCount(1));
    }

    [TestMethod]
    public void ForwardSpawnPlannerUsesLedgerCollectionValueForFutureStackSafety()
    {
        var settings = new Settings
        {
            PrizeLadderRows = new[]
            {
                new PrizeLadderRow { Target = 3, Tiers = new decimal[] { 1 } },
            },
        };
        var ledger = new SymbolLedger(
            new Dictionary<int, int>(),
            new Dictionary<int, int>(),
            maxSymbol: 1,
            settings: settings);
        var planner = SpawnPlanner(
            ledger,
            new Dictionary<int, int>(),
            new Dictionary<int, int>(),
            new[] { 1 },
            maxSymbol: 1,
            seed: 1,
            settings: settings);

        var result = planner.Plan(
            new[] { new ForwardSpawnCellRequest(0, 4, ForwardSymbolIntent.SafeFiller, ledgerCollectionValue: 3, spawnStack: 1) },
            new[] { new ForwardFutureTurn(2, Shape(1, 1, 1, 1, 5)) });

        Assert.AreEqual(ForwardSpawnPlanStatus.NoSafeSymbolForCollectingCell, result.Status);
        Assert.AreEqual(0, ledger.CollectedCount(1));
    }

    [TestMethod]
    public void ForwardStackImpactAnalyzerAppliesOnlyMatchingWheelsBeforeCollection()
    {
        var analyzer = new ForwardStackImpactAnalyzer(
            new[]
            {
                new ForwardWheelImpact(1, 2, 3),
                new ForwardWheelImpact(3, 2, 2),
                new ForwardWheelImpact(4, 3, 3),
                new ForwardWheelImpact(5, 2, 3),
            },
            maxSymbol: 3,
            settings: Settings.Default);

        var impact = analyzer.Analyze(symbol: 2, spawnTurn: 2, collectionTurn: 5, spawnStack: 1);

        Assert.AreEqual(ForwardStackImpactStatus.Valid, impact.Status);
        Assert.AreEqual(2, impact.FinalStack);
        Assert.AreEqual(1, impact.AppliedWheelCount);
    }

    [TestMethod]
    public void ForwardStackImpactAnalyzerClampsMultipleWheelImpacts()
    {
        var analyzer = new ForwardStackImpactAnalyzer(
            new[]
            {
                new ForwardWheelImpact(2, 2, 4),
                new ForwardWheelImpact(3, 2, 4),
                new ForwardWheelImpact(4, 2, 4),
            },
            maxSymbol: 3,
            settings: Settings.Default);

        var impact = analyzer.Analyze(symbol: 2, spawnTurn: 1, collectionTurn: 5, spawnStack: 2);

        Assert.AreEqual(ForwardStackImpactStatus.Valid, impact.Status);
        Assert.AreEqual(Settings.Default.MAX_COIN_STACK, impact.FinalStack);
        Assert.AreEqual(2, impact.AppliedWheelCount);
    }

    [TestMethod]
    public void ForwardStackImpactAnalyzerLeavesResidueAndNonMatchingSymbolsUnchanged()
    {
        var analyzer = new ForwardStackImpactAnalyzer(
            new[] { new ForwardWheelImpact(2, 2, 3) },
            maxSymbol: 3,
            settings: Settings.Default);

        var residue = analyzer.Analyze(symbol: 2, spawnTurn: 1, collectionTurn: null, spawnStack: 1);
        Assert.AreEqual(ForwardStackImpactStatus.Valid, residue.Status);
        Assert.AreEqual(1, residue.FinalStack);
        Assert.AreEqual(0, residue.AppliedWheelCount);

        var nonMatching = analyzer.Analyze(symbol: 3, spawnTurn: 1, collectionTurn: 4, spawnStack: 1);
        Assert.AreEqual(ForwardStackImpactStatus.Valid, nonMatching.Status);
        Assert.AreEqual(1, nonMatching.FinalStack);
        Assert.AreEqual(0, nonMatching.AppliedWheelCount);
    }

    [TestMethod]
    public void ForwardStackImpactAnalyzerRejectsInvalidInputs()
    {
        var analyzer = new ForwardStackImpactAnalyzer(
            Array.Empty<ForwardWheelImpact>(),
            maxSymbol: 3,
            settings: Settings.Default);

        Assert.AreEqual(
            ForwardStackImpactStatus.InvalidSymbol,
            analyzer.Analyze(Settings.Default.F_WHEEL, spawnTurn: 1, collectionTurn: 2).Status);
        Assert.AreEqual(
            ForwardStackImpactStatus.InvalidSpawnStack,
            analyzer.Analyze(2, spawnTurn: 1, collectionTurn: 2, spawnStack: 0).Status);
        Assert.AreEqual(
            ForwardStackImpactStatus.InvalidCollectionTurn,
            analyzer.Analyze(2, spawnTurn: 2, collectionTurn: 2).Status);

        var badWheel = new ForwardStackImpactAnalyzer(
            new[] { new ForwardWheelImpact(0, 2, 2) },
            maxSymbol: 3,
            settings: Settings.Default);
        Assert.AreEqual(
            ForwardStackImpactStatus.InvalidWheel,
            badWheel.Analyze(2, spawnTurn: 1, collectionTurn: 3).Status);
    }

    [TestMethod]
    public void ForwardFeatureSpawnPlannerPlansValidFeatureCellsAndUpdatesLedgers()
    {
        var extraLedger = new ForwardExtraSpinLedger(plannedTotalTurns: 6, Settings.Default);
        var prizeLedger = new ForwardPrizeUpgradeLedger(
            new Dictionary<int, int> { [2] = 1 },
            PrizeValues(3, tiers: 2),
            maxSymbol: 3,
            settings: Settings.Default);
        var planner = new ForwardFeatureSpawnPlanner(Settings.Default, maxSymbol: 3);

        var result = planner.Plan(
            turn: 1,
            plannedTotalTurns: 6,
            emptyPositions: Positions((0, 4), (1, 4), (2, 4)),
            reservedPositions: Array.Empty<(int r, int c)>(),
            featureRequests: new[]
            {
                ForwardFeatureSpawnRequest.ExtraGo(0, 4, convertToSymbol: 1),
                ForwardFeatureSpawnRequest.PrizeUpgrade(1, 4, convertToSymbol: 2, upgradeSymbol: 2, upgradeTier: 1),
                ForwardFeatureSpawnRequest.Wheel(2, 4, convertToSymbol: 3, wheelSymbol: 2, wheelStack: 2),
            },
            remainingFeatureCapacity: FeatureCapacity(
                (ForwardFeatureKind.ExtraGo, 1),
                (ForwardFeatureKind.PrizeUpgrade, 1),
                (ForwardFeatureKind.Wheel, 1)),
            extraLedger,
            prizeLedger);

        Assert.AreEqual(ForwardFeatureSpawnStatus.Valid, result.Status);
        Assert.AreEqual(3, result.Spawns.Count);
        Assert.AreEqual(Settings.Default.F_XSPIN, result.Spawns[0].Cell.Sym);
        Assert.AreEqual(Settings.Default.F_PRUP, result.Spawns[1].Cell.Sym);
        Assert.AreEqual(Settings.Default.F_WHEEL, result.Spawns[2].Cell.Sym);
        Assert.AreEqual(6, extraLedger.EarnedTurns);
        Assert.AreEqual(1, extraLedger.LogicalExtraGoAwards);
        Assert.AreEqual(1, prizeLedger.CurrentTier(2));
        Assert.AreEqual(1, result.WheelImpacts.Count);
        Assert.AreEqual(1, result.WheelImpacts[0].FireTurn);
        Assert.AreEqual(2, result.WheelImpacts[0].Symbol);
        Assert.AreEqual(2, result.WheelImpacts[0].StackValue);
    }

    [TestMethod]
    public void ForwardFeatureSpawnPlannerRejectsTimingAndCapacityProblems()
    {
        var planner = new ForwardFeatureSpawnPlanner(Settings.Default, maxSymbol: 3);

        var finalTurn = planner.Plan(
            turn: 6,
            plannedTotalTurns: 6,
            emptyPositions: Positions((0, 4)),
            reservedPositions: Array.Empty<(int r, int c)>(),
            featureRequests: new[] { ForwardFeatureSpawnRequest.ExtraGo(0, 4, 1) },
            remainingFeatureCapacity: FeatureCapacity((ForwardFeatureKind.ExtraGo, 1)),
            new ForwardExtraSpinLedger(6, Settings.Default),
            EmptyPrizeLedger(3));
        Assert.AreEqual(ForwardFeatureSpawnStatus.FeatureOnFinalTurn, finalTurn.Status);

        var overAward = planner.Plan(
            turn: 1,
            plannedTotalTurns: 6,
            emptyPositions: Positions((0, 4), (1, 4)),
            reservedPositions: Array.Empty<(int r, int c)>(),
            featureRequests: new[]
            {
                ForwardFeatureSpawnRequest.ExtraGo(0, 4, 1),
                ForwardFeatureSpawnRequest.ExtraGo(1, 4, 1),
            },
            remainingFeatureCapacity: FeatureCapacity((ForwardFeatureKind.ExtraGo, 2)),
            new ForwardExtraSpinLedger(6, Settings.Default),
            EmptyPrizeLedger(3));
        Assert.AreEqual(ForwardFeatureSpawnStatus.ExtraGoTimelineInvalid, overAward.Status);

        var capacity = planner.Plan(
            turn: 1,
            plannedTotalTurns: 6,
            emptyPositions: Positions((0, 4), (1, 4)),
            reservedPositions: Array.Empty<(int r, int c)>(),
            featureRequests: new[]
            {
                ForwardFeatureSpawnRequest.Wheel(0, 4, 1, 2, 2),
                ForwardFeatureSpawnRequest.Wheel(1, 4, 1, 2, 2),
            },
            remainingFeatureCapacity: FeatureCapacity((ForwardFeatureKind.Wheel, 1)),
            new ForwardExtraSpinLedger(6, Settings.Default),
            EmptyPrizeLedger(3));
        Assert.AreEqual(ForwardFeatureSpawnStatus.FeatureCapacityExceeded, capacity.Status);
    }

    [TestMethod]
    public void ForwardFeatureSpawnPlannerRejectsPositionProblems()
    {
        var planner = new ForwardFeatureSpawnPlanner(Settings.Default, maxSymbol: 3);

        Assert.AreEqual(
            ForwardFeatureSpawnStatus.InvalidEmptyPosition,
            planner.Plan(1, 6, Positions((-1, 4)), Array.Empty<(int r, int c)>(), Array.Empty<ForwardFeatureSpawnRequest>(),
                FeatureCapacity(), new ForwardExtraSpinLedger(6, Settings.Default), EmptyPrizeLedger(3)).Status);

        Assert.AreEqual(
            ForwardFeatureSpawnStatus.DuplicateEmptyPosition,
            planner.Plan(1, 6, Positions((0, 4), (0, 4)), Array.Empty<(int r, int c)>(), Array.Empty<ForwardFeatureSpawnRequest>(),
                FeatureCapacity(), new ForwardExtraSpinLedger(6, Settings.Default), EmptyPrizeLedger(3)).Status);

        Assert.AreEqual(
            ForwardFeatureSpawnStatus.InvalidReservedPosition,
            planner.Plan(1, 6, Positions((0, 4)), Positions((9, 9)), Array.Empty<ForwardFeatureSpawnRequest>(),
                FeatureCapacity(), new ForwardExtraSpinLedger(6, Settings.Default), EmptyPrizeLedger(3)).Status);

        Assert.AreEqual(
            ForwardFeatureSpawnStatus.FeaturePositionNotEmpty,
            planner.Plan(1, 6, Positions((0, 4)), Array.Empty<(int r, int c)>(),
                new[] { ForwardFeatureSpawnRequest.ExtraGo(1, 4, 1) },
                FeatureCapacity((ForwardFeatureKind.ExtraGo, 1)),
                new ForwardExtraSpinLedger(6, Settings.Default), EmptyPrizeLedger(3)).Status);

        Assert.AreEqual(
            ForwardFeatureSpawnStatus.FeaturePositionAlreadyReserved,
            planner.Plan(1, 6, Positions((0, 4)), Positions((0, 4)),
                new[] { ForwardFeatureSpawnRequest.ExtraGo(0, 4, 1) },
                FeatureCapacity((ForwardFeatureKind.ExtraGo, 1)),
                new ForwardExtraSpinLedger(6, Settings.Default), EmptyPrizeLedger(3)).Status);

        Assert.AreEqual(
            ForwardFeatureSpawnStatus.DuplicateFeaturePosition,
            planner.Plan(1, 6, Positions((0, 4), (1, 4)), Array.Empty<(int r, int c)>(),
                new[]
                {
                    ForwardFeatureSpawnRequest.ExtraGo(0, 4, 1),
                    ForwardFeatureSpawnRequest.Wheel(0, 4, 1, 2, 2),
                },
                FeatureCapacity((ForwardFeatureKind.ExtraGo, 1), (ForwardFeatureKind.Wheel, 1)),
                new ForwardExtraSpinLedger(6, Settings.Default), EmptyPrizeLedger(3)).Status);
    }

    [TestMethod]
    public void ForwardFeatureSpawnPlannerRejectsInvalidPayloadsAndPrizeSequences()
    {
        var planner = new ForwardFeatureSpawnPlanner(Settings.Default, maxSymbol: 3);

        var badWheel = planner.Plan(
            1, 6, Positions((0, 4)), Array.Empty<(int r, int c)>(),
            new[] { ForwardFeatureSpawnRequest.Wheel(0, 4, 1, wheelSymbol: 2, wheelStack: Settings.Default.MAX_WHEEL_STACK_VALUE + 2) },
            FeatureCapacity((ForwardFeatureKind.Wheel, 1)),
            new ForwardExtraSpinLedger(6, Settings.Default), EmptyPrizeLedger(3));
        Assert.AreEqual(ForwardFeatureSpawnStatus.InvalidWheelPayload, badWheel.Status);

        var badPrizePayload = planner.Plan(
            1, 6, Positions((0, 4)), Array.Empty<(int r, int c)>(),
            new[] { ForwardFeatureSpawnRequest.PrizeUpgrade(0, 4, 1, upgradeSymbol: 4, upgradeTier: 1) },
            FeatureCapacity((ForwardFeatureKind.PrizeUpgrade, 1)),
            new ForwardExtraSpinLedger(6, Settings.Default), EmptyPrizeLedger(3));
        Assert.AreEqual(ForwardFeatureSpawnStatus.InvalidPrizeUpgradePayload, badPrizePayload.Status);

        var prizeInvalid = planner.Plan(
            1, 6, Positions((0, 4)), Array.Empty<(int r, int c)>(),
            new[] { ForwardFeatureSpawnRequest.PrizeUpgrade(0, 4, 1, upgradeSymbol: 2, upgradeTier: 1) },
            FeatureCapacity((ForwardFeatureKind.PrizeUpgrade, 1)),
            new ForwardExtraSpinLedger(6, Settings.Default), EmptyPrizeLedger(3));
        Assert.AreEqual(ForwardFeatureSpawnStatus.PrizeUpgradeInvalid, prizeInvalid.Status);

        var duplicateSameSymbol = planner.Plan(
            1, 6, Positions((0, 4), (1, 4)), Array.Empty<(int r, int c)>(),
            new[]
            {
                ForwardFeatureSpawnRequest.PrizeUpgrade(0, 4, 1, upgradeSymbol: 2, upgradeTier: 1),
                ForwardFeatureSpawnRequest.PrizeUpgrade(1, 4, 1, upgradeSymbol: 2, upgradeTier: 2),
            },
            FeatureCapacity((ForwardFeatureKind.PrizeUpgrade, 2)),
            new ForwardExtraSpinLedger(6, Settings.Default),
            new ForwardPrizeUpgradeLedger(new Dictionary<int, int> { [2] = 2 }, PrizeValues(3, 3), 3, Settings.Default));
        Assert.AreEqual(ForwardFeatureSpawnStatus.MultiplePrizeUpgradesSameSymbolSameTurn, duplicateSameSymbol.Status);
    }

    [TestMethod]
    public void ForwardFeatureSpawnPlannerDoesNotMutateLedgersWhenPlanFails()
    {
        var extraLedger = new ForwardExtraSpinLedger(plannedTotalTurns: 6, Settings.Default);
        var prizeLedger = EmptyPrizeLedger(3);
        var planner = new ForwardFeatureSpawnPlanner(Settings.Default, maxSymbol: 3);

        var result = planner.Plan(
            1, 6,
            Positions((0, 4), (1, 4)),
            Array.Empty<(int r, int c)>(),
            new[]
            {
                ForwardFeatureSpawnRequest.ExtraGo(0, 4, 1),
                ForwardFeatureSpawnRequest.PrizeUpgrade(1, 4, 1, upgradeSymbol: 2, upgradeTier: 1),
            },
            FeatureCapacity((ForwardFeatureKind.ExtraGo, 1), (ForwardFeatureKind.PrizeUpgrade, 1)),
            extraLedger,
            prizeLedger);

        Assert.AreEqual(ForwardFeatureSpawnStatus.PrizeUpgradeInvalid, result.Status);
        Assert.AreEqual(Settings.Default.BASE_SPINS, extraLedger.EarnedTurns);
        Assert.AreEqual(0, extraLedger.LogicalExtraGoAwards);
        Assert.AreEqual(0, prizeLedger.CurrentTier(2));
    }

    [TestMethod]
    public void ForwardTurnShapePlannerBuildsMixedUnsortedLegalShapes()
    {
        var planner = new ForwardTurnShapePlanner(Settings.Default, new Random(11));

        var result = planner.Plan(minPoppedCells: 10, maxPoppedCells: 12);

        Assert.AreEqual(ForwardTurnShapePlanStatus.Valid, result.Status);
        Assert.IsNotNull(result.Shape);
        Assert.IsTrue(result.PoppedCellCount >= 10 && result.PoppedCellCount <= 12);
        Assert.IsFalse(result.IsAllSame);
        Assert.IsFalse(result.IsSortedPattern);
        Assert.IsTrue(result.DistinctPushValues >= 3);
        Assert.IsTrue(result.Shape!.Pushers.All(p => p.PushValue >= Settings.Default.MIN_PUSH && p.PushValue <= Settings.Default.MAX_PUSH));
    }

    [TestMethod]
    public void ForwardTurnShapePlannerSupportsFlushAndBlockedColumns()
    {
        var planner = new ForwardTurnShapePlanner(Settings.Default, new Random(12));

        var result = planner.Plan(
            minPoppedCells: 10,
            maxPoppedCells: 14,
            flushColumns: new HashSet<int> { 2 },
            blockedColumns: new HashSet<int> { 4 });

        Assert.AreEqual(ForwardTurnShapePlanStatus.Valid, result.Status);
        Assert.AreEqual(Settings.Default.ROWS, result.Shape!.Pushers[2].PushValue);
        Assert.AreEqual(Settings.Default.F_FLUSH_ID, result.Shape.Pushers[2].FeatureId);
        Assert.AreEqual(Settings.Default.MIN_PUSH, result.Shape.Pushers[4].PushValue);
        Assert.IsNull(result.Shape.Pushers[4].FeatureId);
        Assert.IsTrue(result.PoppedCellCount >= 10 && result.PoppedCellCount <= 14);
    }

    [TestMethod]
    public void ForwardTurnShapePlannerRejectsInvalidOrImpossibleInputs()
    {
        var planner = new ForwardTurnShapePlanner(Settings.Default, new Random(13));

        Assert.AreEqual(
            ForwardTurnShapePlanStatus.InvalidBudget,
            planner.Plan(minPoppedCells: 12, maxPoppedCells: 10).Status);
        Assert.AreEqual(
            ForwardTurnShapePlanStatus.InvalidFlushColumn,
            planner.Plan(5, 10, flushColumns: new HashSet<int> { 9 }).Status);
        Assert.AreEqual(
            ForwardTurnShapePlanStatus.InvalidBlockedColumn,
            planner.Plan(5, 10, blockedColumns: new HashSet<int> { -1 }).Status);
        Assert.AreEqual(
            ForwardTurnShapePlanStatus.InvalidBlockedColumn,
            planner.Plan(5, 10, flushColumns: new HashSet<int> { 1 }, blockedColumns: new HashSet<int> { 1 }).Status);
        Assert.AreEqual(
            ForwardTurnShapePlanStatus.NoLegalShape,
            planner.Plan(minPoppedCells: 30, maxPoppedCells: 31).Status);
    }

    [TestMethod]
    public void ForwardTurnShapePlannerIsDeterministicForSeedAndKeepsPressureMixed()
    {
        var first = new ForwardTurnShapePlanner(Settings.Default, new Random(14))
            .Plan(minPoppedCells: 14, maxPoppedCells: 17, pressureMode: true);
        var second = new ForwardTurnShapePlanner(Settings.Default, new Random(14))
            .Plan(minPoppedCells: 14, maxPoppedCells: 17, pressureMode: true);

        Assert.AreEqual(ForwardTurnShapePlanStatus.Valid, first.Status);
        Assert.AreEqual(first.PoppedCellCount, second.PoppedCellCount);
        CollectionAssert.AreEqual(
            first.Shape!.Pushers.Select(p => p.PushValue).ToArray(),
            second.Shape!.Pushers.Select(p => p.PushValue).ToArray());
        Assert.IsFalse(first.IsAllSame);
        Assert.IsFalse(first.IsSortedPattern);
        Assert.IsTrue(first.Shape.Pushers.Any(p => p.PushValue == 3));
    }

    [TestMethod]
    public void ForwardTurnShapePlannerAvoidsRepeatedPushBagsWhenLegal()
    {
        var planner = new ForwardTurnShapePlanner(Settings.Default, new Random(20260813));
        var used = new HashSet<long>();

        for (var i = 0; i < Settings.Default.BASE_SPINS; i++)
        {
            var result = planner.Plan(
                minPoppedCells: Settings.Default.COLS * Settings.Default.MIN_PUSH,
                maxPoppedCells: Settings.Default.COLS * Settings.Default.MAX_PUSH,
                avoidedPushBags: used);

            Assert.AreEqual(ForwardTurnShapePlanStatus.Valid, result.Status, result.Detail);
            used.Add(ForwardTurnShapePlanner.PushBagKey(result.Shape!, Settings.Default));
        }

        Assert.AreEqual(Settings.Default.BASE_SPINS, used.Count);
    }

    [TestMethod]
    public void ForwardMathInputResolverBuildsNoWinInputFromEmptyPrizeList()
    {
        var result = new ForwardMathInputResolver(Settings.Default)
            .Resolve(Array.Empty<decimal>(), seed: 1);

        Assert.AreEqual(ForwardMathInputStatus.Valid, result.Status);
        Assert.IsNotNull(result.Bundle);
        Assert.AreEqual(0, result.Bundle!.Covered.Count);
        Assert.AreEqual(0, result.Bundle.Input.Targets.Count);
        Assert.AreEqual(Settings.Default.BASE_SPINS, result.Bundle.Input.BaseSpins);
        Assert.AreEqual(Settings.Default.PrizeLadderRows.Count, result.Bundle.Input.MaxSym);
    }

    [TestMethod]
    public void ForwardMathInputResolverBundlesPrizeAmountsDeterministically()
    {
        var resolver = new ForwardMathInputResolver(Settings.Default);

        var first = resolver.Resolve(new decimal[] { 1, 2, 5 }, seed: 15);
        var second = resolver.Resolve(new decimal[] { 1, 2, 5 }, seed: 15);

        Assert.AreEqual(ForwardMathInputStatus.Valid, first.Status);
        Assert.AreEqual(ForwardMathInputStatus.Valid, second.Status);
        CollectionAssert.AreEqual(first.Bundle!.Covered.ToArray(), second.Bundle!.Covered.ToArray());
        CollectionAssert.AreEqual(
            first.Bundle.Entries.Select(entry => $"{entry.Sym}:{entry.Target}:{entry.Tier}").ToArray(),
            second.Bundle.Entries.Select(entry => $"{entry.Sym}:{entry.Target}:{entry.Tier}").ToArray());
    }

    [TestMethod]
    public void ForwardObjectivePlannerAlwaysPlansNearMissWhenEligible()
    {
        var settings = Settings.Default;
        var input = new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            Required = new Dictionary<string, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        };

        for (var seed = 1; seed <= 100; seed++)
        {
            var result = new ForwardObjectivePlanner(settings).Resolve(input, seed);

            Assert.AreEqual(ForwardObjectiveStatus.Valid, result.Status, result.Detail);
            Assert.IsTrue(result.Objectives!.NearMissTargets.Count > 0, $"seed {seed}");
            Assert.IsTrue(result.Objectives.NearMissTargets.Values.All(target => target >= settings.NONWIN_MIN_TARGET));
        }
    }

    [TestMethod]
    public void ForwardMathInputResolverRejectsBadPrizeInputs()
    {
        var resolver = new ForwardMathInputResolver(Settings.Default);

        Assert.AreEqual(ForwardMathInputStatus.MissingPrizeAmounts, resolver.Resolve(null, seed: 1).Status);
        Assert.AreEqual(ForwardMathInputStatus.NegativePrizeAmount, resolver.Resolve(new decimal[] { -1 }, seed: 1).Status);

        var noLadder = new Settings { PrizeLadderRows = Array.Empty<PrizeLadderRow>() };
        Assert.AreEqual(
            ForwardMathInputStatus.MissingPrizeLadder,
            new ForwardMathInputResolver(noLadder).Resolve(new decimal[] { 1 }, seed: 1).Status);
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
    public void SimFlattensStaleFeatures()
    {
        var board = FilledBoard(1);
        board[0, 0] = Grid.Feat(Settings.Default.F_PRUP, 3, new FP { FeatId = "PRIZE_UPGRADE", PrupSym = 2, PrupTier = 1 });

        Sim.FlatStale(board);

        Assert.AreEqual(3, board[0, 0]!.Sym);
        Assert.IsFalse(board[0, 0]!.IsFeat);
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
    public void ForwardObjectivePlannerBuildsNoWinNearMissesFromConfiguredSymbols()
    {
        var settings = SettingsWithForcedNearMiss(count: 5);
        var input = new MathInput
        {
            Targets = new Dictionary<int, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = 99,
        };

        var result = new ForwardObjectivePlanner(settings).Resolve(input, seed: 16);

        Assert.AreEqual(ForwardObjectiveStatus.Valid, result.Status, result.Detail);
        Assert.IsTrue(result.Objectives!.IsNoWin);
        Assert.AreEqual(settings.PrizeLadderRows.Count, result.Objectives.MaxSymbol);
        CollectionAssert.AreEqual(
            Enumerable.Range(1, settings.PrizeLadderRows.Count).ToArray(),
            result.Objectives.FillSymbols.ToArray());
        Assert.AreEqual(5, result.Objectives.NearMissTargets.Count);
        Assert.IsTrue(result.Objectives.NearMissTargets.Keys.All(symbol => symbol >= 1 && symbol <= settings.PrizeLadderRows.Count));
        Assert.IsTrue(result.Objectives.NearMissTargets.All(kv =>
            kv.Value >= settings.NONWIN_MIN_TARGET && kv.Value < settings.SymbolFillCap(kv.Key)));
    }

    [TestMethod]
    public void ForwardObjectivePlannerAllowsAllConfiguredSymbolsToWin()
    {
        var settings = SettingsWithForcedNearMiss(count: 5);
        var input = new MathInput
        {
            Targets = Enumerable.Range(1, settings.PrizeLadderRows.Count)
                .ToDictionary(symbol => symbol, symbol => settings.SymbolFillCap(symbol)),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        };

        var result = new ForwardObjectivePlanner(settings).Resolve(input, seed: 17);

        Assert.AreEqual(ForwardObjectiveStatus.Valid, result.Status, result.Detail);
        Assert.IsFalse(result.Objectives!.IsNoWin);
        Assert.AreEqual(0, result.Objectives.FillSymbols.Count);
        Assert.AreEqual(0, result.Objectives.NearMissTargets.Count);
        CollectionAssert.AreEqual(
            Enumerable.Range(1, settings.PrizeLadderRows.Count).ToArray(),
            result.Objectives.WinSymbols.ToArray());
    }

    [TestMethod]
    public void ForwardObjectivePlannerRejectsUnsafeTargetsAndOverlaps()
    {
        var settings = SettingsWithForcedNearMiss(count: 5);
        var planner = new ForwardObjectivePlanner(settings);

        var wrongWinTarget = planner.Resolve(new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) - 1 },
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        }, seed: 18);
        Assert.AreEqual(ForwardObjectiveStatus.InvalidWinTarget, wrongWinTarget.Status);

        var overlappingNonWin = planner.Resolve(new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            NonWinTargets = new Dictionary<int, int> { [1] = settings.NONWIN_MIN_TARGET },
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        }, seed: 19);
        Assert.AreEqual(ForwardObjectiveStatus.NonWinOverlapsWin, overlappingNonWin.Status);

        var capCrossingNonWin = planner.Resolve(new MathInput
        {
            Targets = new Dictionary<int, int>(),
            NonWinTargets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        }, seed: 20);
        Assert.AreEqual(ForwardObjectiveStatus.InvalidNonWinTarget, capCrossingNonWin.Status);
    }

    [TestMethod]
    public void ForwardObjectivePlannerRejectsPrizeTierDeclarationsWithoutConfiguredValues()
    {
        var settings = SettingsWithForcedNearMiss(count: 3);
        var planner = new ForwardObjectivePlanner(settings);

        var nonWinningPrizeTier = planner.Resolve(new MathInput
        {
            Targets = new Dictionary<int, int>(),
            PrizeTiers = new Dictionary<int, int> { [2] = 1 },
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 2),
            MaxSym = settings.PrizeLadderRows.Count,
        }, seed: 21);
        Assert.AreEqual(ForwardObjectiveStatus.InvalidPrizeTier, nonWinningPrizeTier.Status);

        var missingNonWinPrizeValue = planner.Resolve(new MathInput
        {
            Targets = new Dictionary<int, int>(),
            NonWinTargets = new Dictionary<int, int> { [2] = settings.NONWIN_MIN_TARGET },
            NonWinPrizeTiers = new Dictionary<int, int> { [2] = 5 },
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 2),
            MaxSym = settings.PrizeLadderRows.Count,
        }, seed: 22);
        Assert.AreEqual(ForwardObjectiveStatus.InvalidNonWinPrizeTier, missingNonWinPrizeValue.Status);
    }

    [TestMethod]
    public void ForwardObjectivePlannerResolvesNearMissDeterministically()
    {
        var settings = SettingsWithForcedNearMiss(count: 4);
        var input = new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        };

        var first = new ForwardObjectivePlanner(settings).Resolve(input, seed: 23);
        var second = new ForwardObjectivePlanner(settings).Resolve(input, seed: 23);

        Assert.AreEqual(ForwardObjectiveStatus.Valid, first.Status, first.Detail);
        Assert.AreEqual(ForwardObjectiveStatus.Valid, second.Status, second.Detail);
        CollectionAssert.AreEqual(
            first.Objectives!.NearMissTargets.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}").ToArray(),
            second.Objectives!.NearMissTargets.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}").ToArray());
        Assert.IsFalse(first.Objectives.NearMissTargets.ContainsKey(1));
    }

    [TestMethod]
    public void ForwardFeatureBudgetPlannerMakesExtraGoCountMatchTotalTurns()
    {
        var settings = SettingsWithNoOptionalFeatures();
        var input = new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            Required = new Dictionary<string, int> { ["EXTRA_SPIN"] = 3 },
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        };
        var objectives = new ForwardObjectivePlanner(settings).Resolve(input, seed: 24).Objectives;

        var result = new ForwardFeatureBudgetPlanner(settings).Plan(input, objectives, seed: 25);

        Assert.AreEqual(ForwardFeatureBudgetStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(3, result.Budget!.ExtraGoCount);
        Assert.AreEqual(settings.BASE_SPINS + 3, result.Budget.TotalTurns);
        Assert.AreEqual(3, result.Budget.BoardFeatureCounts[ForwardFeatureKind.ExtraGo]);
    }

    [TestMethod]
    public void ForwardFeatureBudgetPlannerAddsRequiredExtraGoForHighPressureWins()
    {
        var settings = SettingsWithNoOptionalFeatures();
        var input = new MathInput
        {
            Targets = settings.PrizeLadderRows
                .Select((row, index) => (Symbol: index + 1, row.Target))
                .ToDictionary(item => item.Symbol, item => item.Target),
            Required = new Dictionary<string, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        };
        var objectives = new ForwardObjectivePlanner(settings).Resolve(input, seed: 25).Objectives;

        var result = new ForwardFeatureBudgetPlanner(settings).Plan(input, objectives, seed: 26);

        Assert.AreEqual(ForwardFeatureBudgetStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(settings.MAX_SPINS - settings.BASE_SPINS, result.Budget!.ExtraGoCount);
        Assert.AreEqual(settings.MAX_SPINS, result.Budget.TotalTurns);
        Assert.IsFalse(result.Budget.HasOptionalFeatures);
    }

    [TestMethod]
    public void ForwardFeatureBudgetPlannerInfersPrizeUpgradesFromDeclaredTiers()
    {
        var settings = SettingsWithNoOptionalFeatures();
        var input = new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            PrizeTiers = new Dictionary<int, int> { [1] = 2 },
            Required = new Dictionary<string, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        };
        var objectives = new ForwardObjectivePlanner(settings).Resolve(input, seed: 26).Objectives;

        var result = new ForwardFeatureBudgetPlanner(settings).Plan(input, objectives, seed: 27);

        Assert.AreEqual(ForwardFeatureBudgetStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(2, result.Budget!.RequiredPrizeUpgradeCount);
        Assert.AreEqual(2, result.Budget.PrizeUpgradeCount);
        Assert.AreEqual(2, result.Budget.BoardFeatureCounts[ForwardFeatureKind.PrizeUpgrade]);
    }

    [TestMethod]
    public void ForwardFeatureBudgetPlannerRejectsBadRequiredFeatureCounts()
    {
        var settings = SettingsWithNoOptionalFeatures();
        var input = new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        };
        var objectives = new ForwardObjectivePlanner(settings).Resolve(input, seed: 28).Objectives;

        var unknown = WithRequired(input, new Dictionary<string, int> { ["BONUS"] = 1 });
        Assert.AreEqual(
            ForwardFeatureBudgetStatus.UnknownRequiredFeature,
            new ForwardFeatureBudgetPlanner(settings).Plan(unknown, objectives, seed: 29).Status);

        var negative = WithRequired(input, new Dictionary<string, int> { ["WHEEL"] = -1 });
        Assert.AreEqual(
            ForwardFeatureBudgetStatus.NegativeRequiredFeature,
            new ForwardFeatureBudgetPlanner(settings).Plan(negative, objectives, seed: 30).Status);

        var tooManyWheel = WithRequired(input, new Dictionary<string, int> { ["WHEEL"] = settings.WheelFeatureConfig.Max + 1 });
        Assert.AreEqual(
            ForwardFeatureBudgetStatus.RequiredFeatureExceedsLimit,
            new ForwardFeatureBudgetPlanner(settings).Plan(tooManyWheel, objectives, seed: 31).Status);
    }

    [TestMethod]
    public void ForwardFeatureBudgetPlannerRejectsPrizeUpgradeCountMismatch()
    {
        var settings = SettingsWithNoOptionalFeatures();
        var input = new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            PrizeTiers = new Dictionary<int, int> { [1] = 2 },
            Required = new Dictionary<string, int> { ["PRIZE_UPGRADE"] = 1 },
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        };
        var objectives = new ForwardObjectivePlanner(settings).Resolve(input, seed: 32).Objectives;

        var result = new ForwardFeatureBudgetPlanner(settings).Plan(input, objectives, seed: 33);

        Assert.AreEqual(ForwardFeatureBudgetStatus.PrizeUpgradeRequirementMismatch, result.Status);
    }

    [TestMethod]
    public void ForwardFeatureBudgetPlannerCanAddRareOptionalNoWinExtraGo()
    {
        var settings = new Settings
        {
            PNoWinExtraGoOptional = 1.0,
            POptionalFeatureTicket = 0.0,
            PFlushOptional = 0.0,
        };
        var input = new MathInput
        {
            Targets = new Dictionary<int, int>(),
            Required = new Dictionary<string, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        };
        var objectives = new ForwardObjectivePlanner(settings).Resolve(input, seed: 34).Objectives;

        var result = new ForwardFeatureBudgetPlanner(settings).Plan(input, objectives, seed: 35);

        Assert.AreEqual(ForwardFeatureBudgetStatus.Valid, result.Status, result.Detail);
        Assert.IsTrue(result.Budget!.ExtraGoCount >= 1);
        Assert.IsTrue(result.Budget.ExtraGoCount <= settings.MAX_SPINS - settings.BASE_SPINS);
        Assert.AreEqual(settings.BASE_SPINS + result.Budget.ExtraGoCount, result.Budget.TotalTurns);
        Assert.IsTrue(result.Budget.HasOptionalFeatures);
    }

    [TestMethod]
    public void ForwardFeatureBudgetPlannerCanAddDedicatedOptionalFlush()
    {
        var settings = new Settings
        {
            PNoWinExtraGoOptional = 0.0,
            POptionalFeatureTicket = 0.0,
            PFlushOptional = 1.0,
        };
        var input = new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            Required = new Dictionary<string, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        };
        var objectives = new ForwardObjectivePlanner(settings).Resolve(input, seed: 35).Objectives;

        var result = new ForwardFeatureBudgetPlanner(settings).Plan(input, objectives, seed: 36);

        Assert.AreEqual(ForwardFeatureBudgetStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(1, result.Budget!.FlushCount);
        Assert.IsTrue(result.Budget.HasOptionalFeatures);
    }

    [TestMethod]
    public void ForwardFeatureBudgetPlannerCanAddDedicatedOptionalWheel()
    {
        var settings = new Settings
        {
            PNoWinExtraGoOptional = 0.0,
            POptionalFeatureTicket = 0.0,
            PWheelOptional = 1.0,
            PFlushOptional = 0.0,
        };
        var input = new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            Required = new Dictionary<string, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        };
        var objectives = new ForwardObjectivePlanner(settings).Resolve(input, seed: 135).Objectives;

        var result = new ForwardFeatureBudgetPlanner(settings).Plan(input, objectives, seed: 136);

        Assert.AreEqual(ForwardFeatureBudgetStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(1, result.Budget!.WheelCount);
        Assert.IsTrue(result.Budget.HasOptionalFeatures);
    }

    [TestMethod]
    public void ForwardFeatureBudgetPlannerSkipsOptionalFlushWithoutSafeFiller()
    {
        var settings = new Settings
        {
            PNoWinExtraGoOptional = 0.0,
            POptionalFeatureTicket = 1.0,
            PFlushOptional = 1.0,
            POptionalTicketFlush = 1.0,
            POptionalTicketWheel = 0.0,
            POptionalTicketPrizeUpgrade = 0.0,
        };
        var input = new MathInput
        {
            Targets = settings.PrizeLadderRows
                .Select((row, index) => (Symbol: index + 1, row.Target))
                .ToDictionary(item => item.Symbol, item => item.Target),
            Required = new Dictionary<string, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        };
        var objectives = new ForwardObjectivePlanner(settings).Resolve(input, seed: 365).Objectives;

        var result = new ForwardFeatureBudgetPlanner(settings).Plan(input, objectives, seed: 366);

        Assert.AreEqual(ForwardFeatureBudgetStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(0, result.Budget!.FlushCount);
        Assert.IsFalse(result.Budget.HasOptionalFeatures);
    }

    [TestMethod]
    public void ForwardFeatureBudgetPlannerCanAddOptionalTicketFeaturesWithinCaps()
    {
        var settings = new Settings
        {
            PNoWinExtraGoOptional = 0.0,
            PFlushOptional = 0.0,
            POptionalFeatureTicket = 1.0,
            POptionalTicketWheel = 1.0,
            POptionalTicketFlush = 1.0,
            POptionalTicketPrizeUpgrade = 1.0,
        };
        var input = new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            NonWinTargets = new Dictionary<int, int> { [2] = settings.NONWIN_MIN_TARGET },
            Required = new Dictionary<string, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        };
        var objectives = new ForwardObjectivePlanner(settings).Resolve(input, seed: 36).Objectives;

        var result = new ForwardFeatureBudgetPlanner(settings).Plan(input, objectives, seed: 37);

        Assert.AreEqual(ForwardFeatureBudgetStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(1, result.Budget!.WheelCount);
        Assert.AreEqual(1, result.Budget.FlushCount);
        Assert.AreEqual(1, result.Budget.OptionalPrizeUpgradeCount);
        Assert.IsTrue(result.Budget.HasOptionalFeatures);
    }

    [TestMethod]
    public void ForwardFeatureTimingPlannerSchedulesLateExtraGoChainSafely()
    {
        var settings = new Settings { PFeatureLatePlacement = 1.0 };
        var budget = new ForwardFeatureBudget(
            settings.BASE_SPINS,
            settings.BASE_SPINS + 3,
            wheelCount: 1,
            flushCount: 1,
            extraGoCount: 3,
            requiredPrizeUpgradeCount: 1,
            optionalPrizeUpgradeCount: 0,
            hasOptionalFeatures: false);

        var result = new ForwardFeatureTimingPlanner(settings, seed: 38).Plan(budget);

        Assert.AreEqual(ForwardFeatureTimingStatus.Valid, result.Status, result.Detail);
        CollectionAssert.AreEqual(
            new[] { settings.BASE_SPINS, settings.BASE_SPINS + 1, settings.BASE_SPINS + 2 },
            result.Timing!.TurnsFor(ForwardTimedFeatureKind.ExtraGo).ToArray());
        Assert.IsTrue(result.Timing.Events.All(feature => feature.Turn < budget.TotalTurns));
        Assert.IsTrue(result.Timing.TurnsFor(ForwardTimedFeatureKind.Wheel).All(turn => turn >= budget.TotalTurns - settings.WinLateTailSpins));
        Assert.IsTrue(result.Timing.TurnsFor(ForwardTimedFeatureKind.Flush).All(turn => turn >= budget.TotalTurns - settings.WinLateTailSpins));
        Assert.IsTrue(result.Timing.TurnsFor(ForwardTimedFeatureKind.PrizeUpgrade).All(turn => turn >= budget.TotalTurns - settings.WinLateTailSpins));
    }

    [TestMethod]
    public void ForwardFeatureTimingPlannerRejectsInvalidExtraGoEnvelope()
    {
        var settings = Settings.Default;
        var budget = new ForwardFeatureBudget(
            settings.BASE_SPINS,
            settings.BASE_SPINS + 3,
            wheelCount: 0,
            flushCount: 0,
            extraGoCount: 2,
            requiredPrizeUpgradeCount: 0,
            optionalPrizeUpgradeCount: 0,
            hasOptionalFeatures: false);

        var result = new ForwardFeatureTimingPlanner(settings, seed: 39).Plan(budget);

        Assert.AreEqual(ForwardFeatureTimingStatus.InvalidTurnEnvelope, result.Status);
    }

    [TestMethod]
    public void ForwardFeatureTimingPlannerRejectsFeatureWindowWithNoLegalTurn()
    {
        var settings = new Settings
        {
            WheelFeatureConfig = (1.0, 1, 9, 9, 1),
        };
        var budget = new ForwardFeatureBudget(
            settings.BASE_SPINS,
            settings.BASE_SPINS,
            wheelCount: 1,
            flushCount: 0,
            extraGoCount: 0,
            requiredPrizeUpgradeCount: 0,
            optionalPrizeUpgradeCount: 0,
            hasOptionalFeatures: false);

        var result = new ForwardFeatureTimingPlanner(settings, seed: 40).Plan(budget);

        Assert.AreEqual(ForwardFeatureTimingStatus.NoLegalTurn, result.Status);
    }

    [TestMethod]
    public void ForwardFeatureTimingPlannerRejectsExtraGoWindowWithNoEarnableTurn()
    {
        var settings = new Settings
        {
            ExtraSpinFeatureConfig = (1.0, 1, 9, 9, 1),
        };
        var budget = new ForwardFeatureBudget(
            settings.BASE_SPINS,
            settings.BASE_SPINS + 1,
            wheelCount: 0,
            flushCount: 0,
            extraGoCount: 1,
            requiredPrizeUpgradeCount: 0,
            optionalPrizeUpgradeCount: 0,
            hasOptionalFeatures: false);

        var result = new ForwardFeatureTimingPlanner(settings, seed: 41).Plan(budget);

        Assert.AreEqual(ForwardFeatureTimingStatus.NoLegalTurn, result.Status);
    }

    [TestMethod]
    public void ForwardFeatureTimingPlannerNeverSchedulesSimpleFeaturesOnFinalTurn()
    {
        var settings = new Settings { PFeatureLatePlacement = 1.0 };
        var budget = new ForwardFeatureBudget(
            settings.BASE_SPINS,
            settings.BASE_SPINS,
            wheelCount: 3,
            flushCount: 3,
            extraGoCount: 0,
            requiredPrizeUpgradeCount: 2,
            optionalPrizeUpgradeCount: 0,
            hasOptionalFeatures: false);

        var result = new ForwardFeatureTimingPlanner(settings, seed: 42).Plan(budget);

        Assert.AreEqual(ForwardFeatureTimingStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(3, result.Timing!.Count(ForwardTimedFeatureKind.Wheel));
        Assert.AreEqual(3, result.Timing.Count(ForwardTimedFeatureKind.Flush));
        Assert.AreEqual(2, result.Timing.Count(ForwardTimedFeatureKind.PrizeUpgrade));
        Assert.IsTrue(result.Timing.Events.All(feature => feature.Turn < budget.TotalTurns));
    }

    [TestMethod]
    public void ForwardFeatureTimingPlannerKeepsPrizeUpgradesOnDistinctTurns()
    {
        var settings = new Settings { PFeatureLatePlacement = 1.0 };
        var budget = new ForwardFeatureBudget(
            settings.BASE_SPINS,
            settings.BASE_SPINS,
            wheelCount: 0,
            flushCount: 0,
            extraGoCount: 0,
            requiredPrizeUpgradeCount: 2,
            optionalPrizeUpgradeCount: 0,
            hasOptionalFeatures: false);

        for (var seed = 1; seed <= 100; seed++)
        {
            var result = new ForwardFeatureTimingPlanner(settings, seed).Plan(budget);
            Assert.AreEqual(ForwardFeatureTimingStatus.Valid, result.Status, result.Detail);

            var turns = result.Timing!.TurnsFor(ForwardTimedFeatureKind.PrizeUpgrade);
            Assert.AreEqual(2, turns.Count);
            Assert.AreEqual(2, turns.Distinct().Count());
            Assert.IsTrue(turns.All(turn => turn < budget.TotalTurns));
        }
    }

    [TestMethod]
    public void ForwardTurnFramePlannerBuildsPlayableFramesAndFutureViews()
    {
        var settings = Settings.Default;
        var budget = new ForwardFeatureBudget(
            settings.BASE_SPINS,
            settings.BASE_SPINS + 1,
            wheelCount: 1,
            flushCount: 1,
            extraGoCount: 1,
            requiredPrizeUpgradeCount: 0,
            optionalPrizeUpgradeCount: 0,
            hasOptionalFeatures: false);
        var intents = new ForwardFeatureIntentPlan(
            budget.TotalTurns,
            new[]
            {
                ForwardFeatureIntent.ExtraGo(2),
                ForwardFeatureIntent.Flush(3),
                ForwardFeatureIntent.Wheel(4, symbol: 2, stackValue: 1),
            },
            new Dictionary<int, int>(),
            new Dictionary<int, int>());

        var result = new ForwardTurnFramePlanner(settings, seed: 49).Plan(budget, intents);

        Assert.AreEqual(ForwardTurnFrameStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(budget.TotalTurns, result.Plan!.Frames.Count);
        Assert.AreEqual(0, result.Plan.FeatureIntentsForTurn(budget.TotalTurns).Count);
        Assert.AreEqual(budget.TotalTurns - 1, result.Plan.FutureTurnsAfter(1).Count);

        var flushFrame = result.Plan.ByTurn[3];
        Assert.AreEqual(1, flushFrame.FlushColumns.Count);
        Assert.AreEqual(1, flushFrame.Shape.Pushers.Count(pusher => pusher.FeatureId == settings.F_FLUSH_ID));
        Assert.AreEqual(settings.ROWS, flushFrame.Shape.Pushers.Single(pusher => pusher.FeatureId == settings.F_FLUSH_ID).PushValue);

        var normalPushes = result.Plan.ByTurn[1].Shape.Pushers
            .Where(pusher => !pusher.IsFlush(settings))
            .Select(pusher => pusher.PushValue)
            .ToArray();
        Assert.IsTrue(normalPushes.Distinct().Count() >= 2);
        Assert.IsTrue(normalPushes.Contains(settings.MAX_PUSH));
        Assert.IsTrue(normalPushes.All(push => push >= settings.MIN_PUSH && push <= settings.MAX_PUSH));
    }

    [TestMethod]
    public void ForwardTurnFramePlannerRejectsIntentBudgetCountMismatch()
    {
        var settings = Settings.Default;
        var budget = new ForwardFeatureBudget(
            settings.BASE_SPINS,
            settings.BASE_SPINS,
            wheelCount: 0,
            flushCount: 1,
            extraGoCount: 0,
            requiredPrizeUpgradeCount: 0,
            optionalPrizeUpgradeCount: 0,
            hasOptionalFeatures: false);
        var intents = new ForwardFeatureIntentPlan(
            budget.TotalTurns,
            Array.Empty<ForwardFeatureIntent>(),
            new Dictionary<int, int>(),
            new Dictionary<int, int>());

        var result = new ForwardTurnFramePlanner(settings, seed: 50).Plan(budget, intents);

        Assert.AreEqual(ForwardTurnFrameStatus.FeatureCountMismatch, result.Status);
    }

    [TestMethod]
    public void ForwardTurnFramePlannerRejectsAnyFinalTurnFeature()
    {
        var settings = Settings.Default;
        var budget = new ForwardFeatureBudget(
            settings.BASE_SPINS,
            settings.BASE_SPINS,
            wheelCount: 1,
            flushCount: 0,
            extraGoCount: 0,
            requiredPrizeUpgradeCount: 0,
            optionalPrizeUpgradeCount: 0,
            hasOptionalFeatures: false);
        var intents = new ForwardFeatureIntentPlan(
            budget.TotalTurns,
            new[] { ForwardFeatureIntent.Wheel(budget.TotalTurns, symbol: 2, stackValue: 1) },
            new Dictionary<int, int>(),
            new Dictionary<int, int>());

        var result = new ForwardTurnFramePlanner(settings, seed: 51).Plan(budget, intents);

        Assert.AreEqual(ForwardTurnFrameStatus.FeatureOnFinalTurn, result.Status);
    }

    [TestMethod]
    public void ForwardTurnFramePlannerRejectsMoreFlushesThanColumns()
    {
        var settings = Settings.Default;
        var flushes = Enumerable.Range(0, settings.COLS + 1)
            .Select(_ => ForwardFeatureIntent.Flush(turn: 2))
            .ToArray();
        var budget = new ForwardFeatureBudget(
            settings.BASE_SPINS,
            settings.BASE_SPINS,
            wheelCount: 0,
            flushCount: flushes.Length,
            extraGoCount: 0,
            requiredPrizeUpgradeCount: 0,
            optionalPrizeUpgradeCount: 0,
            hasOptionalFeatures: false);
        var intents = new ForwardFeatureIntentPlan(
            budget.TotalTurns,
            flushes,
            new Dictionary<int, int>(),
            new Dictionary<int, int>());

        var result = new ForwardTurnFramePlanner(settings, seed: 52).Plan(budget, intents);

        Assert.AreEqual(ForwardTurnFrameStatus.TooManyFlushColumns, result.Status);
    }

    [TestMethod]
    public void ForwardNormalIntentPlannerAssignsRequiredDemandOnlyToCollectingSlots()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            NonWinTargets = new Dictionary<int, int> { [2] = settings.NONWIN_MIN_TARGET },
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var ledger = new SymbolLedger(objectives.WinTargets, objectives.NearMissTargets, objectives.MaxSymbol, settings);
        Assert.AreEqual(SymbolCollectionStatus.Valid, ledger.Collect(1, 18).Status);
        Assert.AreEqual(SymbolCollectionStatus.Valid, ledger.Collect(2, 9).Status);
        var positions = Positions((4, 0), (4, 1), (4, 2), (0, 0), (0, 1));
        var futureTurns = new[] { new ForwardFutureTurn(2, Shape(1, 1, 1, 1, 1)) };

        var result = new ForwardNormalIntentPlanner(settings).Plan(
            turn: 1,
            objectives,
            positions,
            futureTurns,
            ledger);

        Assert.AreEqual(ForwardNormalIntentStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(3, result.CollectingSlots);
        Assert.AreEqual(2, result.ResidueSlots);
        Assert.AreEqual(2, result.WinProgressCount);
        Assert.AreEqual(1, result.NearMissProgressCount);
        Assert.AreEqual(0, result.SafeFillerCount);
        Assert.AreEqual(2, result.ResidueCount);
        Assert.AreEqual(2, result.Intents.Count(intent => intent.CollectIntent == ForwardSymbolIntent.MustProgressWin));
        Assert.AreEqual(1, result.Intents.Count(intent => intent.CollectIntent == ForwardSymbolIntent.PreferNearMiss));
        Assert.AreEqual(2, result.Intents.Count(intent => intent.CollectIntent == ForwardSymbolIntent.ResidueOnly));
    }

    [TestMethod]
    public void ForwardNormalIntentPlannerFailsWhenRequiredCollectionsCannotFit()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            NonWinTargets = new Dictionary<int, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var ledger = new SymbolLedger(objectives.WinTargets, objectives.NearMissTargets, objectives.MaxSymbol, settings);
        var positions = Positions((0, 0), (0, 1));

        var result = new ForwardNormalIntentPlanner(settings).Plan(
            turn: settings.BASE_SPINS,
            objectives,
            positions,
            Array.Empty<ForwardFutureTurn>(),
            ledger);

        Assert.AreEqual(ForwardNormalIntentStatus.InsufficientCollectionCapacity, result.Status);
        Assert.AreEqual(0, result.CollectingSlots);
        Assert.AreEqual(2, result.ResidueSlots);
    }

    [TestMethod]
    public void ForwardNormalIntentPlannerUsesCurrentCollectionCapacityBeforeSafeFiller()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            NonWinTargets = new Dictionary<int, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var ledger = new SymbolLedger(objectives.WinTargets, objectives.NearMissTargets, objectives.MaxSymbol, settings);
        Assert.AreEqual(SymbolCollectionStatus.Valid, ledger.Collect(1, 16).Status);
        var positions = Positions((4, 0), (4, 1), (4, 2), (4, 3), (4, 4));
        var futureTurns = new[]
        {
            new ForwardFutureTurn(2, Shape(1, 1, 1, 1, 1)),
            new ForwardFutureTurn(3, Shape(5, 5, 5, 5, 5)),
        };

        var result = new ForwardNormalIntentPlanner(settings).Plan(
            turn: 1,
            objectives,
            positions,
            futureTurns,
            ledger);

        Assert.AreEqual(ForwardNormalIntentStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(5, result.CollectingSlots);
        Assert.AreEqual(4, result.WinProgressCount);
        Assert.AreEqual(1, result.SafeFillerCount);
    }

    [TestMethod]
    public void ForwardStartingBoardPlannerBuildsNormalBoardAndReservesCollectedCells()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            NonWinTargets = new Dictionary<int, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var ledger = new SymbolLedger(objectives.WinTargets, objectives.NearMissTargets, objectives.MaxSymbol, settings);
        Assert.AreEqual(SymbolCollectionStatus.Valid, ledger.Collect(1, 17).Status);
        var frames = new ForwardTurnFramePlan(
            totalTurns: 1,
            new[] { new ForwardTurnFrame(1, Shape(1, 1, 1, 1, 1), Array.Empty<ForwardFeatureIntent>(), new HashSet<int>()) });

        var result = new ForwardStartingBoardPlanner(settings, seed: 61).Plan(objectives, frames, ledger);

        Assert.AreEqual(ForwardStartingBoardStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(5, result.CollectingCells);
        Assert.AreEqual(20, result.ResidueCells);
        Assert.AreEqual(3, result.WinProgressCount);
        Assert.AreEqual(2, result.SafeFillerCount);
        Assert.AreEqual(settings.SymbolFillCap(1), ledger.CollectedCount(1));
        Assert.IsFalse(result.Board!.Cast<Cell?>().Any(cell => cell == null || settings.IsFeat(cell.Sym)));
    }

    [TestMethod]
    public void ForwardStartingBoardPlannerAllowsResidueOnlyWinSymbolsWithoutLedgerChange()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            NonWinTargets = new Dictionary<int, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var ledger = new SymbolLedger(objectives.WinTargets, objectives.NearMissTargets, objectives.MaxSymbol, settings);
        Assert.AreEqual(SymbolCollectionStatus.Valid, ledger.Collect(1, settings.SymbolFillCap(1)).Status);
        var frames = new ForwardTurnFramePlan(totalTurns: 0, Array.Empty<ForwardTurnFrame>());

        var result = new ForwardStartingBoardPlanner(settings, seed: 62).Plan(objectives, frames, ledger);

        Assert.AreEqual(ForwardStartingBoardStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(0, result.CollectingCells);
        Assert.AreEqual(settings.ROWS * settings.COLS, result.ResidueCells);
        Assert.AreEqual(settings.SymbolFillCap(1), ledger.CollectedCount(1));
        Assert.IsFalse(result.Board!.Cast<Cell?>().Any(cell => cell == null || settings.IsFeat(cell.Sym)));
    }

    [TestMethod]
    public void ForwardStartingBoardPlannerFailsWithoutMutatingLedgerWhenDemandCannotFit()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            NonWinTargets = new Dictionary<int, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var ledger = new SymbolLedger(objectives.WinTargets, objectives.NearMissTargets, objectives.MaxSymbol, settings);
        var frames = new ForwardTurnFramePlan(
            totalTurns: 1,
            new[] { new ForwardTurnFrame(1, Shape(1, 1, 1, 1, 1), Array.Empty<ForwardFeatureIntent>(), new HashSet<int>()) });

        var result = new ForwardStartingBoardPlanner(settings, seed: 63).Plan(objectives, frames, ledger);

        Assert.AreEqual(ForwardStartingBoardStatus.IntentPlanningFailed, result.Status);
        Assert.AreEqual(5, result.CollectingCells);
        Assert.AreEqual(20, result.ResidueCells);
        Assert.AreEqual(0, ledger.CollectedCount(1));
    }

    [TestMethod]
    public void ForwardTurnRealizerPlacesFeaturesBeforePlanningNormalIntents()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            NonWinTargets = new Dictionary<int, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var board = new ForwardBoardState(FilledBoard(6), settings);
        var symbolLedger = new SymbolLedger(objectives.WinTargets, objectives.NearMissTargets, objectives.MaxSymbol, settings);
        Assert.AreEqual(SymbolCollectionStatus.Valid, symbolLedger.Collect(1, settings.SymbolFillCap(1) - 2).Status);
        var extraLedger = new ForwardExtraSpinLedger(settings.BASE_SPINS + 1, settings);
        var prizeLedger = EmptyPrizeLedger(objectives.MaxSymbol);
        var frame = new ForwardTurnFrame(
            turn: 1,
            Shape(1, 1, 1, 1, 1),
            new[] { ForwardFeatureIntent.ExtraGo(turn: 1) },
            new HashSet<int>());
        var futureTurns = new[] { new ForwardFutureTurn(2, Shape(5, 5, 5, 5, 5)) };

        var result = new ForwardTurnRealizer(settings, seed: 71).RealizeAndAdvance(
            frame,
            plannedTotalTurns: settings.BASE_SPINS + 1,
            board,
            objectives,
            futureTurns,
            remainingFeatureCapacity: FeatureCapacity((ForwardFeatureKind.ExtraGo, 1)),
            symbolLedger,
            extraLedger,
            prizeLedger);

        Assert.AreEqual(ForwardTurnRealizationStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(5, result.Spawns.Count);
        Assert.AreEqual(1, result.Spawns.Count(spawn => spawn.Cell.Sym == settings.F_XSPIN));
        Assert.AreEqual(4, result.NormalIntentResult!.CollectingSlots);
        Assert.AreEqual(2, result.NormalIntentResult.WinProgressCount);
        Assert.AreEqual(2, result.NormalIntentResult.SafeFillerCount);
        Assert.AreEqual(settings.SymbolFillCap(1), symbolLedger.CollectedCount(1));
        Assert.AreEqual(settings.BASE_SPINS + 1, extraLedger.EarnedTurns);
    }

    [TestMethod]
    public void ForwardTurnRealizerDoesNotMutateBoardOrLedgersWhenNormalDemandCannotFit()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            NonWinTargets = new Dictionary<int, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var board = new ForwardBoardState(FilledBoard(6), settings);
        var before = BoardSignature(board.Snapshot());
        var symbolLedger = new SymbolLedger(objectives.WinTargets, objectives.NearMissTargets, objectives.MaxSymbol, settings);
        var extraLedger = new ForwardExtraSpinLedger(settings.BASE_SPINS, settings);
        var frame = new ForwardTurnFrame(
            turn: settings.BASE_SPINS,
            Shape(1, 1, 1, 1, 1),
            Array.Empty<ForwardFeatureIntent>(),
            new HashSet<int>());

        var result = new ForwardTurnRealizer(settings, seed: 72).RealizeAndAdvance(
            frame,
            plannedTotalTurns: settings.BASE_SPINS,
            board,
            objectives,
            futureTurns: Array.Empty<ForwardFutureTurn>(),
            remainingFeatureCapacity: FeatureCapacity(),
            symbolLedger,
            extraLedger,
            prizeUpgradeLedger: EmptyPrizeLedger(objectives.MaxSymbol));

        Assert.AreEqual(ForwardTurnRealizationStatus.NormalIntentPlanningFailed, result.Status);
        Assert.AreEqual(before, BoardSignature(board.Snapshot()));
        Assert.AreEqual(0, symbolLedger.CollectedCount(1));
        Assert.AreEqual(settings.BASE_SPINS, extraLedger.EarnedTurns);
    }

    [TestMethod]
    public void ForwardTurnRealizerRejectsFinalTurnFeatureWithoutMutation()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int>(),
            NonWinTargets = new Dictionary<int, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var board = new ForwardBoardState(FilledBoard(6), settings);
        var before = BoardSignature(board.Snapshot());
        var extraLedger = new ForwardExtraSpinLedger(settings.BASE_SPINS, settings);
        var frame = new ForwardTurnFrame(
            turn: settings.BASE_SPINS,
            Shape(1, 1, 1, 1, 1),
            new[] { ForwardFeatureIntent.ExtraGo(turn: settings.BASE_SPINS) },
            new HashSet<int>());

        var result = new ForwardTurnRealizer(settings, seed: 73).RealizeAndAdvance(
            frame,
            plannedTotalTurns: settings.BASE_SPINS,
            board,
            objectives,
            futureTurns: Array.Empty<ForwardFutureTurn>(),
            remainingFeatureCapacity: FeatureCapacity((ForwardFeatureKind.ExtraGo, 1)),
            symbolLedger: new SymbolLedger(objectives.WinTargets, objectives.NearMissTargets, objectives.MaxSymbol, settings),
            extraLedger,
            prizeUpgradeLedger: EmptyPrizeLedger(objectives.MaxSymbol));

        Assert.AreEqual(ForwardTurnRealizationStatus.FeaturePlacementInvalid, result.Status);
        Assert.AreEqual(before, BoardSignature(board.Snapshot()));
        Assert.AreEqual(settings.BASE_SPINS, extraLedger.EarnedTurns);
    }

    [TestMethod]
    public void ForwardTurnCycleExecutorAdvancesFiresAndAuditsFeaturePayloads()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int>(),
            NonWinTargets = new Dictionary<int, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var board = new ForwardBoardState(FilledBoard(6), settings);
        var symbolLedger = new SymbolLedger(objectives.WinTargets, objectives.NearMissTargets, objectives.MaxSymbol, settings);
        var extraLedger = new ForwardExtraSpinLedger(settings.BASE_SPINS + 1, settings);
        var prizeLedger = new ForwardPrizeUpgradeLedger(
            new Dictionary<int, int> { [2] = 1 },
            objectives.PrizeValues,
            objectives.MaxSymbol,
            settings);
        var frame = new ForwardTurnFrame(
            turn: 1,
            Shape(1, 1, 1, 1, 1),
            new[]
            {
                ForwardFeatureIntent.ExtraGo(turn: 1),
                ForwardFeatureIntent.Wheel(turn: 1, symbol: 2, stackValue: 1),
                ForwardFeatureIntent.PrizeUpgrade(turn: 1, symbol: 2, tier: 1, prizeValue: objectives.PrizeValues[2][1]),
            },
            new HashSet<int>());

        var result = new ForwardTurnCycleExecutor(settings, seed: 81).ExecuteAndAdvance(
            frame,
            plannedTotalTurns: settings.BASE_SPINS + 1,
            board,
            objectives,
            futureTurns: new[] { new ForwardFutureTurn(2, Shape(1, 1, 1, 1, 1)) },
            remainingFeatureCapacity: FeatureCapacity(
                (ForwardFeatureKind.ExtraGo, 1),
                (ForwardFeatureKind.Wheel, 1),
                (ForwardFeatureKind.PrizeUpgrade, 1)),
            symbolLedger,
            extraLedger,
            prizeLedger);

        var after = board.Snapshot();

        Assert.AreEqual(ForwardTurnCycleStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(3, result.Realization!.Spawns.Count(spawn => spawn.Cell.IsFeat));
        Assert.AreEqual(3, result.FeatureFire!.Events.Count);
        Assert.AreEqual(1, result.FeatureFire.ExtraGoAwardCount);
        Assert.AreEqual(1, result.FeatureFire.WheelFireCount);
        Assert.AreEqual(1, result.FeatureFire.PrizeUpgradeFireCount);
        Assert.IsFalse(after.Cast<Cell?>().Any(cell => cell?.IsFeat == true));
        Assert.IsTrue(after.Cast<Cell?>().Any(cell => cell?.Sym == 2 && cell.Stack >= 2));
        Assert.AreEqual(settings.BASE_SPINS + 1, extraLedger.EarnedTurns);
        Assert.AreEqual(1, extraLedger.LogicalExtraGoAwards);
        Assert.AreEqual(1, prizeLedger.CurrentTier(2));
    }

    [TestMethod]
    public void ForwardTurnCycleExecutorRejectsUnfiredFeatureAtTurnStartWithoutMutation()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int>(),
            NonWinTargets = new Dictionary<int, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var rawBoard = FilledBoard(6);
        rawBoard[0, 0] = Grid.Feat(settings.F_XSPIN, 2, new FP { FeatId = "EXTRA_SPIN" });
        var board = new ForwardBoardState(rawBoard, settings);
        var before = BoardSignature(board.Snapshot());
        var extraLedger = new ForwardExtraSpinLedger(settings.BASE_SPINS, settings);

        var result = new ForwardTurnCycleExecutor(settings, seed: 82).ExecuteAndAdvance(
            new ForwardTurnFrame(1, Shape(1, 1, 1, 1, 1), Array.Empty<ForwardFeatureIntent>(), new HashSet<int>()),
            plannedTotalTurns: settings.BASE_SPINS,
            board,
            objectives,
            futureTurns: new[] { new ForwardFutureTurn(2, Shape(1, 1, 1, 1, 1)) },
            remainingFeatureCapacity: FeatureCapacity(),
            symbolLedger: new SymbolLedger(objectives.WinTargets, objectives.NearMissTargets, objectives.MaxSymbol, settings),
            extraLedger,
            prizeUpgradeLedger: EmptyPrizeLedger(objectives.MaxSymbol));

        Assert.AreEqual(ForwardTurnCycleStatus.BoardHasUnfiredFeatureCells, result.Status);
        Assert.AreEqual(before, BoardSignature(board.Snapshot()));
        Assert.AreEqual(settings.BASE_SPINS, extraLedger.EarnedTurns);
    }

    [TestMethod]
    public void ForwardTurnCycleExecutorAuditRejectsWrongConvertToId()
    {
        var settings = Settings.Default;
        var spawn = new ForwardSpawn(
            0,
            0,
            Grid.Feat(settings.F_XSPIN, 2, new FP { FeatId = "EXTRA_SPIN" }));
        var fireEvent = new ForwardFeatureFireEvent
        {
            Row = 0,
            Col = 0,
            FeatureSymbol = settings.F_XSPIN,
            ConvertToSymbol = 3,
            ExtraGoAward = 1,
        };

        var detail = new ForwardTurnCycleExecutor(settings, seed: 83).AuditFeatureFire(
            new[] { spawn },
            new[] { fireEvent });

        Assert.IsNotNull(detail);
        StringAssert.Contains(detail!, "ConvertToId");
    }

    [TestMethod]
    public void ForwardTurnCycleExecutorAuditRejectsWheelBeforeLaterNonWheel()
    {
        var settings = Settings.Default;
        var wheel = new ForwardSpawn(
            0,
            0,
            Grid.Feat(settings.F_WHEEL, 2, new FP { FeatId = "WHEEL", WheelSym = 2, WheelStack = 2 }));
        var extra = new ForwardSpawn(
            0,
            1,
            Grid.Feat(settings.F_XSPIN, 2, new FP { FeatId = "EXTRA_SPIN" }));

        var detail = new ForwardTurnCycleExecutor(settings, seed: 84).AuditFeatureFire(
            new[] { wheel, extra },
            new[]
            {
                new ForwardFeatureFireEvent
                {
                    Row = 0,
                    Col = 0,
                    FeatureSymbol = settings.F_WHEEL,
                    ConvertToSymbol = 2,
                    WheelSymbol = 2,
                    WheelStack = 2,
                },
                new ForwardFeatureFireEvent
                {
                    Row = 0,
                    Col = 1,
                    FeatureSymbol = settings.F_XSPIN,
                    ConvertToSymbol = 2,
                    ExtraGoAward = 1,
                },
            });

        Assert.IsNotNull(detail);
        StringAssert.Contains(detail!, "WHEEL fire event");
    }

    [TestMethod]
    public void ForwardTicketPipelineExecutorBuildsCompleteNoFeatureWinTicket()
    {
        var settings = SettingsWithNoOptionalFeatures();
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            NonWinTargets = new Dictionary<int, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var budget = new ForwardFeatureBudget(
            settings.BASE_SPINS,
            settings.BASE_SPINS,
            wheelCount: 0,
            flushCount: 0,
            extraGoCount: 0,
            requiredPrizeUpgradeCount: 0,
            optionalPrizeUpgradeCount: 0,
            hasOptionalFeatures: false);
        var intentPlan = new ForwardFeatureIntentPlan(
            settings.BASE_SPINS,
            Array.Empty<ForwardFeatureIntent>(),
            new Dictionary<int, int>(),
            new Dictionary<int, int>());
        var frameResult = new ForwardTurnFramePlanner(settings, seed: 91).Plan(budget, intentPlan);
        Assert.AreEqual(ForwardTurnFrameStatus.Valid, frameResult.Status, frameResult.Detail);

        var result = new ForwardTicketPipelineExecutor(settings, seed: 92).Execute(
            objectives,
            frameResult.Plan);

        Assert.AreEqual(ForwardTicketPipelineStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(settings.BASE_SPINS, result.Plan!.Turns.Count);
        Assert.AreEqual(settings.SymbolFillCap(1), result.Plan.PlannedCollected[1]);
        Assert.AreEqual(settings.SymbolFillCap(1), result.Plan.ActualCollected[1]);
        CollectionAssert.AreEqual(
            result.Plan.PlannedCollected.OrderBy(kv => kv.Key).ToArray(),
            result.Plan.ActualCollected.OrderBy(kv => kv.Key).ToArray());
        Assert.IsFalse(result.Plan.StartingBoard.Cast<Cell?>().Any(cell => cell == null || cell.IsFeat));
        Assert.IsFalse(result.Plan.FinalBoard.Cast<Cell?>().Any(cell => cell == null || cell.IsFeat));
    }

    [TestMethod]
    public void ForwardTicketPipelineExecutorRejectsInvalidFramePlanBeforeMutation()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int>(),
            NonWinTargets = new Dictionary<int, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var invalidFrames = new ForwardTurnFramePlan(
            totalTurns: 3,
            new[]
            {
                new ForwardTurnFrame(1, Shape(1, 1, 1, 1, 1), Array.Empty<ForwardFeatureIntent>(), new HashSet<int>()),
                new ForwardTurnFrame(2, Shape(1, 1, 1, 1, 1), Array.Empty<ForwardFeatureIntent>(), new HashSet<int>()),
            });

        var result = new ForwardTicketPipelineExecutor(settings, seed: 93).Execute(
            objectives,
            invalidFrames);

        Assert.AreEqual(ForwardTicketPipelineStatus.FramePlanInvalid, result.Status);
        StringAssert.Contains(result.Detail, "frame count=2");
    }

    [TestMethod]
    public void ForwardTicketPipelineExecutorAuditRejectsActualCollectionMismatch()
    {
        var settings = Settings.Default;

        var detail = new ForwardTicketPipelineExecutor(settings, seed: 94).AuditActualCollections(
            new Dictionary<int, int> { [1] = 19, [2] = 3 },
            new Dictionary<int, int> { [1] = 20, [2] = 3 },
            maxSymbol: settings.PrizeLadderRows.Count);

        Assert.IsNotNull(detail);
        StringAssert.Contains(detail!, "symbol 1");
        StringAssert.Contains(detail!, "actual collected=19");
        StringAssert.Contains(detail!, "planned collected=20");
    }

    [TestMethod]
    public void ForwardTicketEnvelopeValidatorAcceptsFeasibleBaseFramePlan()
    {
        var settings = SettingsWithNoOptionalFeaturesAndNoNearMiss();
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            NonWinTargets = new Dictionary<int, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var budget = new ForwardFeatureBudget(
            settings.BASE_SPINS,
            settings.BASE_SPINS,
            wheelCount: 0,
            flushCount: 0,
            extraGoCount: 0,
            requiredPrizeUpgradeCount: 0,
            optionalPrizeUpgradeCount: 0,
            hasOptionalFeatures: false);
        var intentPlan = new ForwardFeatureIntentPlan(
            settings.BASE_SPINS,
            Array.Empty<ForwardFeatureIntent>(),
            new Dictionary<int, int>(),
            new Dictionary<int, int>());
        var frames = new ForwardTurnFramePlanner(settings, seed: 95).Plan(budget, intentPlan);
        Assert.AreEqual(ForwardTurnFrameStatus.Valid, frames.Status, frames.Detail);

        var result = new ForwardTicketEnvelopeValidator(settings).Validate(objectives, frames.Plan);

        Assert.AreEqual(ForwardTicketEnvelopeStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(settings.SymbolFillCap(1), result.RequiredCollections);
        Assert.IsTrue(result.AvailableCollectionSlots >= result.RequiredCollections);
        Assert.AreEqual(0, result.ExtraGoCount);
    }

    [TestMethod]
    public void ForwardTicketEnvelopeValidatorRejectsFeatureOnFinalTurn()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int>(),
            NonWinTargets = new Dictionary<int, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var frames = new ForwardTurnFramePlan(
            settings.BASE_SPINS,
            Enumerable.Range(1, settings.BASE_SPINS)
                .Select(turn => new ForwardTurnFrame(
                    turn,
                    Shape(1, 1, 1, 1, 1),
                    turn == settings.BASE_SPINS
                        ? new[] { ForwardFeatureIntent.Wheel(turn, symbol: 2, stackValue: 1) }
                        : Array.Empty<ForwardFeatureIntent>(),
                    new HashSet<int>()))
                .ToArray());

        var result = new ForwardTicketEnvelopeValidator(settings).Validate(objectives, frames);

        Assert.AreEqual(ForwardTicketEnvelopeStatus.FeatureOnFinalTurn, result.Status);
    }

    [TestMethod]
    public void ForwardTicketEnvelopeValidatorRejectsUnearnedExtraTurnPlan()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int>(),
            NonWinTargets = new Dictionary<int, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var frames = new ForwardTurnFramePlan(
            settings.BASE_SPINS + 1,
            Enumerable.Range(1, settings.BASE_SPINS + 1)
                .Select(turn => new ForwardTurnFrame(
                    turn,
                    Shape(1, 1, 1, 1, 1),
                    Array.Empty<ForwardFeatureIntent>(),
                    new HashSet<int>()))
                .ToArray());

        var result = new ForwardTicketEnvelopeValidator(settings).Validate(objectives, frames);

        Assert.AreEqual(ForwardTicketEnvelopeStatus.InvalidTotalTurns, result.Status);
        StringAssert.Contains(result.Detail, "EXTRA_GO count=0");
    }

    [TestMethod]
    public void ForwardTicketEnvelopeValidatorRejectsInsufficientCollectionCapacity()
    {
        var settings = Settings.Default;
        var targets = settings.PrizeLadderRows
            .Select((row, index) => (Symbol: index + 1, row.Target))
            .ToDictionary(item => item.Symbol, item => item.Target);
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = targets,
            NonWinTargets = new Dictionary<int, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var frames = new ForwardTurnFramePlan(
            settings.BASE_SPINS,
            Enumerable.Range(1, settings.BASE_SPINS)
                .Select(turn => new ForwardTurnFrame(
                    turn,
                    Shape(1, 1, 1, 1, 1),
                    Array.Empty<ForwardFeatureIntent>(),
                    new HashSet<int>()))
                .ToArray());

        var result = new ForwardTicketEnvelopeValidator(settings).Validate(objectives, frames);

        Assert.AreEqual(ForwardTicketEnvelopeStatus.InsufficientCollectionCapacity, result.Status);
        Assert.IsTrue(result.RequiredCollections > result.AvailableCollectionSlots);
    }

    [TestMethod]
    public void ForwardTicketBuilderBuildsValidatedPipelineFromPrizeAmounts()
    {
        var settings = SettingsWithNoOptionalFeaturesAndNoNearMiss();

        var result = new ForwardTicketBuilder(settings, seed: 101).Build(new decimal[] { 1m });

        Assert.AreEqual(ForwardTicketBuildStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(ForwardTicketEnvelopeStatus.Valid, result.Envelope!.Status);
        Assert.AreEqual(1m, result.MathInput!.Bundle!.Covered.Single());
        Assert.AreEqual(1, result.Objectives!.Objectives!.WinSymbols.Single());
        Assert.AreEqual(settings.SymbolFillCap(1), result.Pipeline!.Plan!.PlannedCollected[1]);
        Assert.AreEqual(settings.SymbolFillCap(1), result.Pipeline.Plan.ActualCollected[1]);
        CollectionAssert.AreEqual(
            result.Pipeline.Plan.PlannedCollected.OrderBy(kv => kv.Key).ToArray(),
            result.Pipeline.Plan.ActualCollected.OrderBy(kv => kv.Key).ToArray());
    }

    [TestMethod]
    public void ForwardTicketBuilderReportsMathInputFailureWithoutLaterStages()
    {
        var settings = Settings.Default;

        var result = new ForwardTicketBuilder(settings, seed: 102).Build(null);

        Assert.AreEqual(ForwardTicketBuildStatus.MathInputFailed, result.Status);
        Assert.AreEqual(ForwardMathInputStatus.MissingPrizeAmounts, result.MathInput!.Status);
        Assert.IsNull(result.Objectives);
        Assert.IsNull(result.Pipeline);
    }

    [TestMethod]
    public void ForwardGamePlanAdapterCreatesVerifiedSerializerCompatiblePlan()
    {
        var settings = SettingsWithNoOptionalFeaturesAndNoNearMiss();
        var build = new ForwardTicketBuilder(settings, seed: 111).Build(new decimal[] { 1m });
        Assert.AreEqual(ForwardTicketBuildStatus.Valid, build.Status, build.Detail);

        var result = new ForwardGamePlanAdapter(settings).Adapt(build);

        Assert.AreEqual(ForwardGamePlanAdapterStatus.Valid, result.Status, result.Detail);
        Assert.IsTrue(result.Plan!.Verified);
        Assert.AreEqual(build.Pipeline!.Plan!.Turns.Count, result.Plan.Spins.Count);
        Assert.AreEqual(build.Pipeline.Plan.ActualCollected[1], Sim.Run(result.Plan)[1]);

        var ticket = TicketSerializer.ToTicketObject(result.Plan, settings);
        var report = TicketChecker.CheckTicket(ticket);
        Assert.IsTrue(report.IsValid, string.Join(Environment.NewLine,
            report.Checks
                .Where(check => check.Result == TicketChecker.Status.Fail)
                .Select(check => $"{check.Category}/{check.Name}: {check.Detail}")));
        Assert.AreEqual(result.Plan.TotalSpins, ticket.WinInfo.TotalSpins);
        Assert.AreEqual(result.Plan.Spins.Count, ticket.Turns.Length);
    }

    [TestMethod]
    public void ForwardGamePlanAdapterRejectsInvalidBuildResult()
    {
        var settings = Settings.Default;
        var build = new ForwardTicketBuilder(settings, seed: 112).Build(null);

        var result = new ForwardGamePlanAdapter(settings).Adapt(build);

        Assert.AreEqual(ForwardGamePlanAdapterStatus.SourceBuildInvalid, result.Status);
        Assert.IsNull(result.Plan);
    }

    [TestMethod]
    public void ForwardTicketGeneratorReturnsTicketJsonFromPrizeAmounts()
    {
        var settings = SettingsWithNoOptionalFeaturesAndNoNearMiss();

        var result = new ForwardTicketGenerator(settings, seed: 121).Generate(new decimal[] { 1m });

        Assert.AreEqual(ForwardTicketGenerationStatus.Valid, result.Status, result.Detail);
        Assert.IsNotNull(result.Ticket);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Json));
        Assert.AreEqual(result.Ticket!.WinInfo.TotalSpins, result.Ticket.Turns.Length);
        Assert.AreEqual(settings.SymbolFillCap(1), result.Ticket.WinInfo.WinSymbols.Single().Target);

        var reparsed = JsonConvert.DeserializeObject<TicketSerializer.TicketDto>(result.Json!);
        var report = TicketChecker.CheckTicket(reparsed);
        Assert.IsTrue(report.IsValid, string.Join(Environment.NewLine,
            report.Checks
                .Where(check => check.Result == TicketChecker.Status.Fail)
                .Select(check => $"{check.Category}/{check.Name}: {check.Detail}")));
    }

    [TestMethod]
    public void ForwardTicketGeneratorStopsWhenBuildFails()
    {
        var settings = Settings.Default;

        var result = new ForwardTicketGenerator(settings, seed: 122).Generate(null);

        Assert.AreEqual(ForwardTicketGenerationStatus.BuildFailed, result.Status);
        Assert.AreEqual(ForwardTicketBuildStatus.MathInputFailed, result.Build!.Status);
        Assert.IsNull(result.AdaptedPlan);
        Assert.IsNull(result.Ticket);
        Assert.IsNull(result.Json);
    }

    [TestMethod]
    public void CoinPusherTicketGeneratorPublicFacadeReturnsTicketForPluginValidation()
    {
        var settings = SettingsWithNoOptionalFeaturesAndNoNearMiss();

        var result = new CoinPusherTicketGenerator(settings).Generate(new decimal[] { 1m }, seed: 131);

        Assert.AreEqual(CoinPusherTicketGenerationStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(131, result.Seed);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Json));
        Assert.IsNotNull(result.Ticket);
        Assert.IsNotNull(result.Plan);
        Assert.IsTrue(result.Plan!.Verified);

        var reparsed = JsonConvert.DeserializeObject<TicketSerializer.TicketDto>(result.Json!);
        var report = TicketChecker.CheckTicket(reparsed);
        Assert.IsTrue(report.IsValid, string.Join(Environment.NewLine,
            report.Checks
                .Where(check => check.Result == TicketChecker.Status.Fail)
                .Select(check => $"{check.Category}/{check.Name}: {check.Detail}")));
    }

    [TestMethod]
    public void CoinPusherTicketGeneratorIsDeterministicForSameSeed()
    {
        var settings = SettingsWithNoOptionalFeaturesAndNoNearMiss();
        var generator = new CoinPusherTicketGenerator(settings);

        var first = generator.Generate(new decimal[] { 1m }, seed: 132);
        var second = generator.Generate(new decimal[] { 1m }, seed: 132);

        Assert.AreEqual(CoinPusherTicketGenerationStatus.Valid, first.Status, first.Detail);
        Assert.AreEqual(CoinPusherTicketGenerationStatus.Valid, second.Status, second.Detail);
        Assert.AreEqual(first.Json, second.Json);
    }

    [TestMethod]
    public void CoinPusherTicketGeneratorRejectsNullPrizeListWithoutThrowing()
    {
        var result = new CoinPusherTicketGenerator(Settings.Default).Generate(null, seed: 133);

        Assert.AreEqual(CoinPusherTicketGenerationStatus.InvalidRequest, result.Status);
        Assert.AreEqual(133, result.Seed);
        Assert.IsNull(result.Json);
        Assert.IsNull(result.Ticket);
        Assert.IsNull(result.Plan);
    }

    [TestMethod]
    public void CoinPusherTicketGenerationRequestValidatorAcceptsWinAndNoWinRequests()
    {
        var validator = new CoinPusherTicketGenerationRequestValidator(Settings.Default);

        Assert.AreEqual(
            CoinPusherTicketGenerationRequestStatus.Valid,
            validator.Validate(Array.Empty<decimal>()).Status);
        Assert.AreEqual(
            CoinPusherTicketGenerationRequestStatus.Valid,
            validator.Validate(new decimal[] { 1m, 2m, 5m }).Status);
    }

    [TestMethod]
    public void CoinPusherTicketGenerationRequestValidatorRejectsZeroPrizeAmount()
    {
        var result = new CoinPusherTicketGenerationRequestValidator(Settings.Default)
            .Validate(new decimal[] { 0m });

        Assert.AreEqual(CoinPusherTicketGenerationRequestStatus.ZeroPrizeAmount, result.Status);
        StringAssert.Contains(result.Detail, "empty prize list");
    }

    [TestMethod]
    public void CoinPusherTicketGeneratorRejectsInvalidRequestBeforePlanning()
    {
        var result = new CoinPusherTicketGenerator(Settings.Default).Generate(new decimal[] { 0m }, seed: 134);

        Assert.AreEqual(CoinPusherTicketGenerationStatus.InvalidRequest, result.Status);
        StringAssert.Contains(result.Detail, nameof(CoinPusherTicketGenerationRequestStatus.ZeroPrizeAmount));
        Assert.IsNull(result.Json);
        Assert.IsNull(result.Ticket);
        Assert.IsNull(result.Plan);
        CollectionAssert.AreEqual(new decimal[] { 0m }, result.RequestedPrizeAmounts.ToArray());
    }

    [TestMethod]
    public void CoinPusherTicketGenerationRequestValidatorRejectsUnsafeFeatureIds()
    {
        var settings = new Settings { F_WHEEL = Settings.Default.F_COIN };

        var result = new CoinPusherTicketGenerationRequestValidator(settings)
            .Validate(Array.Empty<decimal>());

        Assert.AreEqual(CoinPusherTicketGenerationRequestStatus.InvalidFeatureIds, result.Status);
    }

    [TestMethod]
    public void CoinPusherTicketGenerationValidatorAcceptsCompleteGeneratedResult()
    {
        var settings = SettingsWithNoOptionalFeaturesAndNoNearMiss();
        var generated = new ForwardTicketGenerator(settings, seed: 141).Generate(new decimal[] { 1m });
        Assert.AreEqual(ForwardTicketGenerationStatus.Valid, generated.Status, generated.Detail);

        var result = new CoinPusherTicketGenerationValidator().Validate(new decimal[] { 1m }, generated);

        Assert.AreEqual(CoinPusherTicketGenerationValidationStatus.Valid, result.Status, result.Detail);
    }

    [TestMethod]
    public void CoinPusherTicketGenerationValidatorRejectsPrizeCoverageMismatch()
    {
        var settings = SettingsWithNoOptionalFeaturesAndNoNearMiss();
        var generated = new ForwardTicketGenerator(settings, seed: 142).Generate(new decimal[] { 1m });
        Assert.AreEqual(ForwardTicketGenerationStatus.Valid, generated.Status, generated.Detail);

        var result = new CoinPusherTicketGenerationValidator().Validate(new decimal[] { 2m }, generated);

        Assert.AreEqual(CoinPusherTicketGenerationValidationStatus.PrizeCoverageMismatch, result.Status);
        StringAssert.Contains(result.Detail, "requested=[2]");
        StringAssert.Contains(result.Detail, "covered=[1]");
    }

    [TestMethod]
    public void CoinPusherTicketGenerationAuditorSummarizesValidTicket()
    {
        var settings = SettingsWithNoOptionalFeaturesAndNoNearMiss();
        var generation = new CoinPusherTicketGenerator(settings).Generate(new decimal[] { 1m }, seed: 151);
        Assert.AreEqual(CoinPusherTicketGenerationStatus.Valid, generation.Status, generation.Detail);

        var result = new CoinPusherTicketGenerationAuditor(settings).Audit(generation);

        Assert.AreEqual(CoinPusherTicketGenerationAuditStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(151, result.Audit!.Seed);
        Assert.AreEqual(generation.Ticket!.WinInfo.TotalSpins, result.Audit.TotalSpins);
        Assert.AreEqual(generation.Ticket.WinInfo.WinSymbols.Length, result.Audit.WinSymbolCount);
        Assert.AreEqual(generation.Ticket.WinInfo.NonWinSymbols.Length, result.Audit.NonWinSymbolCount);
        CollectionAssert.AreEqual(new decimal[] { 1m }, result.Audit.RequestedPrizeAmounts.ToArray());
        CollectionAssert.AreEqual(new decimal[] { 1m }, result.Audit.CoveredPrizeAmounts.ToArray());
        Assert.AreEqual(0, result.Audit.SkippedPrizeAmounts.Count);
    }

    [TestMethod]
    public void CoinPusherTicketGenerationAuditorRejectsInvalidGenerationResult()
    {
        var generation = new CoinPusherTicketGenerator(Settings.Default).Generate(null, seed: 152);

        var result = new CoinPusherTicketGenerationAuditor(Settings.Default).Audit(generation);

        Assert.AreEqual(CoinPusherTicketGenerationAuditStatus.GenerationNotValid, result.Status);
        Assert.IsNull(result.Audit);
    }

    [TestMethod]
    public void CoinPusherTicketGenerationGuardReturnsAuditedGenerationMetadata()
    {
        var settings = SettingsWithNoOptionalFeaturesAndNoNearMiss();

        var result = new CoinPusherTicketGenerationGuard(settings).Generate(new decimal[] { 1m }, seed: 161);

        Assert.AreEqual(CoinPusherTicketGenerationGuardStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(161, result.Seed);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Json));
        Assert.IsNotNull(result.Ticket);
        Assert.IsNotNull(result.Plan);
        Assert.IsNotNull(result.Summary);
        Assert.AreEqual(result.Ticket!.WinInfo.TotalSpins, result.Summary!.TotalSpins);
        Assert.AreEqual(1, result.Summary.CoveredPrizeAmounts.Count);
        Assert.AreEqual(1m, result.Summary.CoveredPrizeAmounts[0]);
    }

    [TestMethod]
    public void CoinPusherTicketGenerationGuardDoesNotExposeTicketWhenGenerationFails()
    {
        var result = new CoinPusherTicketGenerationGuard(Settings.Default).Generate(null, seed: 162);

        Assert.AreEqual(CoinPusherTicketGenerationGuardStatus.GenerationRejected, result.Status);
        Assert.IsFalse(result.IsValid);
        Assert.IsNull(result.Json);
        Assert.IsNull(result.Ticket);
        Assert.IsNull(result.Plan);
        Assert.IsNull(result.Summary);
        Assert.AreEqual(CoinPusherTicketGenerationStatus.InvalidRequest, result.Generation.Status);
    }

    [TestMethod]
    public void CoinPusherTicketJsonGeneratorReturnsJsonForPluginValidation()
    {
        var settings = SettingsWithNoOptionalFeaturesAndNoNearMiss();

        var result = new CoinPusherTicketJsonGenerator(settings).Generate(new decimal[] { 1m }, seed: 171);

        Assert.AreEqual(CoinPusherTicketJsonGenerationStatus.Valid, result.Status, result.Detail);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Json));
        Assert.IsNotNull(result.Ticket);
        Assert.IsNotNull(result.Summary);

        var reparsed = JsonConvert.DeserializeObject<TicketSerializer.TicketDto>(result.Json!);
        var report = TicketChecker.CheckTicket(reparsed);
        Assert.IsTrue(report.IsValid, string.Join(Environment.NewLine,
            report.Checks
                .Where(check => check.Result == TicketChecker.Status.Fail)
                .Select(check => $"{check.Category}/{check.Name}: {check.Detail}")));
    }

    [TestMethod]
    public void CoinPusherTicketJsonGeneratorDoesNotExposeJsonWhenGuardRejects()
    {
        var generator = new CoinPusherTicketJsonGenerator(Settings.Default);

        var result = generator.Generate(new decimal[] { 0m }, seed: 172);

        Assert.AreEqual(CoinPusherTicketJsonGenerationStatus.GuardRejected, result.Status);
        Assert.IsNull(result.Json);
        Assert.IsNull(result.Ticket);
        Assert.IsNull(result.Summary);
        Assert.ThrowsException<InvalidOperationException>(() => generator.GenerateJson(new decimal[] { 0m }, seed: 172));
    }

    [TestMethod]
    public void ForwardObjectiveFinalizerBalancesNearMissTargetsToFrameCapacity()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            NonWinTargets = new Dictionary<int, int>
            {
                [2] = settings.SymbolFillCap(2) - 1,
                [3] = settings.SymbolFillCap(3) - 1,
                [4] = settings.SymbolFillCap(4) - 1,
                [5] = settings.SymbolFillCap(5) - 1,
                [6] = settings.SymbolFillCap(6) - 1,
            },
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var frames = Enumerable.Range(1, settings.BASE_SPINS)
            .Select(turn => new ForwardTurnFrame(
                turn,
                Shape(1, 2, 3, 4, 1),
                Array.Empty<ForwardFeatureIntent>(),
                new HashSet<int>()))
            .ToArray();
        var framePlan = new ForwardTurnFramePlan(settings.BASE_SPINS, frames);
        var intentPlan = new ForwardFeatureIntentPlan(
            settings.BASE_SPINS,
            Array.Empty<ForwardFeatureIntent>(),
            new Dictionary<int, int>(),
            new Dictionary<int, int>());

        var result = new ForwardObjectiveFinalizer(settings).Finalize(objectives, intentPlan, framePlan);

        Assert.AreEqual(ForwardObjectiveFinalizationStatus.Valid, result.Status, result.Detail);
        CollectionAssert.AreEqual(
            objectives.WinTargets.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}").ToArray(),
            result.Objectives!.WinTargets.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}").ToArray());
        Assert.IsTrue(result.Objectives!.NearMissTargets.Values.Sum() < objectives.NearMissTargets.Values.Sum());
        Assert.IsTrue(result.Objectives.NearMissTargets.Count > 0);
        Assert.IsTrue(result.Objectives.NearMissTargets.Values.All(target => target >= settings.NONWIN_MIN_TARGET));
        Assert.AreEqual(
            ForwardTicketEnvelopeStatus.Valid,
            new ForwardTicketEnvelopeValidator(settings).Validate(result.Objectives, framePlan).Status);
    }

    [TestMethod]
    public void ForwardObjectiveFinalizerKeepsNearMissTargetsWhenCapacityFits()
    {
        var settings = new Settings { NONWIN_MIN_TARGET = 1 };
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int>(),
            NonWinTargets = new Dictionary<int, int> { [1] = 2 },
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var frames = Enumerable.Range(1, settings.BASE_SPINS)
            .Select(turn => new ForwardTurnFrame(
                turn,
                Shape(1, 2, 3, 4, 1),
                Array.Empty<ForwardFeatureIntent>(),
                new HashSet<int>()))
            .ToArray();
        var framePlan = new ForwardTurnFramePlan(settings.BASE_SPINS, frames);
        var intentPlan = new ForwardFeatureIntentPlan(
            settings.BASE_SPINS,
            Array.Empty<ForwardFeatureIntent>(),
            new Dictionary<int, int>(),
            new Dictionary<int, int>());

        var result = new ForwardObjectiveFinalizer(settings).Finalize(objectives, intentPlan, framePlan);

        Assert.AreEqual(ForwardObjectiveFinalizationStatus.Valid, result.Status, result.Detail);
        CollectionAssert.AreEqual(
            objectives.NearMissTargets.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}").ToArray(),
            result.Objectives!.NearMissTargets.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}").ToArray());
    }

    [TestMethod]
    public void CoinPusherTicketJsonGeneratorHandlesDefaultSeedNearMissCapacity()
    {
        var result = new CoinPusherTicketJsonGenerator(Settings.Default).Generate(new decimal[] { 1m }, seed: 181);

        Assert.AreEqual(CoinPusherTicketJsonGenerationStatus.Valid, result.Status, result.Detail);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Json));
        Assert.IsNotNull(result.Summary);
        Assert.IsTrue(result.Plan!.NonWinTargets.Values.All(target => target >= Settings.Default.NONWIN_MIN_TARGET));
    }

    [TestMethod]
    public void CoinPusherTicketJsonGeneratorHandlesSixSymbolCapacityWithoutSafeFiller()
    {
        var result = new CoinPusherTicketJsonGenerator(Settings.Default).Generate(
            new decimal[] { 1m, 2m, 5m, 10m, 100m, 10000m },
            seed: 1006295523);

        Assert.AreEqual(CoinPusherTicketJsonGenerationStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(Settings.Default.MAX_SPINS, result.Plan!.TotalSpins);
        Assert.AreEqual(6, result.Ticket!.WinInfo.WinSymbols.Length);
    }

    [TestMethod]
    public void ForwardTurnRecorderPreservesPushersSpawnsAndFeatureCells()
    {
        var settings = Settings.Default;
        var shape = Shape(5, 1, 2, 3, 4);
        var frame = new ForwardTurnFrame(
            turn: 2,
            shape,
            new[] { ForwardFeatureIntent.Wheel(turn: 2, symbol: 2, stackValue: 1) },
            new HashSet<int> { 0 });
        var wheel = Grid.Feat(
            settings.F_WHEEL,
            2,
            new FP { FeatId = "WHEEL", WheelSym = 2, WheelStack = 2 });
        var realization = new ForwardTurnRealizationResult(
            ForwardTurnRealizationStatus.Valid,
            "ok",
            new[]
            {
                new ForwardSpawn(4, 4, Grid.Norm(3)),
                new ForwardSpawn(0, 0, wheel),
            },
            new Dictionary<int, int> { [2] = 1 },
            new[] { new ForwardWheelImpact(2, 2, 2) },
            normalIntentResult: null,
            flushCount: 1);

        var result = new ForwardTurnRecorder(settings).Record(frame, realization);

        Assert.AreEqual(ForwardTurnRecordStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(2, result.Turn!.Turn);
        Assert.AreEqual(settings.COLS, result.Turn.Pushers.Count);
        Assert.AreEqual(settings.ROWS, result.Turn.Pushers[0].PushValue);
        Assert.AreEqual(settings.F_FLUSH_ID, result.Turn.Pushers[0].FeatureId);
        Assert.AreEqual(1, result.Turn.Pushers[1].PushValue);
        Assert.IsNull(result.Turn.Pushers[1].FeatureId);
        Assert.AreEqual(2, result.Turn.Spawns.Count);
        Assert.AreEqual((0, 0), (result.Turn.Spawns[0].Row, result.Turn.Spawns[0].Col));
        Assert.AreEqual(settings.F_WHEEL, result.Turn.Spawns[0].Cell.Sym);
        Assert.AreEqual((4, 4), (result.Turn.Spawns[1].Row, result.Turn.Spawns[1].Col));
        Assert.AreEqual(1, result.Turn.FlushCount);
        Assert.AreEqual(1, result.Turn.Collected[2]);
        Assert.AreEqual(1, result.Turn.WheelImpacts.Count);
    }

    [TestMethod]
    public void ForwardTurnRecorderRejectsInvalidRealizationAndDuplicateSpawns()
    {
        var settings = Settings.Default;
        var frame = new ForwardTurnFrame(
            turn: 1,
            Shape(1, 1, 1, 1, 1),
            Array.Empty<ForwardFeatureIntent>(),
            new HashSet<int>());
        var invalid = new ForwardTurnRealizationResult(
            ForwardTurnRealizationStatus.NormalSpawnInvalid,
            "bad normal spawn",
            Array.Empty<ForwardSpawn>(),
            new Dictionary<int, int>(),
            Array.Empty<ForwardWheelImpact>(),
            normalIntentResult: null,
            flushCount: 0);

        var invalidResult = new ForwardTurnRecorder(settings).Record(frame, invalid);

        Assert.AreEqual(ForwardTurnRecordStatus.InvalidRealization, invalidResult.Status);

        var duplicate = new ForwardTurnRealizationResult(
            ForwardTurnRealizationStatus.Valid,
            "ok",
            new[]
            {
                new ForwardSpawn(0, 0, Grid.Norm(1)),
                new ForwardSpawn(0, 0, Grid.Norm(2)),
            },
            new Dictionary<int, int>(),
            Array.Empty<ForwardWheelImpact>(),
            normalIntentResult: null,
            flushCount: 0);

        var duplicateResult = new ForwardTurnRecorder(settings).Record(frame, duplicate);

        Assert.AreEqual(ForwardTurnRecordStatus.DuplicateSpawnPosition, duplicateResult.Status);
    }

    [TestMethod]
    public void ForwardFeatureIntentPlannerBuildsSemanticPayloadsWithoutConvertGuessing()
    {
        var settings = new Settings
        {
            PWheelStackValue1 = 0.0,
            PWheelStackValue2 = 1.0,
        };
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            PrizeTiers = new Dictionary<int, int> { [1] = 2 },
            NonWinTargets = new Dictionary<int, int> { [2] = settings.NONWIN_MIN_TARGET },
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var timing = new ForwardFeatureTiming(
            totalTurns: 6,
            new[]
            {
                new ForwardTimedFeature(ForwardTimedFeatureKind.Wheel, 2),
                new ForwardTimedFeature(ForwardTimedFeatureKind.Flush, 3),
                new ForwardTimedFeature(ForwardTimedFeatureKind.ExtraGo, 4),
                new ForwardTimedFeature(ForwardTimedFeatureKind.PrizeUpgrade, 4),
                new ForwardTimedFeature(ForwardTimedFeatureKind.PrizeUpgrade, 5),
            });

        var result = new ForwardFeatureIntentPlanner(settings, seed: 43).Plan(objectives, timing);

        Assert.AreEqual(ForwardFeatureIntentStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(5, result.Plan!.Intents.Count);
        var wheel = result.Plan.Intents.Single(intent => intent.Kind == ForwardTimedFeatureKind.Wheel);
        Assert.IsTrue(wheel.WheelSymbol >= 1 && wheel.WheelSymbol <= settings.PrizeLadderRows.Count);
        Assert.AreEqual(2, wheel.WheelStackValue);
        Assert.AreEqual(3, wheel.ResultingWheelStack);
        Assert.AreEqual(1, result.Plan.Intents.Count(intent => intent.Kind == ForwardTimedFeatureKind.Flush));
        Assert.AreEqual(1, result.Plan.Intents.Count(intent => intent.Kind == ForwardTimedFeatureKind.ExtraGo));

        var upgrades = result.Plan.Intents
            .Where(intent => intent.Kind == ForwardTimedFeatureKind.PrizeUpgrade)
            .OrderBy(intent => intent.Turn)
            .ToArray();
        CollectionAssert.AreEqual(new[] { 1, 2 }, upgrades.Select(intent => intent.UpgradeTier!.Value).ToArray());
        Assert.IsTrue(upgrades.All(intent => intent.UpgradeSymbol == 1));
        CollectionAssert.AreEqual(new decimal[] { 11m, 12m }, upgrades.Select(intent => intent.UpgradePrizeValue!.Value).ToArray());
        Assert.AreEqual(2, result.Plan.EffectivePrizeTiers[1]);
    }

    [TestMethod]
    public void ForwardFeatureIntentPlannerBlocksSameTurnDuplicatePrizeUpgradeSymbol()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            PrizeTiers = new Dictionary<int, int> { [1] = 2 },
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var timing = new ForwardFeatureTiming(
            totalTurns: 5,
            new[]
            {
                new ForwardTimedFeature(ForwardTimedFeatureKind.PrizeUpgrade, 2),
                new ForwardTimedFeature(ForwardTimedFeatureKind.PrizeUpgrade, 2),
            });

        var result = new ForwardFeatureIntentPlanner(settings, seed: 44).Plan(objectives, timing);

        Assert.AreEqual(ForwardFeatureIntentStatus.PrizeUpgradeNoEligibleSymbol, result.Status);
    }

    [TestMethod]
    public void ForwardFeatureIntentPlannerAllowsSameTurnPrizeUpgradesForDifferentSymbols()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            PrizeTiers = new Dictionary<int, int> { [1] = 1 },
            NonWinTargets = new Dictionary<int, int> { [2] = settings.NONWIN_MIN_TARGET },
            NonWinPrizeTiers = new Dictionary<int, int> { [2] = 1 },
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var timing = new ForwardFeatureTiming(
            totalTurns: 5,
            new[]
            {
                new ForwardTimedFeature(ForwardTimedFeatureKind.PrizeUpgrade, 3),
                new ForwardTimedFeature(ForwardTimedFeatureKind.PrizeUpgrade, 3),
            });

        var result = new ForwardFeatureIntentPlanner(settings, seed: 45).Plan(objectives, timing);

        Assert.AreEqual(ForwardFeatureIntentStatus.Valid, result.Status, result.Detail);
        var symbols = result.Plan!.Intents
            .Where(intent => intent.Kind == ForwardTimedFeatureKind.PrizeUpgrade)
            .Select(intent => intent.UpgradeSymbol!.Value)
            .OrderBy(symbol => symbol)
            .ToArray();
        CollectionAssert.AreEqual(new[] { 1, 2 }, symbols);
    }

    [TestMethod]
    public void ForwardFeatureIntentPlannerAllocatesOptionalPrizeUpgradeToNearMiss()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int>(),
            NonWinTargets = new Dictionary<int, int> { [2] = settings.NONWIN_MIN_TARGET },
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var timing = new ForwardFeatureTiming(
            totalTurns: 5,
            new[] { new ForwardTimedFeature(ForwardTimedFeatureKind.PrizeUpgrade, 3) });

        var result = new ForwardFeatureIntentPlanner(settings, seed: 46).Plan(objectives, timing);

        Assert.AreEqual(ForwardFeatureIntentStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(1, result.Plan!.EffectiveNonWinPrizeTiers[2]);
        var upgrade = result.Plan.Intents.Single();
        Assert.AreEqual(2, upgrade.UpgradeSymbol);
        Assert.AreEqual(1, upgrade.UpgradeTier);
        Assert.AreEqual(21m, upgrade.UpgradePrizeValue);
    }

    [TestMethod]
    public void ForwardFeatureIntentPlannerRejectsOptionalPrizeUpgradeWithoutNearMiss()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            NonWinTargets = new Dictionary<int, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var timing = new ForwardFeatureTiming(
            totalTurns: 5,
            new[] { new ForwardTimedFeature(ForwardTimedFeatureKind.PrizeUpgrade, 3) });

        var result = new ForwardFeatureIntentPlanner(settings, seed: 47).Plan(objectives, timing);

        Assert.AreEqual(ForwardFeatureIntentStatus.OptionalPrizeUpgradeNoTarget, result.Status);
    }

    [TestMethod]
    public void ForwardFeaturePlacementAdapterPlacesFeaturesWithSafePreferredConvert()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int> { [2] = settings.SymbolFillCap(2) },
            NonWinTargets = new Dictionary<int, int> { [1] = settings.NONWIN_MIN_TARGET },
            PrizeTiers = new Dictionary<int, int> { [2] = 1 },
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var symbolLedger = new SymbolLedger(objectives.WinTargets, objectives.NearMissTargets, objectives.MaxSymbol, settings);
        var extraLedger = new ForwardExtraSpinLedger(plannedTotalTurns: 6, settings);
        var prizeLedger = new ForwardPrizeUpgradeLedger(objectives.PrizeTiers, objectives.PrizeValues, objectives.MaxSymbol, settings);
        var intents = new[]
        {
            ForwardFeatureIntent.Wheel(turn: 1, symbol: 2, stackValue: 2),
            ForwardFeatureIntent.PrizeUpgrade(turn: 1, symbol: 2, tier: 1, prizeValue: 21m),
            ForwardFeatureIntent.ExtraGo(turn: 1),
            ForwardFeatureIntent.Flush(turn: 1),
        };

        var result = new ForwardFeaturePlacementAdapter(settings, objectives.MaxSymbol, seed: 48).Plan(
            turn: 1,
            plannedTotalTurns: 6,
            objectives,
            intents,
            emptyPositions: Positions((4, 0), (4, 1), (4, 2)),
            reservedPositions: Array.Empty<(int r, int c)>(),
            futureTurns: new[] { new ForwardFutureTurn(2, Shape(1, 1, 1, 1, 1)) },
            remainingFeatureCapacity: FeatureCapacity(
                (ForwardFeatureKind.Wheel, 1),
                (ForwardFeatureKind.PrizeUpgrade, 1),
                (ForwardFeatureKind.ExtraGo, 1)),
            symbolLedger,
            extraLedger,
            prizeLedger);

        Assert.AreEqual(ForwardFeaturePlacementStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(3, result.Spawns.Count);
        Assert.AreEqual(1, result.FlushCount);
        Assert.IsTrue(result.Spawns.Any(spawn => spawn.Cell.Sym == settings.F_WHEEL && spawn.Cell.CvtSym == 2));
        Assert.IsTrue(result.Spawns.Any(spawn => spawn.Cell.Sym == settings.F_PRUP && spawn.Cell.Fp!.PrupSym == 2));
        Assert.IsTrue(result.Spawns.Any(spawn => spawn.Cell.Sym == settings.F_XSPIN));
        Assert.AreEqual(1, result.WheelImpacts.Count);
        Assert.AreEqual(3, result.WheelImpacts[0].StackValue);
        Assert.AreEqual(2, symbolLedger.CollectedCount(2));
        Assert.AreEqual(settings.BASE_SPINS + 1, extraLedger.EarnedTurns);
        Assert.AreEqual(1, extraLedger.LogicalExtraGoAwards);
        Assert.AreEqual(1, prizeLedger.CurrentTier(2));
    }

    [TestMethod]
    public void ForwardFeaturePlacementAdapterFallsBackWhenPreferredConvertWouldOverCollect()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int> { [2] = settings.SymbolFillCap(2) },
            NonWinTargets = new Dictionary<int, int> { [1] = settings.NONWIN_MIN_TARGET },
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var symbolLedger = new SymbolLedger(objectives.WinTargets, objectives.NearMissTargets, objectives.MaxSymbol, settings);
        for (var i = 0; i < settings.SymbolFillCap(2); i++)
            Assert.AreEqual(SymbolCollectionStatus.Valid, symbolLedger.Collect(2).Status);

        var result = new ForwardFeaturePlacementAdapter(settings, objectives.MaxSymbol, seed: 49).Plan(
            turn: 1,
            plannedTotalTurns: 5,
            objectives,
            new[] { ForwardFeatureIntent.Wheel(turn: 1, symbol: 2, stackValue: 1) },
            emptyPositions: Positions((4, 0)),
            reservedPositions: Array.Empty<(int r, int c)>(),
            futureTurns: new[] { new ForwardFutureTurn(2, Shape(1, 1, 1, 1, 1)) },
            remainingFeatureCapacity: FeatureCapacity((ForwardFeatureKind.Wheel, 1)),
            symbolLedger,
            new ForwardExtraSpinLedger(plannedTotalTurns: 5, settings),
            EmptyPrizeLedger(objectives.MaxSymbol));

        Assert.AreEqual(ForwardFeaturePlacementStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(settings.SymbolFillCap(2), symbolLedger.CollectedCount(2));
        Assert.AreNotEqual(2, result.Requests.Single().ConvertToSymbol);
        Assert.AreEqual(1, symbolLedger.CollectedCount(result.Requests.Single().ConvertToSymbol));
    }

    [TestMethod]
    public void ForwardFeaturePlacementAdapterDoesNotMutateLedgersWhenConvertIsImpossible()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            NonWinTargets = new Dictionary<int, int>(),
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(1, tiers: 1),
            MaxSym = 1,
        });
        var symbolLedger = new SymbolLedger(objectives.WinTargets, objectives.NearMissTargets, objectives.MaxSymbol, settings);
        for (var i = 0; i < settings.SymbolFillCap(1); i++)
            Assert.AreEqual(SymbolCollectionStatus.Valid, symbolLedger.Collect(1).Status);
        var extraLedger = new ForwardExtraSpinLedger(plannedTotalTurns: 6, settings);
        var prizeLedger = EmptyPrizeLedger(objectives.MaxSymbol);

        var result = new ForwardFeaturePlacementAdapter(settings, objectives.MaxSymbol, seed: 50).Plan(
            turn: 1,
            plannedTotalTurns: 6,
            objectives,
            new[] { ForwardFeatureIntent.ExtraGo(turn: 1) },
            emptyPositions: Positions((4, 0)),
            reservedPositions: Array.Empty<(int r, int c)>(),
            futureTurns: new[] { new ForwardFutureTurn(2, Shape(1, 1, 1, 1, 1)) },
            remainingFeatureCapacity: FeatureCapacity((ForwardFeatureKind.ExtraGo, 1)),
            symbolLedger,
            extraLedger,
            prizeLedger);

        Assert.AreEqual(ForwardFeaturePlacementStatus.NoSafeConvertSymbol, result.Status);
        Assert.AreEqual(settings.SymbolFillCap(1), symbolLedger.CollectedCount(1));
        Assert.AreEqual(settings.BASE_SPINS, extraLedger.EarnedTurns);
        Assert.AreEqual(0, extraLedger.LogicalExtraGoAwards);
        Assert.AreEqual(0, prizeLedger.ValidateFinal().Count);
    }

    [TestMethod]
    public void ForwardTurnAssemblerAdvancesBoardWithExactNormalSpawnsAndWinProgress()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            NonWinTargets = new Dictionary<int, int> { [2] = settings.NONWIN_MIN_TARGET },
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var board = new ForwardBoardState(FilledBoard(6), settings);
        var shape = Shape(1, 1, 1, 1, 1);
        var emptyCount = board.PreviewAfterPushRotate(shape).EmptyPositions.Count;
        var normalIntents = new[] { new ForwardNormalSpawnIntent(ForwardSymbolIntent.MustProgressWin) }
            .Concat(Enumerable.Repeat(ForwardNormalSpawnIntent.SafeFiller(), emptyCount - 1))
            .ToArray();
        var symbolLedger = new SymbolLedger(objectives.WinTargets, objectives.NearMissTargets, objectives.MaxSymbol, settings);

        var result = new ForwardTurnAssembler(settings, seed: 51).AssembleAndAdvance(
            turn: 1,
            plannedTotalTurns: 5,
            board,
            shape,
            objectives,
            featureIntents: Array.Empty<ForwardFeatureIntent>(),
            normalIntents,
            futureTurns: new[] { new ForwardFutureTurn(2, Shape(1, 1, 1, 1, 1)) },
            remainingFeatureCapacity: FeatureCapacity(),
            symbolLedger: symbolLedger,
            extraSpinLedger: new ForwardExtraSpinLedger(plannedTotalTurns: 5, settings),
            prizeUpgradeLedger: EmptyPrizeLedger(objectives.MaxSymbol));

        Assert.AreEqual(ForwardTurnAssemblyStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(emptyCount, result.Spawns.Count);
        Assert.AreEqual(1, symbolLedger.CollectedCount(1));
        Assert.AreEqual(5, result.Collected.GetValueOrDefault(6));
        Assert.IsFalse(board.Snapshot().Cast<Cell?>().Any(cell => cell == null));
    }

    [TestMethod]
    public void ForwardTurnAssemblerMergesFeatureAndNormalSpawnsExactly()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int>(),
            NonWinTargets = new Dictionary<int, int> { [2] = settings.NONWIN_MIN_TARGET },
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var board = new ForwardBoardState(FilledBoard(6), settings);
        var shape = Shape(1, 1, 1, 1, 1);
        var emptyCount = board.PreviewAfterPushRotate(shape).EmptyPositions.Count;
        var extraLedger = new ForwardExtraSpinLedger(plannedTotalTurns: 6, settings);

        var result = new ForwardTurnAssembler(settings, seed: 52).AssembleAndAdvance(
            turn: 1,
            plannedTotalTurns: 6,
            board,
            shape,
            objectives,
            featureIntents: new[] { ForwardFeatureIntent.ExtraGo(turn: 1), ForwardFeatureIntent.Flush(turn: 1) },
            normalIntents: Enumerable.Repeat(ForwardNormalSpawnIntent.SafeFiller(), emptyCount - 1).ToArray(),
            futureTurns: new[] { new ForwardFutureTurn(2, Shape(1, 1, 1, 1, 1)) },
            remainingFeatureCapacity: FeatureCapacity((ForwardFeatureKind.ExtraGo, 1)),
            symbolLedger: new SymbolLedger(objectives.WinTargets, objectives.NearMissTargets, objectives.MaxSymbol, settings),
            extraSpinLedger: extraLedger,
            prizeUpgradeLedger: EmptyPrizeLedger(objectives.MaxSymbol));

        Assert.AreEqual(ForwardTurnAssemblyStatus.Valid, result.Status, result.Detail);
        Assert.AreEqual(emptyCount, result.Spawns.Count);
        Assert.AreEqual(1, result.FlushCount);
        Assert.AreEqual(1, result.Spawns.Count(spawn => spawn.Cell.Sym == settings.F_XSPIN));
        Assert.AreEqual(settings.BASE_SPINS + 1, extraLedger.EarnedTurns);
        Assert.AreEqual(1, extraLedger.LogicalExtraGoAwards);
    }

    [TestMethod]
    public void ForwardTurnAssemblerRejectsNormalIntentMismatchWithoutMutation()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int>(),
            NonWinTargets = new Dictionary<int, int> { [2] = settings.NONWIN_MIN_TARGET },
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var board = new ForwardBoardState(FilledBoard(6), settings);
        var before = BoardSignature(board.Snapshot());
        var symbolLedger = new SymbolLedger(objectives.WinTargets, objectives.NearMissTargets, objectives.MaxSymbol, settings);
        var extraLedger = new ForwardExtraSpinLedger(plannedTotalTurns: 5, settings);

        var result = new ForwardTurnAssembler(settings, seed: 53).AssembleAndAdvance(
            turn: 1,
            plannedTotalTurns: 5,
            board,
            Shape(1, 1, 1, 1, 1),
            objectives,
            featureIntents: Array.Empty<ForwardFeatureIntent>(),
            normalIntents: Array.Empty<ForwardNormalSpawnIntent>(),
            futureTurns: new[] { new ForwardFutureTurn(2, Shape(1, 1, 1, 1, 1)) },
            remainingFeatureCapacity: FeatureCapacity(),
            symbolLedger: symbolLedger,
            extraSpinLedger: extraLedger,
            prizeUpgradeLedger: EmptyPrizeLedger(objectives.MaxSymbol));

        Assert.AreEqual(ForwardTurnAssemblyStatus.NormalIntentCountMismatch, result.Status);
        Assert.AreEqual(before, BoardSignature(board.Snapshot()));
        Assert.AreEqual(0, symbolLedger.Collected.Values.Sum());
        Assert.AreEqual(settings.BASE_SPINS, extraLedger.EarnedTurns);
    }

    [TestMethod]
    public void ForwardTurnAssemblerRejectsUnsatisfiedResidueIntentWithoutMutation()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int>(),
            NonWinTargets = new Dictionary<int, int> { [2] = settings.NONWIN_MIN_TARGET },
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var board = new ForwardBoardState(FilledBoard(6), settings);
        var shape = Shape(1, 1, 1, 1, 1);
        var emptyCount = board.PreviewAfterPushRotate(shape).EmptyPositions.Count;

        var result = new ForwardTurnAssembler(settings, seed: 54).AssembleAndAdvance(
            turn: 1,
            plannedTotalTurns: 5,
            board,
            shape,
            objectives,
            featureIntents: Array.Empty<ForwardFeatureIntent>(),
            normalIntents: Enumerable.Repeat(new ForwardNormalSpawnIntent(ForwardSymbolIntent.ResidueOnly), emptyCount).ToArray(),
            futureTurns: new[] { new ForwardFutureTurn(2, Shape(5, 5, 5, 5, 5)) },
            remainingFeatureCapacity: FeatureCapacity(),
            symbolLedger: new SymbolLedger(objectives.WinTargets, objectives.NearMissTargets, objectives.MaxSymbol, settings),
            extraSpinLedger: new ForwardExtraSpinLedger(plannedTotalTurns: 5, settings),
            prizeUpgradeLedger: EmptyPrizeLedger(objectives.MaxSymbol));

        Assert.AreEqual(ForwardTurnAssemblyStatus.NormalIntentCannotBeSatisfied, result.Status);
    }

    [TestMethod]
    public void ForwardFreshPipelineResolvesSeededPrizeInputsThroughIntentPlanning()
    {
        var settings = SettingsWithNoOptionalFeatures();
        var amountCases = new[]
        {
            Array.Empty<decimal>(),
            new decimal[] { 1m },
            new decimal[] { 2m },
            new decimal[] { 5m },
            new decimal[] { 10m },
            new decimal[] { 100m },
            new decimal[] { 10000m },
            new decimal[] { 1m, 2m },
            new decimal[] { 1m, 2m, 5m },
            new decimal[] { 10m, 100m },
        };

        foreach (var amounts in amountCases)
        {
            for (var seed = 1; seed <= 40; seed++)
            {
                var math = new ForwardMathInputResolver(settings).Resolve(amounts, seed);
                Assert.AreEqual(ForwardMathInputStatus.Valid, math.Status, $"amounts=[{string.Join(",", amounts)}], seed={seed}: {math.Detail}");

                var objectives = new ForwardObjectivePlanner(settings).Resolve(math.Bundle!.Input, seed + 1000);
                Assert.AreEqual(ForwardObjectiveStatus.Valid, objectives.Status, $"amounts=[{string.Join(",", amounts)}], seed={seed}: {objectives.Detail}");

                var budget = new ForwardFeatureBudgetPlanner(settings).Plan(math.Bundle.Input, objectives.Objectives, seed + 2000);
                Assert.AreEqual(ForwardFeatureBudgetStatus.Valid, budget.Status, $"amounts=[{string.Join(",", amounts)}], seed={seed}: {budget.Detail}");
                Assert.AreEqual(settings.BASE_SPINS + budget.Budget!.ExtraGoCount, budget.Budget.TotalTurns);

                var timing = new ForwardFeatureTimingPlanner(settings, seed + 3000).Plan(budget.Budget);
                Assert.AreEqual(ForwardFeatureTimingStatus.Valid, timing.Status, $"amounts=[{string.Join(",", amounts)}], seed={seed}: {timing.Detail}");
                Assert.IsTrue(timing.Timing!.Events.All(feature => feature.Turn < timing.Timing.TotalTurns));
                Assert.AreEqual(
                    timing.Timing.Count(ForwardTimedFeatureKind.PrizeUpgrade),
                    timing.Timing.TurnsFor(ForwardTimedFeatureKind.PrizeUpgrade).Distinct().Count());

                var intents = new ForwardFeatureIntentPlanner(settings, seed + 4000).Plan(objectives.Objectives, timing.Timing);
                Assert.AreEqual(ForwardFeatureIntentStatus.Valid, intents.Status, $"amounts=[{string.Join(",", amounts)}], seed={seed}: {intents.Detail}");
                Assert.AreEqual(timing.Timing.Events.Count, intents.Plan!.Intents.Count);
            }
        }
    }

    [TestMethod]
    public void ForwardTurnAssemblerMaintainsExactSpawnInvariantAcrossSeededShapes()
    {
        var settings = Settings.Default;
        var objectives = ResolveObjectives(settings, new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = settings.SymbolFillCap(1) },
            NonWinTargets = new Dictionary<int, int> { [2] = settings.NONWIN_MIN_TARGET },
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        });
        var shapes = new[]
        {
            Shape(1, 1, 1, 1, 1),
            Shape(1, 2, 3, 4, 1),
            Shape(4, 3, 2, 1, 2),
            Shape(5, 1, 2, 3, 1),
            Shape(1, 5, 1, 4, 2),
        };

        for (var seed = 1; seed <= 50; seed++)
        {
            var shape = shapes[seed % shapes.Length];
            var board = new ForwardBoardState(FilledBoard(6), settings);
            var preview = board.PreviewAfterPushRotate(shape);
            Assert.AreEqual(ForwardBoardAdvanceStatus.Valid, preview.Status, preview.Detail);

            var hasExtraGo = seed % 3 == 0;
            var featureIntents = hasExtraGo
                ? new[] { ForwardFeatureIntent.ExtraGo(turn: 1) }
                : Array.Empty<ForwardFeatureIntent>();
            var normalCount = preview.EmptyPositions.Count - featureIntents.Length;
            var normalIntents = Enumerable.Repeat(ForwardNormalSpawnIntent.SafeFiller(), normalCount).ToArray();
            var extraLedger = new ForwardExtraSpinLedger(plannedTotalTurns: hasExtraGo ? 6 : 5, settings);
            var symbolLedger = new SymbolLedger(objectives.WinTargets, objectives.NearMissTargets, objectives.MaxSymbol, settings);
            var before = BoardSignature(board.Snapshot());

            var result = new ForwardTurnAssembler(settings, seed + 5000).AssembleAndAdvance(
                turn: 1,
                plannedTotalTurns: hasExtraGo ? 6 : 5,
                board,
                shape,
                objectives,
                featureIntents,
                normalIntents,
                futureTurns: new[] { new ForwardFutureTurn(2, Shape(1, 1, 1, 1, 1)) },
                remainingFeatureCapacity: hasExtraGo
                    ? FeatureCapacity((ForwardFeatureKind.ExtraGo, 1))
                    : FeatureCapacity(),
                symbolLedger,
                extraLedger,
                EmptyPrizeLedger(objectives.MaxSymbol));

            Assert.AreEqual(ForwardTurnAssemblyStatus.Valid, result.Status, $"seed={seed}: {result.Detail}");
            Assert.AreEqual(preview.EmptyPositions.Count, result.Spawns.Count, $"seed={seed}");
            Assert.AreEqual(result.Spawns.Count, result.Spawns.Select(spawn => (spawn.Row, spawn.Col)).Distinct().Count(), $"seed={seed}");
            Assert.IsFalse(board.Snapshot().Cast<Cell?>().Any(cell => cell == null), $"seed={seed}");
            Assert.AreNotEqual(before, BoardSignature(board.Snapshot()));
            Assert.AreEqual(hasExtraGo ? settings.BASE_SPINS + 1 : settings.BASE_SPINS, extraLedger.EarnedTurns);
            Assert.AreEqual(hasExtraGo ? 1 : 0, extraLedger.LogicalExtraGoAwards);
        }
    }

    private static Cell?[,] FilledBoard(int symbol)
    {
        var board = new Cell?[Settings.Default.ROWS, Settings.Default.COLS];
        for (var row = 0; row < Settings.Default.ROWS; row++)
        {
            for (var col = 0; col < Settings.Default.COLS; col++)
                board[row, col] = Grid.Norm(symbol);
        }

        return board;
    }

    private static string BoardSignature(Cell?[,] board)
    {
        var parts = new List<string>();
        for (var row = 0; row < Settings.Default.ROWS; row++)
        {
            for (var col = 0; col < Settings.Default.COLS; col++)
            {
                var cell = board[row, col];
                parts.Add(cell == null
                    ? "null"
                    : $"{cell.Sym}:{cell.Stack}:{cell.IsFeat}:{cell.CvtSym}");
            }
        }

        return string.Join("|", parts);
    }

    private static Cell?[,] NumberedBoard()
    {
        var board = new Cell?[Settings.Default.ROWS, Settings.Default.COLS];
        var sym = 1;
        for (var row = 0; row < Settings.Default.ROWS; row++)
        {
            for (var col = 0; col < Settings.Default.COLS; col++)
                board[row, col] = Grid.Norm(sym++);
        }

        return board;
    }

    private static Cell?[,] EmptyBoard() =>
        new Cell?[Settings.Default.ROWS, Settings.Default.COLS];

    private static ForwardTurnShape Shape(params int[] pushValues)
    {
        var pushers = pushValues
            .Select(push => push == Settings.Default.ROWS
                ? new ForwardPusher(Settings.Default.ROWS, Settings.Default.F_FLUSH_ID)
                : new ForwardPusher(push))
            .ToArray();
        var (shape, check) = ForwardTurnShape.TryCreate(pushers, Settings.Default);
        Assert.AreEqual(ForwardTurnShapeStatus.Valid, check.Status);
        return shape!;
    }

    private static ForwardSpawnPlanner SpawnPlanner(
        SymbolLedger ledger,
        IReadOnlyDictionary<int, int> winTargets,
        IReadOnlyDictionary<int, int> nearMissMinimums,
        IReadOnlyList<int> fillerSymbols,
        int maxSymbol,
        int seed,
        Settings? settings = null)
    {
        var actualSettings = settings ?? Settings.Default;
        return new ForwardSpawnPlanner(
            new ForwardSymbolSelector(
                ledger,
                winTargets,
                nearMissMinimums,
                fillerSymbols,
                maxSymbol,
                actualSettings,
                new Random(seed)),
            new ForwardCellFateAnalyzer(actualSettings),
            actualSettings);
    }

    private static (int r, int c)[] Positions(params (int r, int c)[] positions) => positions;

    private static Dictionary<ForwardFeatureKind, int> FeatureCapacity(
        params (ForwardFeatureKind Kind, int Count)[] capacities) =>
        capacities.ToDictionary(capacity => capacity.Kind, capacity => capacity.Count);

    private static ForwardPrizeUpgradeLedger EmptyPrizeLedger(int maxSymbol) =>
        new(
            new Dictionary<int, int>(),
            PrizeValues(maxSymbol, tiers: 2),
            maxSymbol,
            Settings.Default);

    private static Settings SettingsWithForcedNearMiss(int count)
    {
        var weights = new double[Math.Max(1, count)];
        weights[count - 1] = 1.0;

        return new Settings
        {
            NonWinTargetProfiles = new[] { (1.0, 10, 19, count) },
            NonWinCountWeights = weights,
        };
    }

    private static Settings SettingsWithNoOptionalFeatures() =>
        new()
        {
            PNoWinExtraGoOptional = 0.0,
            POptionalFeatureTicket = 0.0,
            PWheelOptional = 0.0,
            PFlushOptional = 0.0,
        };

    private static Settings SettingsWithNoOptionalFeaturesAndNoNearMiss() =>
        new()
        {
            PNoWinExtraGoOptional = 0.0,
            POptionalFeatureTicket = 0.0,
            PWheelOptional = 0.0,
            PFlushOptional = 0.0,
            NonWinTargetProfiles = new[] { (1.0, 0, 0, 0) },
        };

    private static MathInput WithRequired(
        MathInput input,
        IReadOnlyDictionary<string, int> required) =>
        new()
        {
            Targets = input.Targets,
            BaseSpins = input.BaseSpins,
            Required = required,
            WheelSymOrder = input.WheelSymOrder,
            PrizeTiers = input.PrizeTiers,
            PrizeValues = input.PrizeValues,
            NonWinTargets = input.NonWinTargets,
            NonWinPrizeTiers = input.NonWinPrizeTiers,
            MaxSym = input.MaxSym,
        };

    private static ForwardObjectives ResolveObjectives(Settings settings, MathInput input)
    {
        var result = new ForwardObjectivePlanner(settings).Resolve(input, seed: 1234);
        Assert.AreEqual(ForwardObjectiveStatus.Valid, result.Status, result.Detail);
        return result.Objectives!;
    }

    private static GamePlan ForwardPlan(MathInput input, int seed, Settings? settings = null)
    {
        var actualSettings = settings ?? Settings.Default;
        var last = "";
        for (var attempt = 0; attempt < Math.Min(actualSettings.MaxPlanAttempts, 32); attempt++)
        {
            var result = TryForwardPlan(input, AttemptSeed(seed, attempt), actualSettings);
            if (result.Plan != null)
                return result.Plan;

            last = result.Detail;
        }

        Assert.Fail($"Could not build a forward plan from test MathInput after bounded attempts: {last}");
        throw new InvalidOperationException(last);
    }

    private static (GamePlan? Plan, string Detail) TryForwardPlan(
        MathInput input,
        int seed,
        Settings actualSettings)
    {
        var objectives = new ForwardObjectivePlanner(actualSettings).Resolve(input, seed + 1);
        if (!objectives.IsValid) return (null, $"{objectives.Status}: {objectives.Detail}");

        var budget = new ForwardFeatureBudgetPlanner(actualSettings).Plan(input, objectives.Objectives, seed + 2);
        if (!budget.IsValid) return (null, $"{budget.Status}: {budget.Detail}");

        var timing = new ForwardFeatureTimingPlanner(actualSettings, seed + 3).Plan(budget.Budget);
        if (!timing.IsValid) return (null, $"{timing.Status}: {timing.Detail}");

        var intents = new ForwardFeatureIntentPlanner(actualSettings, seed + 4).Plan(objectives.Objectives, timing.Timing);
        if (!intents.IsValid) return (null, $"{intents.Status}: {intents.Detail}");

        var frames = new ForwardTurnFramePlanner(actualSettings, seed + 5).Plan(
            budget.Budget,
            intents.Plan,
            objectives.Objectives);
        if (!frames.IsValid) return (null, $"{frames.Status}: {frames.Detail}");

        var finalized = new ForwardObjectiveFinalizer(actualSettings).Finalize(
            objectives.Objectives,
            intents.Plan,
            frames.Plan);
        if (!finalized.IsValid) return (null, $"{finalized.Status}: {finalized.Detail}");

        var finalObjectives = new ForwardObjectiveResult(
            ForwardObjectiveStatus.Valid,
            finalized.Detail,
            finalized.Objectives);

        var envelope = new ForwardTicketEnvelopeValidator(actualSettings).Validate(
            finalObjectives.Objectives,
            frames.Plan);
        if (!envelope.IsValid) return (null, $"{envelope.Status}: {envelope.Detail}");

        var pipeline = new ForwardTicketPipelineExecutor(actualSettings, seed + 6).Execute(
            finalObjectives.Objectives,
            frames.Plan);
        if (!pipeline.IsValid) return (null, $"{pipeline.Status}: {pipeline.Detail}");

        var build = new ForwardTicketBuildResult(
            ForwardTicketBuildStatus.Valid,
            "test helper",
            null,
            finalObjectives,
            budget,
            timing,
            intents,
            frames,
            envelope,
            pipeline);
        var adapted = new ForwardGamePlanAdapter(actualSettings).Adapt(build);
        if (!adapted.IsValid || adapted.Plan == null)
            return (null, $"{adapted.Status}: {adapted.Detail}");

        return adapted.Plan.Verified
            ? (adapted.Plan, "ok")
            : (null, "adapted plan was not verified");
    }

    private static int AttemptSeed(int seed, int attempt)
    {
        if (attempt == 0) return seed;

        unchecked
        {
            var next = seed;
            next = (next * 397) ^ attempt;
            next ^= 0x6d2b79f5;
            return next == int.MinValue ? 0 : Math.Abs(next);
        }
    }

    private static Dictionary<int, IReadOnlyDictionary<int, decimal>> PrizeValues(int maxSym, int tiers)
    {
        var values = new Dictionary<int, IReadOnlyDictionary<int, decimal>>();
        for (var sym = 1; sym <= maxSym; sym++)
        {
            values[sym] = Enumerable.Range(0, tiers)
                .ToDictionary(tier => tier, tier => (decimal)(sym * 10 + tier));
        }

        return values;
    }
}
