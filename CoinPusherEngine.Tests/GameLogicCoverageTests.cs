using Newtonsoft.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CoinPusherEngine.Tests;

[TestClass]
public sealed class GameLogicCoverageTests
{
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
        foreach (var symbol in ticket.WinInfo.NonWinSymbols)
        {
            Assert.IsTrue(symbol.MinTarget >= 10 && symbol.MinTarget < symbol.MaxThreshold);
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
        var lastTurn = ticket.Turns[^1];
        Assert.IsFalse(lastTurn.Spawns.Any(spawn => spawn.Feature?.FeatureId == 12));
        Assert.IsFalse(lastTurn.Spawns.Any(spawn => spawn.Feature?.FeatureId == 11));
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
