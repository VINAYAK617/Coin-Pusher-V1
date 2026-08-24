using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CoinPusherEngine.Tests;

[TestClass]
[DoNotParallelize]
public sealed class AlwPpsTests
{
    [TestInitialize]
    public void ResetSettings() =>
        GameEngine.Engine.Settings = AlwSettings();

    [TestMethod]
    public void PpsResolverBuildsExactCombinationForTen()
    {
        var settings = GameEngine.Engine.Settings;
        var result = new ForwardMathInputResolver(settings)
            .Resolve(new decimal[] { 10m }, seed: 101);

        Assert.AreEqual(ForwardMathInputStatus.Valid, result.Status);
        Assert.IsNotNull(result.Bundle);
        var input = result.Bundle!.Input;

        CollectionAssert.Contains(new[] { 69, 70, 71, 72 }, input.PpsCombinationId!.Value);
        Assert.AreEqual(10m, input.PpsTotalPrize);
        Assert.AreEqual(input.Targets.Keys.Count(), input.Targets.Keys.Distinct().Count());
        Assert.AreEqual(10m, ResolvedPrizeTotal(input));
    }

    [TestMethod]
    public void SharedWinningPolicyUsesPpsSpinRangeForTen()
    {
        var settings = GameEngine.Engine.Settings;
        for (var seed = 1; seed <= 50; seed++)
        {
            var result = new ForwardWinningRoundPolicyPlanner(settings).Plan(10m, seed);

            Assert.IsTrue(result.IsValid, result.Detail);
            Assert.IsNotNull(result.Plan);
            Assert.IsTrue(result.Plan!.ExtraGoCount is 2 or 3);
            Assert.IsTrue(result.Plan.WinningCompletionTurn is >= 7 and <= 8);
            Assert.IsTrue(result.Plan.WinningCompletionTurn <= result.Plan.TotalTurns);
        }
    }

    [TestMethod]
    public void PpsResolverRandomizesRowsWhenTotalHasMultipleCombinations()
    {
        var settings = GameEngine.Engine.Settings;
        var ids = Enumerable.Range(1, 80)
            .Select(seed => new ForwardMathInputResolver(settings)
                .Resolve(new decimal[] { 10m }, seed)
                .Bundle!
                .Input
                .PpsCombinationId!.Value)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        CollectionAssert.IsSubsetOf(ids, new[] { 69, 70, 71, 72 });
        Assert.IsTrue(ids.Length > 1, "same total prize should select different PPS rows across seeds");
    }

    [TestMethod]
    public void EveryPpsRowResolvesItsExactConfiguredPrize()
    {
        var settings = GameEngine.Engine.Settings;
        foreach (var row in AlwMoneyMachinePps.Combinations)
        {
            var resolved = Enumerable.Range(1, 500)
                .Select(seed => new ForwardMathInputResolver(settings)
                    .Resolve(new[] { row.TotalPrize }, seed))
                .First(result => result.Bundle!.Input.PpsCombinationId == row.Id);

            Assert.AreEqual(ForwardMathInputStatus.Valid, resolved.Status, $"row {row.Id}");
            Assert.AreEqual(row.TotalPrize, ResolvedPrizeTotal(resolved.Bundle!.Input), $"row {row.Id}");
        }
    }

    [TestMethod]
    public void SymbolLedgerRejectsWinCompletionOnWrongTurn()
    {
        var ledger = new SymbolLedger(
            new Dictionary<int, int> { [1] = 2 },
            new Dictionary<int, int>(),
            maxSymbol: 6,
            GameEngine.Engine.Settings,
            winningCompletionTurn: 7);

        Assert.AreEqual(SymbolCollectionStatus.Valid, ledger.Collect(1, collectionTurn: 6).Status);
        Assert.AreEqual(
            SymbolCollectionStatus.AllWinsCompleteBeforeRequiredTurn,
            ledger.Collect(1, collectionTurn: 6).Status);
        Assert.AreEqual(SymbolCollectionStatus.Valid, ledger.Collect(1, collectionTurn: 7).Status);
        Assert.AreEqual(0, ledger.ValidateFinal().Count);
    }

    [TestMethod]
    public void AlwGeneratedTicketHonorsPpsCombinationAndWinningRound()
    {
        var generated = new ForwardTicketGenerator(GameEngine.Engine.Settings, seed: 20260813)
            .Generate(new decimal[] { 10m });

        Assert.AreEqual(ForwardTicketGenerationStatus.Valid, generated.Status, generated.Detail);
        Assert.IsNotNull(generated.Build?.MathInput?.Bundle?.Input);
        Assert.IsNotNull(generated.Build?.Objectives?.Objectives?.WinningRoundPlan);
        Assert.IsNotNull(generated.AdaptedPlan?.Plan);

        var input = generated.Build!.MathInput!.Bundle!.Input;
        var policy = generated.Build.Objectives!.Objectives!.WinningRoundPlan!;
        var plan = generated.AdaptedPlan!.Plan!;
        Assert.AreEqual(10m, ResolvedPrizeTotal(input));
        Assert.AreEqual(policy.ExtraGoCount, plan.TotalSpins - GameEngine.Engine.Settings.BASE_SPINS);
        Assert.AreEqual(policy.WinningCompletionTurn, ActualWinCompletionTurn(plan));
    }

    [TestMethod]
    public void AlwGeneratedTicketsHonorRepresentativePpsTotals()
    {
        var totals = new[] { 3m, 5m, 8m, 10m, 13m, 20m, 50m, 75m, 150m, 5000m, 250000m };

        foreach (var total in totals)
        {
            for (var seed = 1; seed <= 5; seed++)
            {
                var generated = new CoinPusherTicketGenerator(GameEngine.Engine.Settings)
                    .Generate(new[] { total }, seed: 30000 + (seed * 97) + (int)Math.Min(total, int.MaxValue));
                Assert.AreEqual(CoinPusherTicketGenerationStatus.Valid, generated.Status, $"{total}: {generated.Detail}");

                var plan = generated.Plan!;
                var rule = AlwMoneyMachinePps.SpinRules.First(item => item.Matches(total));
                var extraGo = plan.TotalSpins - GameEngine.Engine.Settings.BASE_SPINS;
                var completionTurn = ActualWinCompletionTurn(plan);
                Assert.AreEqual(total, ResolvedPlanPrizeTotal(plan), $"total {total}");
                Assert.IsTrue(extraGo >= rule.MinExtraGo && extraGo <= rule.MaxExtraGo, $"total {total} extraGo={extraGo}");
                Assert.IsTrue(completionTurn.HasValue, $"total {total} did not complete");
                Assert.IsTrue(completionTurn.Value >= rule.MinWinningTurn!.Value
                    && completionTurn.Value <= Math.Min(rule.MaxWinningTurn!.Value, plan.TotalSpins), $"total {total} completion={completionTurn}");
            }
        }
    }

    [TestMethod]
    public void AlwSettingsPassRequestValidation()
    {
        var result = new CoinPusherTicketGenerationRequestValidator(GameEngine.Engine.Settings)
            .Validate(new decimal[] { 10m });

        Assert.IsTrue(result.IsValid, $"{result.Status}: {result.Detail}");
    }

    [TestMethod]
    public void AlwSettingsDifferFromFreshForwardOnlyForPpsOwnedConfiguration()
    {
        var freshForward = new DefaultProfileSettings();
        var alw = AlwSettings();
        var ppsOwnedProperties = new HashSet<string>
        {
            nameof(DefaultProfileSettings.MAX_SPINS),
            nameof(DefaultProfileSettings.MAX_EXTRA_GO_PER_TURN),
            nameof(DefaultProfileSettings.ExtraSpinFeatureConfig),
            nameof(DefaultProfileSettings.PrizeUpgradeFeatureConfig),
            nameof(DefaultProfileSettings.FeatureConfigs),
            nameof(DefaultProfileSettings.PrizeLadderRows),
            nameof(DefaultProfileSettings.WinningRoundRules),
            nameof(DefaultProfileSettings.PpsCombinations),
            nameof(DefaultProfileSettings.PpsSpinRules),
        };

        foreach (var property in typeof(DefaultProfileSettings).GetProperties())
        {
            if (!property.CanRead || ppsOwnedProperties.Contains(property.Name))
                continue;

            Assert.AreEqual(
                Fingerprint(property.GetValue(freshForward)),
                Fingerprint(property.GetValue(alw)),
                $"ALW overrides non-PPS setting {property.Name}");
        }
    }

    private static decimal ResolvedPrizeTotal(MathInput input)
    {
        var total = 0m;
        foreach (var (symbol, _) in input.Targets)
        {
            var tier = input.PrizeTiers?.GetValueOrDefault(symbol) ?? 0;
            total += input.PrizeValues![symbol][tier];
        }

        return total;
    }

    private static int? ActualWinCompletionTurn(GamePlan plan)
    {
        var collected = plan.WinSyms.ToDictionary(symbol => symbol, _ => 0);
        foreach (var spin in plan.Spins.OrderBy(spin => spin.Spin))
        {
            foreach (var (symbol, count) in spin.Alloc)
            {
                if (collected.ContainsKey(symbol))
                    collected[symbol] += count;
            }

            if (collected.All(kv => kv.Value >= plan.Targets[kv.Key]))
                return spin.Spin;
        }

        return null;
    }

    private static decimal ResolvedPlanPrizeTotal(GamePlan plan)
    {
        var total = 0m;
        foreach (var symbol in plan.WinSyms)
        {
            var tier = plan.PrizeTiers.GetValueOrDefault(symbol);
            total += plan.PrizeValues[symbol][tier];
        }

        return total;
    }

    private static DefaultProfileSettings AlwSettings() =>
        AlwMoneyMachinePps.CreateSettings();

    private static string Fingerprint(object? value)
    {
        if (value == null)
            return "<null>";
        if (value is string text)
            return text;
        if (value is System.Collections.IEnumerable sequence)
        {
            return "[" + string.Join(",", sequence.Cast<object?>().Select(Fingerprint)) + "]";
        }

        return value.ToString() ?? value.GetType().FullName ?? "<unknown>";
    }
}
