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
    public void PpsResolverBuildsExactCombinationAndPacingForTen()
    {
        var result = new ForwardMathInputResolver().Resolve(new decimal[] { 10m }, seed: 101);

        Assert.AreEqual(ForwardMathInputStatus.Valid, result.Status);
        Assert.IsNotNull(result.Bundle);
        var input = result.Bundle!.Input;

        CollectionAssert.Contains(new[] { 69, 70, 71, 72 }, input.PpsCombinationId!.Value);
        Assert.AreEqual(10m, input.PpsTotalPrize);
        Assert.IsTrue(input.LockExtraGoCount);
        Assert.IsTrue(input.Required.TryGetValue("EXTRA_SPIN", out var extraGo));
        Assert.IsTrue(extraGo is 2 or 3);
        Assert.IsTrue(input.WinCompletionTurn is >= 7 and <= 8);
        Assert.IsTrue(input.WinCompletionTurn <= Settings.BASE_SPINS + extraGo);
        Assert.AreEqual(input.Targets.Keys.Count(), input.Targets.Keys.Distinct().Count());
        Assert.AreEqual(10m, ResolvedPrizeTotal(input));
    }

    [TestMethod]
    public void PpsResolverRandomizesRowsWhenTotalHasMultipleCombinations()
    {
        var ids = Enumerable.Range(1, 80)
            .Select(seed => new ForwardMathInputResolver()
                .Resolve(new decimal[] { 10m }, seed)
                .Bundle!
                .Input
                .PpsCombinationId!.Value)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        CollectionAssert.IsSubsetOf(ids, new[] { 69, 70, 71, 72 });
        Assert.IsTrue(ids.Length > 1, "same total prize should be able to select different PPS rows");
    }

    [TestMethod]
    public void SymbolLedgerRejectsWinCompletionOnWrongTurn()
    {
        var ledger = new SymbolLedger(
            new Dictionary<int, int> { [1] = 2 },
            new Dictionary<int, int>(),
            maxSymbol: 6,
            expectedWinCompletionTurn: 7);

        Assert.AreEqual(SymbolCollectionStatus.Valid, ledger.Collect(1, collectionTurn: 6).Status);
        Assert.AreEqual(
            SymbolCollectionStatus.WouldCompleteWinOnWrongTurn,
            ledger.Collect(1, collectionTurn: 6).Status);
        Assert.AreEqual(SymbolCollectionStatus.Valid, ledger.Collect(1, collectionTurn: 7).Status);
        Assert.AreEqual(0, ledger.ValidateFinal().Count);
    }

    [TestMethod]
    public void AlwGeneratedTicketHonorsPpsCombinationAndWinningRound()
    {
        var generated = new ForwardTicketGenerator(seed: 20260813).Generate(new decimal[] { 10m });

        Assert.AreEqual(ForwardTicketGenerationStatus.Valid, generated.Status, generated.Detail);
        Assert.IsNotNull(generated.Build?.MathInput?.Bundle?.Input);
        Assert.IsNotNull(generated.AdaptedPlan?.Plan);

        var input = generated.Build!.MathInput!.Bundle!.Input;
        var plan = generated.AdaptedPlan!.Plan!;
        Assert.AreEqual(10m, ResolvedPrizeTotal(input));
        Assert.AreEqual(input.Required.GetValueOrDefault("EXTRA_SPIN"), plan.TotalSpins - Settings.BASE_SPINS);
        Assert.AreEqual(input.WinCompletionTurn, ActualWinCompletionTurn(plan));
    }

    [TestMethod]
    public void AlwGeneratedTicketsHonorRepresentativePpsTotals()
    {
        var totals = new[] { 3m, 5m, 8m, 10m, 13m, 20m, 50m, 75m, 150m, 5000m, 250000m };

        foreach (var total in totals)
        {
            for (var seed = 1; seed <= 5; seed++)
            {
                var generated = new CoinPusherTicketGenerator().Generate(new[] { total }, seed: 30000 + (seed * 97) + (int)total);
                Assert.AreEqual(CoinPusherTicketGenerationStatus.Valid, generated.Status, $"{total}: {generated.Detail}");

                var plan = generated.Plan!;
                var rule = AlwMoneyMachinePps.SpinRules.First(item => item.Matches(total));
                var extraGo = plan.TotalSpins - Settings.BASE_SPINS;
                var completionTurn = ActualWinCompletionTurn(plan);
                Assert.AreEqual(total, ResolvedPlanPrizeTotal(plan), $"total {total}");
                Assert.IsTrue(extraGo >= rule.MinExtraGo && extraGo <= rule.MaxExtraGo, $"total {total} extraGo={extraGo}");
                Assert.IsTrue(completionTurn.HasValue, $"total {total} did not complete");
                Assert.IsTrue(completionTurn.Value >= rule.MinWinningTurn!.Value
                    && completionTurn.Value <= Math.Min(rule.MaxWinningTurn!.Value, plan.TotalSpins), $"total {total} completion={completionTurn}");
            }
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

    private static GameEngine.DefaultCoinPusherSettings AlwSettings() =>
        AlwMoneyMachinePps.CreateSettings();
}
