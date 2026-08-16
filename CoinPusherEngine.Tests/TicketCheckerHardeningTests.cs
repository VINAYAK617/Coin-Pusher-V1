using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace CoinPusherEngine.Tests;

[TestClass]
public sealed class TicketCheckerHardeningTests
{
    private static readonly decimal[][] PrizeCases =
    {
        Array.Empty<decimal>(),
        new decimal[] { 1 },
        new decimal[] { 2, 5 },
        new decimal[] { 10, 25, 100 },
        new decimal[] { 10000 },
        new decimal[] { 1, 2, 5, 10, 100, 10000 },
    };

    [TestMethod]
    public void CheckerRejectsTargetedProductionRuleCorruptions()
    {
        var settings = TestSettings.Default;
        var cases = new (string Name, Func<TicketSerializer.TicketDto> Ticket, Action<TicketSerializer.TicketDto> Mutate, string? Category)[]
        {
            ("starting board contains feature id", AnyTicket, t => t.StartingBoard[0][0].Id = settings.F_WHEEL, "Schema"),
            ("starting board contains zero id", AnyTicket, t => t.StartingBoard[0][0].Id = 0, "Schema"),
            ("pusher array contains null item", AnyTicket, t => t.Turns[0].Pushers[0] = null!, "Checker"),
            ("spawn array contains null item", AnyTicket, t => t.Turns[0].Spawns[0] = null!, "Checker"),
            ("spawn position outside board", AnyTicket, t => t.Turns[0].Spawns[0].Pos = settings.ROWS * settings.COLS, "Geometry"),
            ("duplicate spawn position", AnyTicket, DuplicateSpawnPosition, "Geometry"),
            ("spawn removed", AnyTicket, RemoveFirstSpawn, "Geometry"),
            ("normal pusher below min", AnyTicket, t => t.Turns[0].Pushers[0].PushValue = 0, "Geometry"),
            ("normal spawn uses pusher-only id", AnyTicket, SetNormalSpawnToFlushId, "Schema"),
            ("declared win target changed", WinningTicket, t => t.WinInfo.WinSymbols[0].Target++, "Payout"),
            ("near-miss threshold made impossible", AnyTicketWithNearMiss, BreakNearMissThreshold, "WinInfo"),
            ("TotalSpins no longer matches turns", AnyTicket, t => t.WinInfo.TotalSpins++, "SpinCount"),
            ("board feature placed on final turn", AnyTicket, AddFinalTurnWheel, "Feature"),
            ("FLUSH/PUSH placed on final turn", AnyTicket, AddFinalTurnFlush, "Feature"),
            ("EXTRA_SPIN removed but bonus turn remains", ExtraSpinTicket, RemoveFirstExtraSpinToken, "Feature"),
            ("EXTRA_SPIN has invalid ConvertToId", ExtraSpinTicket, BreakFirstExtraSpinConvert, "Schema"),
            ("ReTrigger has more than one child", ExtraSpinTicket, AddTwoRetriggerChildren, "Schema"),
            ("WHEEL stack value outside range", WheelTicket, t => FirstFeature(t, settings.F_WHEEL).WheelStackValue = settings.MAX_WHEEL_STACK_VALUE + 1, "Schema"),
            ("WHEEL symbol points at feature id", WheelTicket, t => FirstFeature(t, settings.F_WHEEL).WheelSymbolId = settings.F_XSPIN, "Schema"),
            ("PRIZE_UPGRADE missing upgrade value", PrizeUpgradeTicket, t => FirstFeature(t, settings.F_PRUP).UpgradePrizeValue = null, "Schema"),
            ("framework CashWin mismatch", FrameworkWinningTicket, t => { }, "Framework"),
        };

        foreach (var testCase in cases)
        {
            if (testCase.Name == "framework CashWin mismatch")
            {
                var gameData = WinningTicket();
                var frameworkTicket = FrameworkTicket(gameData, ExpectedCashWin(gameData) + 1m);
                var frameworkReport = new TicketChecker(settings).CheckTicket(frameworkTicket);
                AssertRejected(frameworkReport, testCase.Name, testCase.Category);
                continue;
            }

            var ticket = Clone(testCase.Ticket());
            AssertValid(ticket, testCase.Name);

            testCase.Mutate(ticket);

            var report = new TicketChecker(settings).CheckTicket(ticket);
            AssertRejected(report, testCase.Name, testCase.Category);
        }
    }

    [TestMethod]
    public void CheckerMutationFuzzRejectsDeterministicRandomCorruptions()
    {
        var rng = new Random(20260815);
        var tickets = Enumerable.Range(0, 24)
            .Select(i => GenerateValid(PrizeCases[i % PrizeCases.Length], 50000 + i * 37))
            .ToArray();

        for (var i = 0; i < 160; i++)
        {
            var ticket = Clone(tickets[i % tickets.Length]);
            var mutationName = ApplyFuzzMutation(ticket, rng);

            var report = new TicketChecker(TestSettings.Default).CheckTicket(ticket);

            AssertRejected(report, $"fuzz {i}: {mutationName}", expectedCategory: null);
        }
    }

    private static string ApplyFuzzMutation(TicketSerializer.TicketDto ticket, Random rng)
    {
        switch (rng.Next(12))
        {
            case 0:
                ticket.StartingBoard[rng.Next(TestSettings.Default.ROWS)][rng.Next(TestSettings.Default.COLS)].Id = 0;
                return "starting board zero id";
            case 1:
                ticket.Turns[0].Pushers[0] = null!;
                return "null pusher item";
            case 2:
                ticket.Turns[0].Spawns[0] = null!;
                return "null spawn item";
            case 3:
                RemoveFirstSpawn(ticket);
                return "spawn removed";
            case 4:
                DuplicateSpawnPosition(ticket);
                return "duplicate spawn position";
            case 5:
                ticket.Turns[0].Pushers[0].PushValue = 0;
                return "invalid pusher value";
            case 6:
                ticket.WinInfo.TotalSpins += 1;
                return "TotalSpins changed";
            case 7:
                if (ticket.WinInfo.WinSymbols.Length > 0)
                    ticket.WinInfo.WinSymbols[0].Target += 1;
                else
                    BreakNearMissThreshold(ticket);
                return "payout declaration changed";
            case 8:
                SetNormalSpawnToFlushId(ticket);
                return "normal spawn set to FLUSH id";
            case 9:
                var feature = AllFeatures(ticket).FirstOrDefault();
                if (feature != null)
                {
                    feature.ConvertToId = TestSettings.Default.F_WHEEL;
                    feature.ReTrigger = Array.Empty<TicketSerializer.FeatureDto>();
                }
                else
                {
                    AddFinalTurnWheel(ticket);
                }
                return "feature convert target changed";
            case 10:
                var wheel = AllFeatures(ticket).FirstOrDefault(feature => feature.FeatureId == TestSettings.Default.F_WHEEL);
                if (wheel != null)
                    wheel.WheelStackValue = TestSettings.Default.MAX_WHEEL_STACK_VALUE + 1;
                else
                    ticket.StartingBoard[0][0].Id = TestSettings.Default.F_WHEEL;
                return "wheel payload or starting board feature changed";
            default:
                AddFinalTurnFlush(ticket);
                return "final turn flush";
        }
    }

    private static TicketSerializer.TicketDto AnyTicket() =>
        GenerateValid(Array.Empty<decimal>(), 41001);

    private static TicketSerializer.TicketDto WinningTicket() =>
        GenerateValid(new decimal[] { 1, 2, 5 }, 41002);

    private static TicketSerializer.TicketDto AnyTicketWithNearMiss()
    {
        for (var seed = 41003; seed < 41100; seed++)
        {
            var ticket = GenerateValid(Array.Empty<decimal>(), seed);
            if (ticket.WinInfo.NonWinSymbols.Length > 0)
                return ticket;
        }

        Assert.Fail("could not generate ticket with near-miss declaration");
        throw new InvalidOperationException();
    }

    private static TicketSerializer.TicketDto ExtraSpinTicket() =>
        TicketWithFeature(TestSettings.Default.F_XSPIN, new decimal[] { 1, 2, 5, 10, 100, 10000 }, 42000);

    private static TicketSerializer.TicketDto WheelTicket() =>
        TicketWithFeature(TestSettings.Default.F_WHEEL, new decimal[] { 1, 2, 5, 10, 100, 10000 }, 43000);

    private static TicketSerializer.TicketDto PrizeUpgradeTicket() =>
        TicketWithFeature(TestSettings.Default.F_PRUP, new decimal[] { 10, 25, 100 }, 44000);

    private static TicketSerializer.TicketDto FrameworkWinningTicket() =>
        WinningTicket();

    private static TicketSerializer.TicketDto TicketWithFeature(int featureId, decimal[] prizes, int seedStart)
    {
        var settings = FeatureRichSettings();
        var candidatePrizes = new[]
        {
            prizes,
            new decimal[] { 1, 2, 5, 10, 100, 10000 },
            new decimal[] { 10, 25, 100 },
            new decimal[] { 10000 },
            Array.Empty<decimal>(),
        };

        foreach (var prizeCase in candidatePrizes)
        {
            for (var seed = seedStart; seed < seedStart + 500; seed++)
            {
                var ticket = GenerateValid(prizeCase, seed, settings);
                if (AllFeatures(ticket).Any(feature => feature.FeatureId == featureId))
                    return ticket;
            }
        }

        Assert.Fail($"could not generate ticket with feature {featureId}");
        throw new InvalidOperationException();
    }

    private static ICustomProfileSettings FeatureRichSettings() =>
        new DefaultProfileSettings
        {
            WheelFeatureConfig = (1.0, 3, 1, 98, 1),
            FlushFeatureConfig = (1.0, 5, 1, 99, 2),
            ExtraSpinFeatureConfig = (1.0, 5, 1, 97, 3),
            PrizeUpgradeFeatureConfig = (1.0, 4, 1, 97, 4),
            POptionalFeatureTicket = 1.0,
            POptionalTicketWheel = 1.0,
            POptionalTicketFlush = 1.0,
            POptionalTicketPrizeUpgrade = 1.0,
            PWheelOptional = 1.0,
            PFlushOptional = 1.0,
            PNonWinPrizeUpgrade = 1.0,
        };

    private static TicketSerializer.TicketDto GenerateValid(IReadOnlyList<decimal> prizes, int seed, ICustomProfileSettings? settings = null)
    {
        var actualSettings = settings ?? TestSettings.Default;
        var result = new CoinPusherTicketJsonGenerator(actualSettings).Generate(prizes, seed);
        Assert.IsTrue(result.IsValid, $"generation failed seed={seed}, prizes=[{string.Join(",", prizes)}]: {result.Detail}");
        Assert.IsNotNull(result.Ticket, $"generator returned no ticket seed={seed}");

        var ticket = Clone(result.Ticket!);
        AssertValid(ticket, $"generated seed={seed}", actualSettings);
        return ticket;
    }

    private static void DuplicateSpawnPosition(TicketSerializer.TicketDto ticket)
    {
        var turn = ticket.Turns.First(item => item.Spawns.Length >= 2);
        turn.Spawns[1].Pos = turn.Spawns[0].Pos;
    }

    private static void RemoveFirstSpawn(TicketSerializer.TicketDto ticket)
    {
        var turn = ticket.Turns.First(item => item.Spawns.Length > 0);
        turn.Spawns = turn.Spawns.Skip(1).ToArray();
    }

    private static void SetNormalSpawnToFlushId(TicketSerializer.TicketDto ticket)
    {
        var spawn = ticket.Turns
            .SelectMany(turn => turn.Spawns)
            .FirstOrDefault(spawn => spawn.Feature == null);
        if (spawn == null)
        {
            ticket.StartingBoard[0][0].Id = TestSettings.Default.F_FLUSH_ID;
            return;
        }

        spawn.Id = TestSettings.Default.F_FLUSH_ID;
    }

    private static void BreakNearMissThreshold(TicketSerializer.TicketDto ticket)
    {
        if (ticket.WinInfo.NonWinSymbols.Length == 0)
        {
            ticket.WinInfo.TotalSpins++;
            return;
        }

        var nearMiss = ticket.WinInfo.NonWinSymbols[0];
        nearMiss.MaxThreshold = nearMiss.MinTarget;
    }

    private static void AddFinalTurnWheel(TicketSerializer.TicketDto ticket)
    {
        var spawn = ticket.Turns[^1].Spawns.First(item => item.Feature == null);
        var convertTo = spawn.Id > 0 && !TestSettings.Default.IsFeat(spawn.Id)
            ? spawn.Id
            : TestSettings.Default.F_COIN;
        spawn.Id = TestSettings.Default.F_WHEEL;
        spawn.Feature = new TicketSerializer.FeatureDto
        {
            FeatureId = TestSettings.Default.F_WHEEL,
            ConvertToId = convertTo,
            WheelSymbolId = convertTo,
            WheelStackValue = TestSettings.Default.MIN_WHEEL_STACK_VALUE,
            ReTrigger = Array.Empty<TicketSerializer.FeatureDto>(),
        };
    }

    private static void AddFinalTurnFlush(TicketSerializer.TicketDto ticket)
    {
        var pusher = ticket.Turns[^1].Pushers[0];
        pusher.PushValue = TestSettings.Default.ROWS;
        pusher.FeatureId = TestSettings.Default.F_FLUSH_ID;
    }

    private static void RemoveFirstExtraSpinToken(TicketSerializer.TicketDto ticket)
    {
        var spawn = ticket.Turns
            .SelectMany(turn => turn.Spawns)
            .First(item => item.Feature?.FeatureId == TestSettings.Default.F_XSPIN);
        spawn.Id = spawn.Feature!.ConvertToId;
        spawn.Feature = null;
    }

    private static void BreakFirstExtraSpinConvert(TicketSerializer.TicketDto ticket)
    {
        var feature = FirstFeature(ticket, TestSettings.Default.F_XSPIN);
        feature.ConvertToId = TestSettings.Default.F_WHEEL;
        feature.ReTrigger = Array.Empty<TicketSerializer.FeatureDto>();
    }

    private static void AddTwoRetriggerChildren(TicketSerializer.TicketDto ticket)
    {
        var feature = FirstFeature(ticket, TestSettings.Default.F_XSPIN);
        feature.ConvertToId = TestSettings.Default.F_PRUP;
        feature.ReTrigger = new[]
        {
            new TicketSerializer.FeatureDto
            {
                FeatureId = TestSettings.Default.F_PRUP,
                ConvertToId = TestSettings.Default.F_COIN,
                UpgradeSymbolId = TestSettings.Default.F_COIN,
                UpgradePrizeValue = 1m,
                ReTrigger = Array.Empty<TicketSerializer.FeatureDto>(),
            },
            new TicketSerializer.FeatureDto
            {
                FeatureId = TestSettings.Default.F_XSPIN,
                ConvertToId = TestSettings.Default.F_COIN,
                ReTrigger = Array.Empty<TicketSerializer.FeatureDto>(),
            },
        };
    }

    private static TicketSerializer.FeatureDto FirstFeature(TicketSerializer.TicketDto ticket, int featureId)
    {
        var feature = AllFeatures(ticket).FirstOrDefault(item => item.FeatureId == featureId);
        Assert.IsNotNull(feature, $"ticket must contain feature {featureId}");
        return feature!;
    }

    private static IEnumerable<TicketSerializer.FeatureDto> AllFeatures(TicketSerializer.TicketDto ticket) =>
        ticket.Turns
            .SelectMany(turn => turn.Spawns)
            .Where(spawn => spawn.Feature != null)
            .SelectMany(spawn => Flatten(spawn.Feature!));

    private static IEnumerable<TicketSerializer.FeatureDto> Flatten(TicketSerializer.FeatureDto feature)
    {
        yield return feature;
        foreach (var child in feature.ReTrigger ?? Array.Empty<TicketSerializer.FeatureDto>())
        {
            foreach (var nested in Flatten(child))
                yield return nested;
        }
    }

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

    private static decimal ExpectedCashWin(TicketSerializer.TicketDto ticket)
    {
        var tiers = (ticket.WinInfo.PrizeTiers ?? Array.Empty<TicketSerializer.PrizeTierDto>())
            .ToDictionary(tier => tier.SymId, tier => tier.Tier);

        return ticket.WinInfo.WinSymbols.Sum(win =>
        {
            var tier = tiers.GetValueOrDefault(win.Id);
            return TestSettings.Default.PrizeLadderRows[win.Id - 1].Tiers[tier];
        });
    }

    private static TicketSerializer.TicketDto Clone(TicketSerializer.TicketDto ticket) =>
        JsonConvert.DeserializeObject<TicketSerializer.TicketDto>(JsonConvert.SerializeObject(ticket))!;

    private static void AssertValid(
        TicketSerializer.TicketDto ticket,
        string label,
        ICustomProfileSettings? settings = null)
    {
        var report = new TicketChecker(settings ?? TestSettings.Default).CheckTicket(ticket);
        Assert.IsTrue(report.IsValid, $"{label} should be valid before mutation. Failures: {Failures(report)}");
    }

    private static void AssertRejected(TicketChecker.Report report, string mutationName, string? expectedCategory)
    {
        var failures = report.Checks
            .Where(check => check.Result == TicketChecker.Status.Fail)
            .ToArray();

        Assert.IsTrue(failures.Length > 0,
            $"{mutationName} unexpectedly passed TicketChecker");

        if (expectedCategory != null)
        {
            Assert.IsTrue(failures.Any(check => check.Category == expectedCategory),
                $"{mutationName} failed, but not in expected category {expectedCategory}. Failures: {Failures(report)}");
        }
    }

    private static string Failures(TicketChecker.Report report) =>
        string.Join(" | ", report.Checks
            .Where(check => check.Result == TicketChecker.Status.Fail)
            .Select(check => $"{check.Category}/{check.Name}: {check.Detail}"));
}
