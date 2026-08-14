using Newtonsoft.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CoinPusherEngine.Tests;

[TestClass]
[DoNotParallelize]
public sealed class GameLogicCoverageTests
{
    [TestInitialize]
    public void ResetSettings() =>
        GameEngine.Engine.Settings = new GameEngine.DefaultCoinPusherSettings();

    [DataTestMethod]
    [DataRow(101)]
    [DataRow(202)]
    [DataRow(303)]
    [DataRow(404)]
    public void PlannerSerializerAndCheckerGenerateValidWinningTickets(int seed)
    {
        var input = new MathInput
        {
            Targets = new Dictionary<int, int>
            {
                [2] = Settings.SymbolFillCap(2),
                [4] = Settings.SymbolFillCap(4),
                [5] = Settings.SymbolFillCap(5),
            },
            BaseSpins = 5,
            Required = new Dictionary<string, int>
            {
                ["WHEEL"] = 1,
                ["FLUSH"] = 1,
                ["EXTRA_SPIN"] = 1,
                ["PRIZE_UPGRADE"] = 1,
            },
            PrizeTiers = new Dictionary<int, int> { [2] = 1 },
            PrizeValues = PrizeValues(6, tiers: 3),
            MaxSym = 6,
        };

        var ticket = PlanTicket(input, seed);

        Assert.AreEqual(3, ticket.WinInfo.WinSymbols.Length);
        Assert.IsTrue(ticket.Turns.Any(turn => turn.Pushers.Any(p => p.FeatureId == 14)));
        Assert.IsTrue(HasFeature(ticket, 11));
        Assert.IsTrue(HasFeature(ticket, 12));
        Assert.IsTrue(HasFeature(ticket, 13));
        AssertValid(ticket);
    }

    [DataTestMethod]
    [DataRow(11)]
    [DataRow(22)]
    [DataRow(33)]
    public void EmptyPrizeBundleCreatesValidNoWinNearMissTickets(int seed)
    {
        var bundle = new LadderCombinator(StandardRows(), seed).Bundle(Array.Empty<decimal>());

        var ticket = PlanTicket(bundle.Input, seed);

        Assert.AreEqual(0, ticket.WinInfo.WinSymbols.Length);
        Assert.IsTrue(ticket.WinInfo.NonWinSymbols.Length > 0);
        Assert.IsTrue(ticket.WinInfo.NonWinSymbols.Any(symbol => symbol.MinTarget >= 10));
        foreach (var symbol in ticket.WinInfo.NonWinSymbols)
        {
            Assert.IsTrue(symbol.MinTarget > 0 && symbol.MinTarget < symbol.MaxThreshold);
            Assert.IsTrue(symbol.Id >= 1);
        }
        AssertValid(ticket);
    }

    [DataTestMethod]
    [DataRow(9001)]
    [DataRow(9002)]
    public void WinningTicketsWithoutRequiredFeaturesRemainValid(int seed)
    {
        var input = new MathInput
        {
            Targets = new Dictionary<int, int>
            {
                [2] = Settings.SymbolFillCap(2),
                [4] = Settings.SymbolFillCap(4),
            },
            BaseSpins = 5,
            Required = new Dictionary<string, int>(),
            MaxSym = 6,
        };

        var ticket = PlanTicket(input, seed);

        AssertValid(ticket);
    }

    [TestMethod]
    public void LadderBundleCoversEveryRequestedPrizeIncludingTopTier()
    {
        var bundle = new LadderCombinator(StandardRows(), seed: 777)
            .Bundle(new decimal[] { 1, 2, 5, 10, 100, 10000 });

        CollectionAssert.AreEqual(new decimal[] { 1, 2, 5, 10, 100, 10000 }, bundle.Covered);
        Assert.AreEqual(0, bundle.Skipped.Count);
        Assert.AreEqual(6, bundle.Input.Targets.Count);

        var ticket = PlanTicket(bundle.Input, seed: 777);

        Assert.AreEqual(6, ticket.WinInfo.WinSymbols.Length);
        AssertValid(ticket);
    }

    [TestMethod]
    public void LadderBundleBacktracksToPreserveFuturePrizeOptions()
    {
        var prizes = new decimal[] { 100, 250 };

        for (var seed = 1; seed <= 20; seed++)
        {
            var bundle = new LadderCombinator(StandardRows(), seed).Bundle(prizes);

            CollectionAssert.AreEqual(prizes, bundle.Covered);
            Assert.AreEqual(2, bundle.Input.Targets.Count);
            Assert.IsTrue(bundle.Input.PrizeTiers!.Values.Sum() >= 3);

            var ticket = PlanTicket(bundle.Input, seed);
            Assert.AreEqual(2, ticket.WinInfo.WinSymbols.Length);
            AssertValid(ticket);
        }
    }

    [TestMethod]
    public void LadderBundleHonorsConfiguredPrizeUpgradeCap()
    {
        var current = Settings.PrizeUpgradeFeatureConfig;
        var settings = new GameEngine.DefaultCoinPusherSettings {
            PrizeUpgradeFeatureConfig = (current.P, 2, current.MinS, current.MaxS, current.Ord),
        };
        GameEngine.Engine.Settings = settings;

        Assert.ThrowsException<InvalidOperationException>(() =>
            new LadderCombinator(StandardRows(), seed: 31313)
                .Bundle(new decimal[] { 100, 250 }));
    }

    [DataTestMethod]
    [DataRow("1,1", 2)]
    [DataRow("1,1,1", 3)]
    public void LadderBundleGroupsDuplicateSmallPrizesByTotalWhenSeparateSymbolsAreImpossible(
        string prizeCsv,
        int expectedCash)
    {
        var prizes = prizeCsv.Split(',').Select(decimal.Parse).ToArray();
        var bundle = new LadderCombinator(StandardRows(), seed: 34343).Bundle(prizes);

        CollectionAssert.AreEqual(prizes, bundle.Covered);
        Assert.AreEqual(expectedCash, bundle.Entries.Sum(entry => entry.Amounts.Sum()));

        var ticket = PlanTicket(bundle.Input, seed: 34343);
        AssertValid(ticket);
    }

    [DataTestMethod]
    [DataRow("1,2,5", 3, 810301)]
    [DataRow("1,2,5,10", 4, 810302)]
    [DataRow("1,2,5,10,100", 5, 810303)]
    [DataRow("1,2,5,10,100,10000", 6, 810304)]
    [DataRow("2,5,10", 3, 810305)]
    [DataRow("5,10,100,10000", 4, 810306)]
    public void LadderBundlesWithSeveralWinSymbolsGenerateValidTickets(
        string prizeCsv,
        int expectedWinSymbols,
        int seed)
    {
        var prizes = prizeCsv.Split(',').Select(decimal.Parse).ToArray();
        var bundle = new LadderCombinator(StandardRows(), seed).Bundle(prizes);

        Assert.AreEqual(expectedWinSymbols, bundle.Input.Targets.Count);
        CollectionAssert.AreEqual(prizes.OrderBy(prize => prize).ToArray(), bundle.Covered);

        var ticket = PlanTicket(bundle.Input, seed);

        Assert.AreEqual(expectedWinSymbols, ticket.WinInfo.WinSymbols.Length);
        AssertNoFinalBoardFeatures(ticket);
        AssertValid(ticket);
    }

    [TestMethod]
    public void CustomBundleRowsGenerateSixSymbolTicketsAcrossSeeds()
    {
        var amounts = new decimal[] { 1, 2, 5, 10, 100, 10000 };
        for (var i = 0; i < 50; i++)
        {
            var bundleSeed = MixedSeed(20260807, i);
            var plannerSeed = MixedSeed(20260808, i);
            var bundle = new LadderCombinator(CustomBundleRows(), bundleSeed).Bundle(amounts);

            try
            {
                var ticket = PlanTicket(bundle.Input, plannerSeed);
                Assert.AreEqual(6, ticket.WinInfo.WinSymbols.Length);
                Assert.IsTrue(ticket.WinInfo.TotalSpins <= Settings.MAX_SPINS);
            }
            catch (Exception ex)
            {
                Assert.Fail(
                    $"i={i} bundleSeed={bundleSeed} plannerSeed={plannerSeed} " +
                    $"targets=[{string.Join(",", bundle.Input.Targets.Select(kv => $"sym{kv.Key}={kv.Value}"))}] " +
                    $"required=[{string.Join(",", bundle.Input.Required.Select(kv => $"{kv.Key}={kv.Value}"))}] " +
                    $"error={ex.Message}");
            }
        }
    }

    [TestMethod]
    public void TopPrizeTargetCollectsExactly()
    {
        var input = new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = 20, [6] = 30 },
            BaseSpins = 5,
            PrizeValues = PrizeValues(6, tiers: 3),
            MaxSym = 8,
        };

        var plan = ForwardPlan(input, seed: 10000);

        Assert.AreEqual(30, Sim.Run(plan)[6]);
        AssertValid(JsonConvert.DeserializeObject<TicketSerializer.TicketDto>(TicketSerializer.ToJson(plan))!);
    }

    [TestMethod]
    public void LadderResolveReturnsCandidateAndPrizeValuesForUpgradePath()
    {
        var combinator = new LadderCombinator(StandardRows(), seed: 12);

        var resolved = combinator.Resolve(100m);

        Assert.IsNotNull(resolved);
        Assert.IsTrue(combinator.KnownAmounts.Contains(100m));
        Assert.IsTrue(combinator.CandidatesFor(100m).Any(c => c.Amount == 100m));
        Assert.AreEqual(5, resolved!.Value.Input.BaseSpins);
        Assert.IsTrue(resolved.Value.Input.PrizeValues!.Count > 0);
    }

    [DataTestMethod]
    [DataRow(5151)]
    [DataRow(6161)]
    public void RequiredExtraSpinsExtendTicketButDoNotCreateFinalSpinExtraToken(int seed)
    {
        var input = new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = 20, [2] = 20, [3] = 20, [4] = 25 },
            BaseSpins = 5,
            Required = new Dictionary<string, int>
            {
                ["EXTRA_SPIN"] = 2,
                ["WHEEL"] = 2,
                ["FLUSH"] = 1,
            },
            WheelSymOrder = new[] { 1, 2 },
            MaxSym = 7,
        };

        var ticket = PlanTicket(input, seed);

        Assert.IsTrue(ticket.WinInfo.TotalSpins > 5);
        Assert.AreEqual(ticket.WinInfo.TotalSpins - 5, PhysicalFeatureCount(ticket, 12));
        AssertNoFinalBoardFeatures(ticket);
        AssertValid(ticket);
    }

    [DataTestMethod]
    [DataRow(8181)]
    [DataRow(8282)]
    public void PrizeUpgradeDoesNotAppearOnFinalSpin(int seed)
    {
        var input = new MathInput
        {
            Targets = new Dictionary<int, int>
            {
                [2] = Settings.SymbolFillCap(2),
                [4] = Settings.SymbolFillCap(4),
            },
            BaseSpins = 5,
            Required = new Dictionary<string, int> { ["PRIZE_UPGRADE"] = 2 },
            PrizeTiers = new Dictionary<int, int> { [2] = 1, [4] = 1 },
            PrizeValues = PrizeValues(6, tiers: 3),
            MaxSym = 6,
        };

        var ticket = PlanTicket(input, seed);

        Assert.IsTrue(LogicalFeatureCount(ticket, 13) >= 2);
        AssertNoFinalBoardFeatures(ticket);
        AssertValid(ticket);
    }

    [TestMethod]
    public void ConfiguredLateFeaturePlacementBiasesNonWheelFeatureTriggersTowardEndSpinsWhenFeasible()
    {
        var settings = new GameEngine.DefaultCoinPusherSettings {
            PFeatureLatePlacement = 1.0,
            PFeatureRetriggerChain = 0.0,
        };
        GameEngine.Engine.Settings = settings;
        var input = new MathInput
        {
            Targets = new Dictionary<int, int> { [2] = 20 },
            BaseSpins = 5,
            Required = new Dictionary<string, int>
            {
                ["EXTRA_SPIN"] = 1,
                ["PRIZE_UPGRADE"] = 1,
            },
            PrizeTiers = new Dictionary<int, int> { [2] = 1 },
            PrizeValues = PrizeValues(6, tiers: 3),
            MaxSym = 6,
        };

        var plan = ForwardPlan(input, seed: 34344);
        var ticket = TicketSerializer.ToTicketObject(plan);
        var lateStart = Math.Max(1, ticket.WinInfo.TotalSpins - Math.Max(2, settings.WinLateTailSpins + 1));
        var featureTurns = ticket.Turns
            .SelectMany((turn, index) => turn.Spawns
                .Where(spawn => spawn.Feature != null
                    && spawn.Feature.FeatureId != Settings.F_XSPIN)
                .Select(_ => index + 1))
            .ToArray();

        Assert.IsTrue(featureTurns.Length >= 1);
        Assert.IsTrue(featureTurns.All(turn => turn >= lateStart), string.Join(",", featureTurns));
        AssertNoFinalBoardFeatures(ticket);
        AssertValid(ticket);
    }

    [DataTestMethod]
    [DataRow(7001)]
    [DataRow(7002)]
    [DataRow(7003)]
    public void SerializedExtraSpinCountMatchesTotalTurnsWithPhysicalSymbols(int seed)
    {
        var input = new MathInput
        {
            Targets = new Dictionary<int, int>
            {
                [1] = 20,
                [2] = 20,
                [3] = 20,
                [4] = 25,
            },
            BaseSpins = 5,
            Required = new Dictionary<string, int> { ["EXTRA_SPIN"] = 3 },
            MaxSym = 7,
        };

        var ticket = PlanTicket(input, seed);

        Assert.IsTrue(ticket.WinInfo.TotalSpins >= 8);
        Assert.AreEqual(ticket.WinInfo.TotalSpins - 5, PhysicalFeatureCount(ticket, 12));
        AssertExtraSpinTiming(ticket);
        AssertNoFinalBoardFeatures(ticket);
        AssertValid(ticket);
    }

    [TestMethod]
    public void CheckerReportsMalformedTicketsAsInvalid()
    {
        var malformed = new TicketSerializer.TicketDto
        {
            WinInfo = new TicketSerializer.WinInfoDto { TotalSpins = 1 },
            StartingBoard = new[] { new[] { new TicketSerializer.BoardCellDto { Id = 1 } } },
            Turns = Array.Empty<TicketSerializer.TurnDto>(),
        };

        var report = TicketChecker.CheckTicket(malformed);

        Assert.IsFalse(report.IsValid);
        Assert.IsTrue(report.FailCount > 0);
    }

    [TestMethod]
    public void CheckerPluginValidatesGeneratedJsonAfterGeneration()
    {
        var generated = new CoinPusherTicketJsonGenerator()
            .Generate(new decimal[] { 1m, 2m, 5m }, seed: 30301);

        Assert.AreEqual(CoinPusherTicketJsonGenerationStatus.Valid, generated.Status, generated.Detail);
        var result = new CoinPusherTicketCheckerPlugin().CheckJson(generated.Json!);

        Assert.IsTrue(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [TestMethod]
    public void CheckerAcceptsSeedWhereWheelAndPrizeUpgradeShareTurn()
    {
        var generated = new CoinPusherTicketJsonGenerator()
            .Generate(new decimal[] { 2m }, seed: 1304042992);

        Assert.AreEqual(CoinPusherTicketJsonGenerationStatus.Valid, generated.Status, generated.Detail);
        var report = TicketChecker.CheckTicket(generated.Ticket);

        Assert.IsTrue(report.IsValid, string.Join(
            Environment.NewLine,
            report.Checks
                .Where(check => check.Result == TicketChecker.Status.Fail)
                .Select(check => $"{check.Category}/{check.Name}: {check.Detail}")));
    }

    [DataTestMethod]
    [DataRow("2", 1304042992)]
    [DataRow("1,2,5,10", 1304050864)]
    [DataRow("5,10,100,10000", 1304051583)]
    [DataRow("1,2,5,10,100,10000", 20260813)]
    public void SerializedTicketDeclarationsMatchInternalSimulationForRiskSeeds(
        string prizeCsv,
        int seed)
    {
        var prizes = ParsePrizes(prizeCsv);
        var generated = new CoinPusherTicketJsonGenerator().Generate(prizes, seed);

        Assert.AreEqual(CoinPusherTicketJsonGenerationStatus.Valid, generated.Status, generated.Detail);
        AssertTicketMatchesInternalSim(generated.Plan!, generated.Ticket!);
        AssertValid(generated.Ticket!);
    }

    [TestMethod]
    public void CheckerAcceptsFullFrameworkTicketObject()
    {
        var gameData = PlanTicket(new MathInput
        {
            Targets = new Dictionary<int, int>(),
            BaseSpins = 5,
            MaxSym = 6,
        }, seed: 30303);
        var ticket = new Ticket
        {
            ErrorCode = 0,
            Error = "success",
            Game = new TicketGameEnvelope
            {
                PublicState = new TicketPublicState
                {
                    Game = new TicketPublicGame
                    {
                        Parameters = new TicketParameters
                        {
                            Stake = 1m,
                            CashWin = 0m,
                            StakeMultiplier = 0m,
                            IsWinner = false,
                        },
                        GameData = gameData,
                    },
                },
                PrivateState = new TicketPrivateState
                {
                    Stake = 1m,
                    PendingCashWin = 0m,
                },
            },
        };

        var result = TicketChecker.CheckObject(ticket);

        Assert.IsTrue(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [TestMethod]
    public void CheckerRejectsFrameworkCashWinMismatchAgainstPrizeLadder()
    {
        var bundle = new LadderCombinator(StandardRows(), seed: 31313)
            .Bundle(new decimal[] { 100, 250 });
        var gameData = PlanTicket(bundle.Input, seed: 31313);
        var ticket = FrameworkTicket(gameData, cashWin: 999m);

        var result = TicketChecker.CheckObject(ticket);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(error => error.Contains("CashWin matches declared ladder prizes")));
    }

    [TestMethod]
    public void CheckerRejectsDuplicatePrizeTiersWithoutThrowing()
    {
        var gameData = PlanTicket(new MathInput
        {
            Targets = new Dictionary<int, int> { [2] = 20 },
            BaseSpins = 5,
            Required = new Dictionary<string, int> { ["PRIZE_UPGRADE"] = 1 },
            PrizeTiers = new Dictionary<int, int> { [2] = 1 },
            PrizeValues = PrizeValues(6, tiers: 3),
            MaxSym = 6,
        }, seed: 32323);
        gameData.WinInfo.PrizeTiers = gameData.WinInfo.PrizeTiers
            .Concat(gameData.WinInfo.PrizeTiers)
            .ToArray();

        var report = TicketChecker.CheckTicket(gameData);

        Assert.IsFalse(report.IsValid);
        Assert.IsTrue(report.Checks.Any(check =>
            check.Result == TicketChecker.Status.Fail &&
            check.Category == "WinInfo" &&
            check.Name.Contains("duplicate")));
    }

    [TestMethod]
    public void CheckerRejectsFeatureCountsAboveConfiguredCaps()
    {
        var bundle = new LadderCombinator(StandardRows(), seed: 33333)
            .Bundle(new decimal[] { 1, 2, 5, 10, 100, 10000 });
        var gameData = PlanTicket(bundle.Input, seed: 33333);
        var injected = 0;

        foreach (var turn in gameData.Turns.Take(gameData.Turns.Length - 1))
        {
            var spawn = turn.Spawns.FirstOrDefault(s => s.Feature == null);
            if (spawn == null) continue;

            var convertToId = spawn.Id;
            spawn.Id = Settings.F_WHEEL;
            spawn.Feature = new TicketSerializer.FeatureDto
            {
                FeatureId = Settings.F_WHEEL,
                ConvertToId = convertToId,
                WheelSymbolId = gameData.WinInfo.WinSymbols[0].Id,
                WheelStackValue = 1,
                ReTrigger = Array.Empty<TicketSerializer.FeatureDto>(),
            };
            injected++;
            if (injected > Settings.FeatureConfig("WHEEL").Max) break;
        }

        Assert.IsTrue(injected > Settings.FeatureConfig("WHEEL").Max);
        var report = TicketChecker.CheckTicket(gameData);

        Assert.IsFalse(report.IsValid);
        Assert.IsTrue(report.Checks.Any(check =>
            check.Result == TicketChecker.Status.Fail &&
            check.Category == "Feature" &&
            check.Name.Contains("WHEEL count within max")));
    }

    [TestMethod]
    public void CheckerRejectsExtraDropThatOverwritesOccupiedCell()
    {
        var ticket = PlanTicket(new MathInput
        {
            Targets = new Dictionary<int, int>(),
            BaseSpins = 5,
            MaxSym = 6,
        }, seed: 40404);

        var firstTurn = ticket.Turns[0];
        var existingPositions = firstTurn.Spawns.Select(spawn => spawn.Pos).ToHashSet();
        var occupiedPos = Enumerable.Range(0, Settings.ROWS * Settings.COLS)
            .First(pos => !existingPositions.Contains(pos));

        firstTurn.Spawns = firstTurn.Spawns
            .Concat(new[]
            {
                new TicketSerializer.SpawnDto
                {
                    Pos = occupiedPos,
                    Id = Settings.F_COIN,
                },
            })
            .ToArray();

        var report = TicketChecker.CheckTicket(ticket);

        Assert.IsFalse(report.IsValid);
        Assert.IsTrue(report.Checks.Any(check =>
            check.Result == TicketChecker.Status.Fail &&
            check.Category == "Replay" &&
            check.Name.Contains("spawn count matches popped cells")));
        Assert.IsTrue(report.Checks.Any(check =>
            check.Result == TicketChecker.Status.Fail &&
            check.Category == "Replay" &&
            check.Name.Contains("spawns only fill empty cells")));
        Assert.IsTrue(report.Checks.Any(check =>
            check.Result == TicketChecker.Status.Fail &&
            check.Category == "Geometry" &&
            check.Name.Contains("pushed cells match drops")));
    }

    [TestMethod]
    public void CheckerRejectsModifiedPusherPopValue()
    {
        var ticket = PlanTicket(new MathInput
        {
            Targets = new Dictionary<int, int>(),
            BaseSpins = 5,
            MaxSym = 6,
        }, seed: 41414);

        var pusher = ticket.Turns
            .SelectMany(turn => turn.Pushers)
            .First(p => p.FeatureId == null && p.PushValue < Settings.MAX_PUSH);
        pusher.PushValue++;

        var report = TicketChecker.CheckTicket(ticket);

        Assert.IsFalse(report.IsValid);
        Assert.IsTrue(report.Checks.Any(check =>
            check.Result == TicketChecker.Status.Fail &&
            check.Category == "Geometry" &&
            check.Name.Contains("pushed cells match drops")));
    }

    [TestMethod]
    public void CheckerRejectsSpawnMovedToWrongPositionWithoutChangingCount()
    {
        var ticket = PlanTicket(new MathInput
        {
            Targets = new Dictionary<int, int>(),
            BaseSpins = 5,
            MaxSym = 6,
        }, seed: 42424);

        var firstTurn = ticket.Turns[0];
        var correctSpawnPositions = firstTurn.Spawns.Select(spawn => spawn.Pos).ToHashSet();
        var occupiedPosition = Enumerable.Range(0, Settings.ROWS * Settings.COLS)
            .First(pos => !correctSpawnPositions.Contains(pos));

        firstTurn.Spawns[0].Pos = occupiedPosition;

        var report = TicketChecker.CheckTicket(ticket);

        Assert.IsFalse(report.IsValid);
        Assert.IsTrue(report.Checks.Any(check =>
            check.Result == TicketChecker.Status.Fail &&
            check.Category == "Replay" &&
            check.Name.Contains("spawns only fill empty cells")));
        Assert.IsTrue(report.Checks.Any(check =>
            check.Result == TicketChecker.Status.Fail &&
            check.Category == "Replay" &&
            check.Name.Contains("fully populated")));
    }

    [TestMethod]
    public void CheckerRejectsFeatureSymbolOnStartingBoard()
    {
        var ticket = PlanTicket(new MathInput
        {
            Targets = new Dictionary<int, int>(),
            BaseSpins = 5,
            MaxSym = 6,
        }, seed: 50505);

        ticket.StartingBoard[0][0].Id = Settings.F_WHEEL;

        var report = TicketChecker.CheckTicket(ticket);

        Assert.IsFalse(report.IsValid);
        Assert.IsTrue(report.Checks.Any(check =>
            check.Result == TicketChecker.Status.Fail &&
            check.Category == "Schema" &&
            check.Name.Contains("StartingBoard")));
    }

    [TestMethod]
    public void SerializerDeclaresEveryCollectedNonWinningSymbol()
    {
        var input = new MathInput
        {
            Targets = new Dictionary<int, int>
            {
                [2] = Settings.SymbolFillCap(2),
                [4] = Settings.SymbolFillCap(4),
            },
            BaseSpins = 5,
            PrizeValues = PrizeValues(Settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = 6,
        };
        var plan = ForwardPlan(input, seed: 60606);
        var ticket = TicketSerializer.ToTicketObject(plan);
        var declared = ticket.WinInfo.WinSymbols.Select(w => w.Id)
            .Concat(ticket.WinInfo.NonWinSymbols.Select(w => w.Id))
            .ToHashSet();

        foreach (var (sym, count) in Sim.Run(plan))
        {
            if (count > 0 && !Settings.IsFeat(sym))
                Assert.IsTrue(declared.Contains(sym), $"symbol {sym} collected {count} time(s) but was not declared");
        }

        AssertValid(ticket);
    }

    [TestMethod]
    public void CheckerRejectsCollectedSymbolMissingFromWinInfo()
    {
        var ticket = PlanTicket(new MathInput
        {
            Targets = new Dictionary<int, int>(),
            BaseSpins = 5,
            MaxSym = 6,
        }, seed: 70707);

        Assert.IsTrue(ticket.WinInfo.NonWinSymbols.Length > 0);
        ticket.WinInfo.NonWinSymbols = ticket.WinInfo.NonWinSymbols.Skip(1).ToArray();

        var report = TicketChecker.CheckTicket(ticket);

        Assert.IsFalse(report.IsValid);
        Assert.IsTrue(report.Checks.Any(check =>
            check.Result == TicketChecker.Status.Fail &&
            check.Category == "WinInfo" &&
            check.Name.Contains("Collected symbol")));
    }

    [TestMethod]
    public void CheckerRejectsBonusTurnsBeforeExtraSpinAwardsEarnThem()
    {
        var ticket = PlanTicket(new MathInput
        {
            Targets = new Dictionary<int, int>
            {
                [1] = 20,
                [2] = 20,
                [3] = 20,
                [4] = 25,
            },
            BaseSpins = 5,
            Required = new Dictionary<string, int> { ["EXTRA_SPIN"] = 3 },
            MaxSym = 7,
        }, seed: 7001);

        foreach (var spawn in ticket.Turns.SelectMany(turn => turn.Spawns))
        {
            if (spawn.Feature?.FeatureId == Settings.F_XSPIN)
            {
                spawn.Id = Settings.F_COIN;
                spawn.Feature = null;
            }
        }

        var turn5Spawn = ticket.Turns[4].Spawns.First(spawn => spawn.Feature == null);
        turn5Spawn.Id = Settings.F_XSPIN;
        turn5Spawn.Feature = ExtraSpinFeature(convertToId: Settings.F_COIN);

        var turn7 = ticket.Turns[6];
        var turn7Spawns = turn7.Spawns
            .Where(spawn => spawn.Feature == null)
            .Take(2)
            .ToArray();
        Assert.AreEqual(2, turn7Spawns.Length);
        foreach (var spawn in turn7Spawns)
        {
            spawn.Id = Settings.F_XSPIN;
            spawn.Feature = ExtraSpinFeature(convertToId: Settings.F_COIN);
        }

        var report = TicketChecker.CheckTicket(ticket);

        Assert.IsFalse(report.IsValid);
        Assert.IsTrue(report.Checks.Any(check =>
            check.Result == TicketChecker.Status.Fail &&
            check.Category == "Feature" &&
            check.Name == "EXTRA_SPIN timeline earns every turn before play" &&
            check.Detail.Contains("turn 7 exists")));
    }

    [TestMethod]
    public void CheckerRejectsRetriggerParentConvertIdThatDoesNotPointAtChildFeature()
    {
        var ticket = PlanTicket(new MathInput
        {
            Targets = new Dictionary<int, int> { [2] = 20 },
            BaseSpins = 5,
            Required = new Dictionary<string, int> { ["EXTRA_SPIN"] = 1 },
            MaxSym = 6,
        }, seed: 90909);
        var parent = ticket.Turns
            .SelectMany(turn => turn.Spawns)
            .First(spawn => spawn.Feature?.FeatureId == Settings.F_XSPIN);
        parent.Feature!.ConvertToId = 2;
        parent.Feature.ReTrigger = new[]
        {
            new TicketSerializer.FeatureDto
            {
                FeatureId = Settings.F_PRUP,
                ConvertToId = 2,
                UpgradeSymbolId = 2,
                UpgradePrizeValue = 2m,
                ReTrigger = Array.Empty<TicketSerializer.FeatureDto>(),
            },
        };

        var report = TicketChecker.CheckTicket(ticket);

        Assert.IsFalse(report.IsValid);
        Assert.IsTrue(report.Checks.Any(check =>
            check.Result == TicketChecker.Status.Fail &&
            check.Category == "Schema" &&
            check.Name.Contains("ReTrigger convert target")));
    }

    [TestMethod]
    public void CheckerMutationWallRejectsSingleFieldCorruptions()
    {
        var valid = FeatureRichTicket(seed: 92929);
        AssertValid(valid);

        var mutations = new (string Name, Action<TicketSerializer.TicketDto> Mutate, string? Category)[]
        {
            ("normal spawn id changed", ChangeNormalSpawnId, null),
            ("spawn removed", RemoveOneSpawn, "Geometry"),
            ("spawn moved onto occupied cell", MoveSpawnOntoOccupiedCell, "Replay"),
            ("normal pusher value changed", IncreaseNormalPusherValue, "Geometry"),
            ("board feature placed on final spin", AddFinalBoardFeature, "Feature"),
            ("FLUSH/PUSH placed on final spin", AddFinalFlushPusher, "Feature"),
            ("WHEEL stack outside configured range", BreakWheelStackValue, "Schema"),
            ("feature convert target points at feature without ReTrigger", BreakFeatureConvertTarget, "Schema"),
            ("feature convert target outside configured symbol range", BreakFeatureConvertTargetOutOfRange, "Schema"),
            ("PRIZE_UPGRADE payload missing prize value", BreakPrizeUpgradePayload, "Schema"),
            ("collected non-winning symbol removed from WinInfo", RemoveCollectedNonWinDeclaration, "WinInfo"),
            ("TotalSpins does not match turns", BreakTotalSpins, "SpinCount"),
        };

        foreach (var mutation in mutations)
        {
            var ticket = CloneTicket(valid);
            mutation.Mutate(ticket);

            var report = TicketChecker.CheckTicket(ticket);

            AssertRejected(report, mutation.Name, mutation.Category);
        }
    }

    [TestMethod]
    public void CheckerRejectsFrameworkEnvelopeCorruptions()
    {
        var gameData = FeatureRichTicket(seed: 93939);
        var cashWin = ExpectedTicketCashWin(gameData);
        var cases = new (string Name, Action<Ticket> Mutate, string NameContains)[]
        {
            ("cash win", ticket => ticket.Game!.PublicState!.Game!.Parameters!.CashWin += 1m, "CashWin matches declared ladder prizes"),
            ("stake multiplier", ticket => ticket.Game!.PublicState!.Game!.Parameters!.StakeMultiplier += 1m, "StakeMultiplier matches CashWin / Stake"),
            ("winner flag", ticket => ticket.Game!.PublicState!.Game!.Parameters!.IsWinner = false, "IsWinner matches CashWin"),
            ("pending cash win", ticket => ticket.Game!.PrivateState!.PendingCashWin += 1m, "PendingCashWin matches CashWin"),
        };

        foreach (var testCase in cases)
        {
            var ticket = FrameworkTicket(CloneTicket(gameData), cashWin);
            testCase.Mutate(ticket);

            var result = TicketChecker.CheckObject(ticket);

            Assert.IsFalse(result.IsValid, testCase.Name);
            Assert.IsTrue(result.Errors.Any(error => error.Contains(testCase.NameContains)),
                $"{testCase.Name} mutation was not caught. Errors: {string.Join(" | ", result.Errors)}");
        }
    }

    [TestMethod]
    public void CheckerAcceptsConfiguredMaximumFlushPushers()
    {
        var settings = Settings;
        var ticket = PlanTicket(new MathInput
        {
            Targets = new Dictionary<int, int> { [2] = settings.SymbolFillCap(2) },
            Required = new Dictionary<string, int>
            {
                ["FLUSH"] = settings.FeatureConfig("FLUSH").Max,
                ["EXTRA_SPIN"] = 2,
            },
            BaseSpins = settings.BASE_SPINS,
            PrizeValues = PrizeValues(settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = settings.PrizeLadderRows.Count,
        }, seed: 94949);

        Assert.AreEqual(
            settings.FeatureConfig("FLUSH").Max,
            ticket.Turns.Sum(turn => turn.Pushers.Count(pusher => pusher.FeatureId == settings.F_FLUSH_ID)));
        AssertValid(ticket);
    }

    [TestMethod]
    public void SerializerDoesNotCreateCrossTurnRetriggerChains()
    {
        var settings = new GameEngine.DefaultCoinPusherSettings { PFeatureRetriggerChain = 1.0 };
        GameEngine.Engine.Settings = settings;
        var plan = new GamePlan
        {
            TotalSpins = 3,
            Targets = new Dictionary<int, int> { [2] = 1 },
            WinSyms = new[] { 2 },
            FillSyms = new[] { 1, 3 },
            PrizeTiers = new Dictionary<int, int> { [2] = 2 },
            PrizeValues = PrizeValues(6, tiers: 3),
            Spins = new List<SpinPlan>
            {
                SpinWithPrizeUpgrade(1, tier: 1),
                SpinWithPrizeUpgrade(2, tier: 2),
                PlainSpin(3),
            },
        };

        var ticket = TicketSerializer.ToTicketObject(plan);

        Assert.IsFalse(ticket.Turns
            .SelectMany(turn => turn.Spawns)
            .Where(spawn => spawn.Feature != null)
            .Any(spawn => spawn.Feature!.ReTrigger.Length > 0));
    }

    [TestMethod]
    public void SerializerUsesNestedFeatureIdAsRetriggerConvertTarget()
    {
        var settings = new GameEngine.DefaultCoinPusherSettings { PFeatureRetriggerChain = 1.0 };
        GameEngine.Engine.Settings = settings;
        var plan = new GamePlan
        {
            TotalSpins = 3,
            Targets = new Dictionary<int, int> { [2] = 1 },
            WinSyms = new[] { 2 },
            FillSyms = new[] { 1, 3 },
            PrizeTiers = new Dictionary<int, int> { [2] = 1 },
            PrizeValues = PrizeValues(6, tiers: 3),
            Spins = new List<SpinPlan>
            {
                SpinWithExtraAndPrizeUpgrade(1),
                PlainSpin(2),
                PlainSpin(3),
            },
        };

        var ticket = TicketSerializer.ToTicketObject(plan);
        var parent = ticket.Turns
            .SelectMany(turn => turn.Spawns)
            .First(spawn => spawn.Feature?.ReTrigger.Length == 1);

        Assert.AreEqual(Settings.F_PRUP, parent.Feature!.ConvertToId);
        Assert.AreEqual(Settings.F_PRUP, parent.Feature.ReTrigger[0].FeatureId);
        Assert.AreEqual(2, parent.Feature.ReTrigger[0].ConvertToId);
    }

    [TestMethod]
    public void SerializerKeepsWheelPhysicalBecauseBoardPositionAffectsStacking()
    {
        var settings = new GameEngine.DefaultCoinPusherSettings { PFeatureRetriggerChain = 1.0 };
        GameEngine.Engine.Settings = settings;
        var plan = new GamePlan
        {
            TotalSpins = 3,
            Targets = new Dictionary<int, int>(),
            WinSyms = Array.Empty<int>(),
            FillSyms = new[] { 1, 2, 3 },
            PrizeValues = PrizeValues(6, tiers: 3),
            Spins = new List<SpinPlan>
            {
                SpinWithExtraAndWheel(1),
                PlainSpin(2),
                PlainSpin(3),
            },
        };

        var ticket = TicketSerializer.ToTicketObject(plan);
        var featureSpawns = ticket.Turns
            .SelectMany(turn => turn.Spawns)
            .Where(spawn => spawn.Feature != null)
            .ToArray();

        Assert.IsFalse(featureSpawns.Any(spawn =>
            spawn.Feature!.ReTrigger.Any(child => child.FeatureId == Settings.F_WHEEL)));
        Assert.IsTrue(featureSpawns.Any(spawn =>
            spawn.Feature!.FeatureId == Settings.F_WHEEL
            && spawn.Feature.WheelSymbolId == 2
            && spawn.Feature.WheelStackValue == 1));
    }

    [TestMethod]
    public void SerializerKeepsWheelPhysicalWhenPrizeUpgradeSharesTurn()
    {
        var settings = new GameEngine.DefaultCoinPusherSettings { PFeatureRetriggerChain = 1.0 };
        GameEngine.Engine.Settings = settings;
        var plan = new GamePlan
        {
            TotalSpins = 5,
            Targets = new Dictionary<int, int>(),
            WinSyms = Array.Empty<int>(),
            FillSyms = new[] { 1, 2, 3 },
            PrizeValues = PrizeValues(6, tiers: 3),
            Spins = new List<SpinPlan>
            {
                SpinWithPrizeUpgradeAndWheel(1),
                PlainSpin(2),
                PlainSpin(3),
                PlainSpin(4),
                PlainSpin(5),
            },
        };

        var ticket = TicketSerializer.ToTicketObject(plan);
        var featureSpawns = ticket.Turns[0].Spawns
            .Where(spawn => spawn.Feature != null)
            .ToArray();

        Assert.IsFalse(featureSpawns.Any(spawn =>
            spawn.Feature!.ReTrigger.Any(child => child.FeatureId == Settings.F_WHEEL)));
        Assert.IsTrue(featureSpawns.Any(spawn => spawn.Feature!.FeatureId == Settings.F_PRUP));
        Assert.IsTrue(featureSpawns.Any(spawn => spawn.Feature!.FeatureId == Settings.F_WHEEL));
    }

    [TestMethod]
    public void CheckerRejectsWheelNestedInRetriggerBecausePositionIsLoadBearing()
    {
        var generated = new CoinPusherTicketJsonGenerator()
            .Generate(new decimal[] { 2m }, seed: 1304042992);
        Assert.AreEqual(CoinPusherTicketJsonGenerationStatus.Valid, generated.Status, generated.Detail);
        var ticket = CloneTicket(generated.Ticket!);
        var turn = ticket.Turns.First(item =>
            item.Spawns.Any(spawn => spawn.Feature?.FeatureId == Settings.F_PRUP)
            && item.Spawns.Any(spawn => spawn.Feature?.FeatureId == Settings.F_WHEEL));
        var prizeUpgrade = turn.Spawns.First(spawn => spawn.Feature?.FeatureId == Settings.F_PRUP).Feature!;
        var wheel = CloneFeature(turn.Spawns.First(spawn => spawn.Feature?.FeatureId == Settings.F_WHEEL).Feature!);

        prizeUpgrade.ConvertToId = Settings.F_WHEEL;
        prizeUpgrade.ReTrigger = new[] { wheel };

        var report = TicketChecker.CheckTicket(ticket);

        AssertRejected(report, "nested WHEEL retrigger", "Schema");
        Assert.IsTrue(report.Checks.Any(check =>
            check.Result == TicketChecker.Status.Fail
            && check.Name.Contains("WHEEL ReTrigger payload")));
    }

    [TestMethod]
    public void CheckerFlagsRepeatedPusherBags()
    {
        var ticket = new TicketSerializer.TicketDto
        {
            WinInfo = new TicketSerializer.WinInfoDto
            {
                TotalSpins = 3,
                WinSymbols = Array.Empty<TicketSerializer.WinSymbolDto>(),
                NonWinSymbols = new[]
                {
                    new TicketSerializer.NonWinSymbolDto { Id = 6, MinTarget = 1, MaxThreshold = 30 },
                },
                PrizeTiers = Array.Empty<TicketSerializer.PrizeTierDto>(),
            },
            StartingBoard = Enumerable.Range(0, Settings.ROWS)
                .Select(_ => Enumerable.Range(0, Settings.COLS)
                    .Select(_ => new TicketSerializer.BoardCellDto { Id = 6 })
                    .ToArray())
                .ToArray(),
            Turns = Enumerable.Range(0, 3)
                .Select(_ => new TicketSerializer.TurnDto
                {
                    Pushers = Enumerable.Repeat(new TicketSerializer.PusherDto { PushValue = 1 }, Settings.COLS).ToArray(),
                    Spawns = new[] { 4, 9, 14, 19, 24 }
                        .Select(pos => new TicketSerializer.SpawnDto { Pos = pos, Id = 6 })
                        .ToArray(),
                })
                .ToArray(),
        };

        var report = TicketChecker.CheckTicket(ticket);

        Assert.IsTrue(report.Checks.Any(check =>
            check.Result == TicketChecker.Status.Warning &&
            check.Category == "Experience" &&
            check.Name.Contains("Pusher bag variety")));
    }

    private static TicketSerializer.TicketDto FeatureRichTicket(int seed) =>
        PlanTicket(new MathInput
        {
            Targets = new Dictionary<int, int>
            {
                [2] = Settings.SymbolFillCap(2),
                [4] = Settings.SymbolFillCap(4),
            },
            BaseSpins = Settings.BASE_SPINS,
            Required = new Dictionary<string, int>
            {
                ["WHEEL"] = 1,
                ["FLUSH"] = 1,
                ["EXTRA_SPIN"] = 1,
                ["PRIZE_UPGRADE"] = 1,
            },
            PrizeTiers = new Dictionary<int, int> { [2] = 1 },
            PrizeValues = PrizeValues(Settings.PrizeLadderRows.Count, tiers: 3),
            MaxSym = Settings.PrizeLadderRows.Count,
        }, seed);

    private static TicketSerializer.TicketDto CloneTicket(TicketSerializer.TicketDto ticket) =>
        JsonConvert.DeserializeObject<TicketSerializer.TicketDto>(
            JsonConvert.SerializeObject(ticket))!;

    private static TicketSerializer.FeatureDto CloneFeature(TicketSerializer.FeatureDto feature) =>
        JsonConvert.DeserializeObject<TicketSerializer.FeatureDto>(
            JsonConvert.SerializeObject(feature))!;

    private static decimal[] ParsePrizes(string prizeCsv) =>
        prizeCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => decimal.Parse(part.Trim()))
            .ToArray();

    private static void ChangeNormalSpawnId(TicketSerializer.TicketDto ticket)
    {
        var spawn = FirstNormalSpawn(ticket, spawn => spawn.Id != Settings.F_COIN);
        spawn.Id = Settings.F_COIN;
    }

    private static void RemoveOneSpawn(TicketSerializer.TicketDto ticket)
    {
        var turn = ticket.Turns.First(t => t.Spawns.Length > 0);
        turn.Spawns = turn.Spawns.Skip(1).ToArray();
    }

    private static void MoveSpawnOntoOccupiedCell(TicketSerializer.TicketDto ticket)
    {
        var turn = ticket.Turns[0];
        var emptyAfterPush = turn.Spawns.Select(spawn => spawn.Pos).ToHashSet();
        var occupiedPosition = Enumerable.Range(0, Settings.ROWS * Settings.COLS)
            .First(pos => !emptyAfterPush.Contains(pos));

        turn.Spawns[0].Pos = occupiedPosition;
    }

    private static void IncreaseNormalPusherValue(TicketSerializer.TicketDto ticket)
    {
        var pusher = ticket.Turns
            .SelectMany(turn => turn.Pushers)
            .First(p => p.FeatureId == null && p.PushValue < Settings.MAX_PUSH);

        pusher.PushValue++;
    }

    private static void AddFinalBoardFeature(TicketSerializer.TicketDto ticket)
    {
        var spawn = ticket.Turns[^1].Spawns.First(s => s.Feature == null);
        var convertToId = spawn.Id;

        spawn.Id = Settings.F_WHEEL;
        spawn.Feature = new TicketSerializer.FeatureDto
        {
            FeatureId = Settings.F_WHEEL,
            ConvertToId = convertToId,
            WheelSymbolId = ticket.WinInfo.WinSymbols.FirstOrDefault()?.Id ?? Settings.F_COIN,
            WheelStackValue = Settings.MIN_WHEEL_STACK_VALUE,
            ReTrigger = Array.Empty<TicketSerializer.FeatureDto>(),
        };
    }

    private static void AddFinalFlushPusher(TicketSerializer.TicketDto ticket)
    {
        var pusher = ticket.Turns[^1].Pushers.First(p => p.FeatureId == null);
        pusher.PushValue = Settings.ROWS;
        pusher.FeatureId = Settings.F_FLUSH_ID;
    }

    private static void BreakWheelStackValue(TicketSerializer.TicketDto ticket)
    {
        var wheel = FirstFeature(ticket, Settings.F_WHEEL);
        wheel.WheelStackValue = Settings.MAX_WHEEL_STACK_VALUE + 1;
    }

    private static void BreakFeatureConvertTarget(TicketSerializer.TicketDto ticket)
    {
        var feature = ticket.Turns
            .SelectMany(turn => turn.Spawns)
            .First(spawn => spawn.Feature != null)
            .Feature!;

        feature.ConvertToId = Settings.F_WHEEL;
        feature.ReTrigger = Array.Empty<TicketSerializer.FeatureDto>();
    }

    private static void BreakFeatureConvertTargetOutOfRange(TicketSerializer.TicketDto ticket)
    {
        var feature = ticket.Turns
            .SelectMany(turn => turn.Spawns)
            .First(spawn => spawn.Feature != null)
            .Feature!;

        feature.ConvertToId = Settings.PrizeLadderRows.Count + 1;
        feature.ReTrigger = Array.Empty<TicketSerializer.FeatureDto>();
    }

    private static void BreakPrizeUpgradePayload(TicketSerializer.TicketDto ticket)
    {
        var prizeUpgrade = FirstFeature(ticket, Settings.F_PRUP);
        prizeUpgrade.UpgradePrizeValue = null;
    }

    private static void RemoveCollectedNonWinDeclaration(TicketSerializer.TicketDto ticket)
    {
        Assert.IsTrue(ticket.WinInfo.NonWinSymbols.Length > 0, "test ticket must contain non-winning declarations");
        ticket.WinInfo.NonWinSymbols = ticket.WinInfo.NonWinSymbols.Skip(1).ToArray();
    }

    private static void BreakTotalSpins(TicketSerializer.TicketDto ticket)
    {
        ticket.WinInfo.TotalSpins++;
    }

    private static TicketSerializer.SpawnDto FirstNormalSpawn(
        TicketSerializer.TicketDto ticket,
        Func<TicketSerializer.SpawnDto, bool>? predicate = null)
    {
        var spawn = ticket.Turns
            .SelectMany(turn => turn.Spawns)
            .FirstOrDefault(spawn => spawn.Feature == null && (predicate == null || predicate(spawn)));

        Assert.IsNotNull(spawn, "test ticket must contain a matching normal spawn");
        return spawn!;
    }

    private static TicketSerializer.FeatureDto FirstFeature(
        TicketSerializer.TicketDto ticket,
        int featureId)
    {
        foreach (var feature in ticket.Turns
                     .SelectMany(turn => turn.Spawns)
                     .Select(spawn => spawn.Feature)
                     .Where(feature => feature != null))
        {
            var found = FindFeature(feature!, featureId);
            if (found != null) return found;
        }

        Assert.Fail($"test ticket must contain feature {featureId}");
        throw new InvalidOperationException($"missing feature {featureId}");
    }

    private static TicketSerializer.FeatureDto? FindFeature(
        TicketSerializer.FeatureDto feature,
        int featureId)
    {
        if (feature.FeatureId == featureId) return feature;
        foreach (var child in feature.ReTrigger ?? Array.Empty<TicketSerializer.FeatureDto>())
        {
            var found = FindFeature(child, featureId);
            if (found != null) return found;
        }

        return null;
    }

    private static void AssertRejected(
        TicketChecker.Report report,
        string mutationName,
        string? expectedCategory)
    {
        var failures = report.Checks
            .Where(check => check.Result == TicketChecker.Status.Fail)
            .ToArray();

        Assert.IsTrue(failures.Length > 0,
            $"{mutationName} mutation unexpectedly passed TicketChecker");

        if (expectedCategory != null)
        {
            Assert.IsTrue(failures.Any(check => check.Category == expectedCategory),
                $"{mutationName} mutation failed, but not in expected category {expectedCategory}. " +
                $"Failures: {string.Join(" | ", failures.Select(check => $"{check.Category}/{check.Name}: {check.Detail}"))}");
        }
    }

    private static decimal ExpectedTicketCashWin(TicketSerializer.TicketDto ticket)
    {
        var tiers = (ticket.WinInfo.PrizeTiers ?? Array.Empty<TicketSerializer.PrizeTierDto>())
            .ToDictionary(tier => tier.SymId, tier => tier.Tier);

        return ticket.WinInfo.WinSymbols.Sum(win =>
        {
            var tier = tiers.GetValueOrDefault(win.Id);
            return Settings.PrizeLadderRows[win.Id - 1].Tiers[tier];
        });
    }

    private static TicketSerializer.TicketDto PlanTicket(MathInput input, int seed)
    {
        var plan = ForwardPlan(input, seed);

        Assert.IsTrue(plan.Verified);

        var json = TicketSerializer.ToJson(plan);
        var ticket = JsonConvert.DeserializeObject<TicketSerializer.TicketDto>(json);

        Assert.IsNotNull(ticket);
        return ticket!;
    }

    private static GamePlan ForwardPlan(MathInput input, int seed, GameEngine.ICustomProfileSettings? settings = null)
    {
        var actualSettings = settings ?? Settings;
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
        GameEngine.ICustomProfileSettings actualSettings)
    {
        GameEngine.Engine.Settings = actualSettings;
        var objectives = new ForwardObjectivePlanner().Resolve(input, seed + 1);
        if (!objectives.IsValid) return (null, $"{objectives.Status}: {objectives.Detail}");

        var budget = new ForwardFeatureBudgetPlanner().Plan(input, objectives.Objectives, seed + 2);
        if (!budget.IsValid) return (null, $"{budget.Status}: {budget.Detail}");

        var timing = new ForwardFeatureTimingPlanner(seed + 3).Plan(budget.Budget);
        if (!timing.IsValid) return (null, $"{timing.Status}: {timing.Detail}");

        var intents = new ForwardFeatureIntentPlanner(seed + 4).Plan(objectives.Objectives, timing.Timing);
        if (!intents.IsValid) return (null, $"{intents.Status}: {intents.Detail}");

        var frames = new ForwardTurnFramePlanner(seed + 5).Plan(
            budget.Budget,
            intents.Plan,
            objectives.Objectives);
        if (!frames.IsValid) return (null, $"{frames.Status}: {frames.Detail}");

        var finalized = new ForwardObjectiveFinalizer().Finalize(
            objectives.Objectives,
            intents.Plan,
            frames.Plan);
        if (!finalized.IsValid) return (null, $"{finalized.Status}: {finalized.Detail}");

        var finalObjectives = new ForwardObjectiveResult(
            ForwardObjectiveStatus.Valid,
            finalized.Detail,
            finalized.Objectives);

        var envelope = new ForwardTicketEnvelopeValidator().Validate(
            finalObjectives.Objectives,
            frames.Plan);
        if (!envelope.IsValid) return (null, $"{envelope.Status}: {envelope.Detail}");

        var pipeline = new ForwardTicketPipelineExecutor(seed + 6).Execute(
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
        var adapted = new ForwardGamePlanAdapter().Adapt(build);
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

    private static void AssertValid(TicketSerializer.TicketDto ticket)
    {
        var report = TicketChecker.CheckTicket(ticket);
        Assert.IsTrue(report.IsValid, string.Join(Environment.NewLine,
            report.Checks
                .Where(c => c.Result == TicketChecker.Status.Fail)
                .Select(c => $"{c.Category}/{c.Name}: {c.Detail}")));
    }

    private static void AssertTicketMatchesInternalSim(
        GamePlan plan,
        TicketSerializer.TicketDto ticket)
    {
        var simTotals = Sim.Run(plan);
        var declaredIds = ticket.WinInfo.WinSymbols.Select(win => win.Id)
            .Concat(ticket.WinInfo.NonWinSymbols.Select(nonWin => nonWin.Id))
            .ToHashSet();

        Assert.AreEqual(plan.TotalSpins, ticket.WinInfo.TotalSpins);
        foreach (var win in ticket.WinInfo.WinSymbols)
        {
            Assert.AreEqual(
                win.Target,
                simTotals.GetValueOrDefault(win.Id),
                $"Win symbol {win.Id} declaration does not match internal simulation");
        }

        foreach (var (symbol, collected) in simTotals)
        {
            if (Settings.IsFeat(symbol) || collected <= 0)
                continue;

            Assert.IsTrue(
                declaredIds.Contains(symbol),
                $"Internal simulation collected symbol {symbol}={collected}, but WinInfo does not declare it");
        }

        foreach (var nonWin in ticket.WinInfo.NonWinSymbols)
        {
            var collected = simTotals.GetValueOrDefault(nonWin.Id);
            Assert.IsTrue(
                collected >= nonWin.MinTarget && collected < nonWin.MaxThreshold,
                $"Non-win symbol {nonWin.Id} internal count {collected} outside [{nonWin.MinTarget}..{nonWin.MaxThreshold})");
        }
    }

    private static bool HasFeature(TicketSerializer.TicketDto ticket, int featureId) =>
        ticket.Turns
            .SelectMany(turn => turn.Spawns)
            .Any(spawn => ContainsFeature(spawn.Feature, featureId));

    private static int PhysicalFeatureCount(TicketSerializer.TicketDto ticket, int featureId) =>
        ticket.Turns
            .SelectMany(turn => turn.Spawns)
            .Count(spawn => spawn.Feature?.FeatureId == featureId);

    private static int LogicalFeatureCount(TicketSerializer.TicketDto ticket, int featureId) =>
        ticket.Turns
            .SelectMany(turn => turn.Spawns)
            .Where(spawn => spawn.Feature != null)
            .Sum(spawn => CountFeatureTree(spawn.Feature!, featureId));

    private static int CountFeatureTree(TicketSerializer.FeatureDto feature, int featureId) =>
        (feature.FeatureId == featureId ? 1 : 0)
        + feature.ReTrigger.Sum(child => CountFeatureTree(child, featureId));

    private static void AssertNoFinalBoardFeatures(TicketSerializer.TicketDto ticket) =>
        Assert.IsFalse(ticket.Turns[^1].Spawns.Any(spawn => spawn.Feature != null));

    private static void AssertExtraSpinTiming(TicketSerializer.TicketDto ticket)
    {
        var availableTurns = Settings.BASE_SPINS;
        for (var index = 0; index < ticket.Turns.Length; index++)
        {
            var turnNumber = index + 1;
            Assert.IsTrue(turnNumber <= availableTurns,
                $"turn {turnNumber} exists before being earned; availableTurns={availableTurns}");

            var extras = ticket.Turns[index].Spawns
                .Where(spawn => spawn.Feature != null)
                .Sum(spawn => CountFeatureTree(spawn.Feature!, Settings.F_XSPIN));
            var futureTurns = ticket.Turns.Length - (index + 1);
            Assert.IsTrue(extras <= futureTurns, $"turn {index + 1} extras={extras}, futureTurns={futureTurns}");

            availableTurns += extras;
            Assert.IsTrue(availableTurns <= ticket.Turns.Length,
                $"turn {turnNumber} over-awards extras; availableTurns={availableTurns}, total={ticket.Turns.Length}");
        }
        Assert.AreEqual(ticket.Turns.Length, availableTurns);
    }

    private static TicketSerializer.FeatureDto ExtraSpinFeature(int convertToId) =>
        new()
        {
            FeatureId = Settings.F_XSPIN,
            ConvertToId = convertToId,
            ReTrigger = Array.Empty<TicketSerializer.FeatureDto>(),
        };

    private static Ticket FrameworkTicket(TicketSerializer.TicketDto gameData, decimal cashWin) =>
        new()
        {
            ErrorCode = 0,
            Error = "success",
            Game = new TicketGameEnvelope
            {
                PublicState = new TicketPublicState
                {
                    Game = new TicketPublicGame
                    {
                        Parameters = new TicketParameters
                        {
                            Stake = 1m,
                            CashWin = cashWin,
                            StakeMultiplier = cashWin,
                            IsWinner = cashWin > 0m,
                        },
                        GameData = gameData,
                    },
                },
                PrivateState = new TicketPrivateState
                {
                    Stake = 1m,
                    PendingCashWin = cashWin,
                },
            },
        };

    private static bool ContainsFeature(TicketSerializer.FeatureDto? feature, int featureId)
    {
        if (feature == null) return false;
        if (feature.FeatureId == featureId) return true;
        return feature.ReTrigger.Any(child => ContainsFeature(child, featureId));
    }

    private static SpinPlan SpinWithPrizeUpgrade(int spin, int tier) =>
        new()
        {
            Spin = spin,
            Board = FilledBoard(1),
            Push = Enumerable.Repeat(1, Settings.COLS).ToArray(),
            Flush = Enumerable.Repeat(false, Settings.COLS).ToArray(),
            Spawns = new Dictionary<(int, int), Cell>
            {
                [(0, 0)] = Grid.Feat(Settings.F_PRUP, 1, new FP
                {
                    FeatId = "PRIZE_UPGRADE",
                    PrupSym = 2,
                    PrupTier = tier,
                }),
            },
        };

    private static SpinPlan PlainSpin(int spin) =>
        new()
        {
            Spin = spin,
            Board = FilledBoard(1),
            Push = Enumerable.Repeat(1, Settings.COLS).ToArray(),
            Flush = Enumerable.Repeat(false, Settings.COLS).ToArray(),
            Spawns = new Dictionary<(int, int), Cell>(),
        };

    private static SpinPlan SpinWithExtraAndPrizeUpgrade(int spin) =>
        new()
        {
            Spin = spin,
            Board = FilledBoard(1),
            Push = Enumerable.Repeat(1, Settings.COLS).ToArray(),
            Flush = Enumerable.Repeat(false, Settings.COLS).ToArray(),
            Spawns = new Dictionary<(int, int), Cell>
            {
                [(0, 0)] = Grid.Feat(Settings.F_XSPIN, 2, new FP
                {
                    FeatId = "EXTRA_SPIN",
                }),
                [(0, 1)] = Grid.Feat(Settings.F_PRUP, 3, new FP
                {
                    FeatId = "PRIZE_UPGRADE",
                    PrupSym = 2,
                    PrupTier = 1,
                }),
            },
        };

    private static SpinPlan SpinWithExtraAndWheel(int spin) =>
        new()
        {
            Spin = spin,
            Board = FilledBoard(1),
            Push = Enumerable.Repeat(1, Settings.COLS).ToArray(),
            Flush = Enumerable.Repeat(false, Settings.COLS).ToArray(),
            Spawns = new Dictionary<(int, int), Cell>
            {
                [(0, 0)] = Grid.Feat(Settings.F_XSPIN, 2, new FP
                {
                    FeatId = "EXTRA_SPIN",
                }),
                [(0, 1)] = Grid.Feat(Settings.F_WHEEL, 3, new FP
                {
                    FeatId = "WHEEL",
                    WheelSym = 2,
                    WheelStack = 2,
                }),
            },
        };

    private static SpinPlan SpinWithPrizeUpgradeAndWheel(int spin) =>
        new()
        {
            Spin = spin,
            Board = FilledBoard(1),
            Push = Enumerable.Repeat(1, Settings.COLS).ToArray(),
            Flush = Enumerable.Repeat(false, Settings.COLS).ToArray(),
            Spawns = new Dictionary<(int, int), Cell>
            {
                [(0, 0)] = Grid.Feat(Settings.F_PRUP, 1, new FP
                {
                    FeatId = "PRIZE_UPGRADE",
                    PrupSym = 2,
                    PrupTier = 1,
                }),
                [(0, 1)] = Grid.Feat(Settings.F_WHEEL, 3, new FP
                {
                    FeatId = "WHEEL",
                    WheelSym = 2,
                    WheelStack = 2,
                }),
            },
        };

    private static Cell?[,] FilledBoard(int sym)
    {
        var board = new Cell?[Settings.ROWS, Settings.COLS];
        for (var row = 0; row < Settings.ROWS; row++)
        {
            for (var col = 0; col < Settings.COLS; col++)
                board[row, col] = Grid.Norm(sym);
        }
        return board;
    }

    private static IReadOnlyList<PrizeLadderRow> StandardRows() =>
        new[]
        {
            new PrizeLadderRow { Target = 20, Tiers = new decimal[] { 1, 2, 5 } },
            new PrizeLadderRow { Target = 20, Tiers = new decimal[] { 2, 5, 10 } },
            new PrizeLadderRow { Target = 20, Tiers = new decimal[] { 5, 10, 25 } },
            new PrizeLadderRow { Target = 25, Tiers = new decimal[] { 10, 25, 100 } },
            new PrizeLadderRow { Target = 25, Tiers = new decimal[] { 100, 250, 1000 } },
            new PrizeLadderRow { Target = 30, Tiers = new decimal[] { 10000 } },
        };

    private static IReadOnlyList<PrizeLadderRow> CustomBundleRows() =>
        new[]
        {
            new PrizeLadderRow { Target = 20, Tiers = new decimal[] { 1, 2, 5 } },
            new PrizeLadderRow { Target = 20, Tiers = new decimal[] { 2, 4, 8 } },
            new PrizeLadderRow { Target = 20, Tiers = new decimal[] { 5, 10, 25 } },
            new PrizeLadderRow { Target = 25, Tiers = new decimal[] { 10, 20, 50 } },
            new PrizeLadderRow { Target = 25, Tiers = new decimal[] { 100, 200, 500 } },
            new PrizeLadderRow { Target = 30, Tiers = new decimal[] { 10000 } },
        };

    private static int MixedSeed(int seed, int index)
    {
        unchecked
        {
            uint x = (uint)seed;
            x ^= (uint)(index + 1) * 0x9E3779B9u;
            x ^= x >> 16;
            x *= 0x85EBCA6Bu;
            x ^= x >> 13;
            x *= 0xC2B2AE35u;
            x ^= x >> 16;
            return (int)x;
        }
    }

    private static Dictionary<int, IReadOnlyDictionary<int, decimal>> PrizeValues(int maxSym, int tiers)
    {
        var result = new Dictionary<int, IReadOnlyDictionary<int, decimal>>();
        for (var sym = 1; sym <= maxSym; sym++)
        {
            result[sym] = Enumerable.Range(0, tiers)
                .ToDictionary(tier => tier, tier => (decimal)((sym * 10) + tier));
        }
        return result;
    }
}
