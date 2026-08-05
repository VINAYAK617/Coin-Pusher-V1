using Newtonsoft.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CoinPusherEngine.Tests;

[TestClass]
public sealed class GameLogicCoverageTests
{
    public TestContext TestContext { get; set; } = null!;

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
                [2] = 18,
                [4] = 18,
                [5] = 13,
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
                [2] = 14,
                [4] = 16,
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
    public void TopPrizeTargetCompletesOnFinalTurn()
    {
        var input = new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = 20, [6] = 30 },
            BaseSpins = 5,
            PrizeValues = PrizeValues(6, tiers: 3),
            MaxSym = 8,
        };

        var plan = new Planner(input, seed: 10000).Plan();

        Assert.IsTrue(plan.Spins[^1].Alloc.GetValueOrDefault(6) > 0);
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
            Targets = new Dictionary<int, int> { [2] = 20, [4] = 20 },
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
        AssertNoFinalBoardFeatures(ticket);
        AssertValid(ticket);
    }

    [TestMethod]
    public void PlannerRejectsInvalidMathInputsBeforeGeneration()
    {
        Assert.ThrowsException<ArgumentException>(() => new Planner(new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = 1 },
            BaseSpins = 4,
            MaxSym = 6,
        }, seed: 1).Plan());

        Assert.ThrowsException<ArgumentException>(() => new Planner(new MathInput
        {
            Targets = new Dictionary<int, int> { [99] = 1 },
            BaseSpins = 5,
            MaxSym = 6,
        }, seed: 1).Plan());

        Assert.ThrowsException<ArgumentException>(() => new Planner(new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = 1 },
            BaseSpins = 5,
            Required = new Dictionary<string, int> { ["UNKNOWN"] = 1 },
            MaxSym = 6,
        }, seed: 1).Plan());
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
        var occupiedPos = Enumerable.Range(0, Settings.Default.ROWS * Settings.Default.COLS)
            .First(pos => !existingPositions.Contains(pos));

        firstTurn.Spawns = firstTurn.Spawns
            .Concat(new[]
            {
                new TicketSerializer.SpawnDto
                {
                    Pos = occupiedPos,
                    Id = Settings.Default.F_COIN,
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
    public void CheckerRejectsFeatureSymbolOnStartingBoard()
    {
        var ticket = PlanTicket(new MathInput
        {
            Targets = new Dictionary<int, int>(),
            BaseSpins = 5,
            MaxSym = 6,
        }, seed: 50505);

        ticket.StartingBoard[0][0].Id = Settings.Default.F_WHEEL;

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
            Targets = new Dictionary<int, int> { [2] = 20, [4] = 20 },
            BaseSpins = 5,
            MaxSym = 6,
        };
        var plan = new Planner(input, seed: 60606).Plan();
        var ticket = TicketSerializer.ToTicketObject(plan);
        var declared = ticket.WinInfo.WinSymbols.Select(w => w.Id)
            .Concat(ticket.WinInfo.NonWinSymbols.Select(w => w.Id))
            .ToHashSet();

        foreach (var (sym, count) in Sim.Run(plan))
        {
            if (count > 0 && !Settings.Default.IsFeat(sym))
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
    public void VolumeAuditWithTicketCheckerWhenRequested()
    {
        var rawCount = Environment.GetEnvironmentVariable("COINPUSHER_VOLUME_TICKETS");
        if (!int.TryParse(rawCount, out var ticketCount) || ticketCount <= 0)
        {
            TestContext.WriteLine("Set COINPUSHER_VOLUME_TICKETS to run the volume ticket checker audit.");
            return;
        }

        var seed = int.TryParse(Environment.GetEnvironmentVariable("COINPUSHER_VOLUME_SEED"), out var parsedSeed)
            ? parsedSeed
            : 20260806;
        var rng = new Random(seed);
        var failures = new List<string>();
        var warningCount = 0;
        var noWinCount = 0;
        var winningCount = 0;
        var maxTurnCount = 0;
        var featureTicketCount = 0;
        var nearMissTicketCount = 0;

        for (var i = 0; i < ticketCount; i++)
        {
            var ticketSeed = rng.Next(1, int.MaxValue);
            TicketSerializer.TicketDto ticket;
            try
            {
                var input = VolumeAuditInputs.Build(i, ticketSeed, rng);
                ticket = PlanTicket(input, ticketSeed);
            }
            catch (Exception ex)
            {
                failures.Add($"ticket {i} seed {ticketSeed}: generation failed: {ex.Message}");
                continue;
            }

            var report = TicketChecker.CheckTicket(ticket);
            if (!report.IsValid)
            {
                var errors = string.Join(" | ", report.Checks
                    .Where(c => c.Result == TicketChecker.Status.Fail)
                    .Select(c => $"{c.Category}/{c.Name}: {c.Detail}")
                    .Take(5));
                failures.Add($"ticket {i} seed {ticketSeed}: checker failed: {errors}");
            }

            warningCount += report.WarningCount;
            if (ticket.WinInfo.WinSymbols.Length == 0) noWinCount++;
            else winningCount++;
            if (ticket.WinInfo.TotalSpins == Settings.Default.MAX_SPINS) maxTurnCount++;
            if (ticket.Turns.Any(turn => turn.Spawns.Any(spawn => spawn.Feature != null))
                || ticket.Turns.Any(turn => turn.Pushers.Any(pusher => pusher.FeatureId.HasValue)))
                featureTicketCount++;
            if (ticket.WinInfo.NonWinSymbols.Any(symbol => symbol.MinTarget >= Settings.Default.NONWIN_MIN_TARGET))
                nearMissTicketCount++;
        }

        TestContext.WriteLine(
            $"volume tickets={ticketCount}, seed={seed}, failures={failures.Count}, warnings={warningCount}, " +
            $"winning={winningCount}, noWin={noWinCount}, nearMiss={nearMissTicketCount}, " +
            $"featureTickets={featureTicketCount}, maxTurns={maxTurnCount}");

        if (failures.Count > 0)
        {
            Assert.Fail(string.Join(Environment.NewLine, failures.Take(20)));
        }
    }

    private static TicketSerializer.TicketDto PlanTicket(MathInput input, int seed)
    {
        var plan = new Planner(input, seed).Plan();

        Assert.IsTrue(plan.Verified);

        var json = TicketSerializer.ToJson(plan);
        var ticket = JsonConvert.DeserializeObject<TicketSerializer.TicketDto>(json);

        Assert.IsNotNull(ticket);
        return ticket!;
    }

    private static void AssertValid(TicketSerializer.TicketDto ticket)
    {
        var report = TicketChecker.CheckTicket(ticket);
        Assert.IsTrue(report.IsValid, string.Join(Environment.NewLine,
            report.Checks
                .Where(c => c.Result == TicketChecker.Status.Fail)
                .Select(c => $"{c.Category}/{c.Name}: {c.Detail}")));
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

    private static bool ContainsFeature(TicketSerializer.FeatureDto? feature, int featureId)
    {
        if (feature == null) return false;
        if (feature.FeatureId == featureId) return true;
        return feature.ReTrigger.Any(child => ContainsFeature(child, featureId));
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
