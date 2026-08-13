namespace CoinPusherEngine;

internal enum PpsMathInputStatus
{
    Valid,
    MissingPrizeLadder,
    MissingSpinRule,
    MissingCombination,
    InvalidCombination,
    InvalidSpinRule,
}

internal sealed class PpsMathInputResult
{
    private PpsMathInputResult(
        PpsMathInputStatus status,
        string detail,
        BundleResult? bundle)
    {
        Status = status;
        Detail = detail;
        Bundle = bundle;
    }

    internal PpsMathInputStatus Status { get; }
    internal string Detail { get; }
    internal BundleResult? Bundle { get; }
    internal bool IsValid => Status == PpsMathInputStatus.Valid;

    internal static PpsMathInputResult Ok(string detail, BundleResult bundle) =>
        new(PpsMathInputStatus.Valid, detail, bundle);

    internal static PpsMathInputResult Fail(PpsMathInputStatus status, string detail) =>
        new(status, detail, null);
}

internal sealed class PpsMathInputResolver
{
    internal PpsMathInputResult Resolve(
        IReadOnlyList<decimal> prizeAmounts,
        int seed)
    {
        if (Settings.PrizeLadderRows == null || Settings.PrizeLadderRows.Count == 0)
            return PpsMathInputResult.Fail(PpsMathInputStatus.MissingPrizeLadder, "settings.PrizeLadderRows is empty");

        var totalPrize = prizeAmounts.Sum();
        var rng = new Random(seed);
        var spin = ResolveSpinRule(totalPrize, rng);
        if (!spin.IsValid) return spin.Result!;

        if (totalPrize == 0m)
        {
            return PpsMathInputResult.Ok(
                "resolved PPS no-win input",
                new BundleResult
                {
                    Input = BuildNoWinInput(spin.ExtraGoCount, spin.WinCompletionTurn),
                    Entries = new List<BundleEntry>(),
                    Covered = prizeAmounts.ToList(),
                    Skipped = new List<decimal>(),
                });
        }

        var candidates = Settings.PpsCombinations
            .Where(combination => combination.TotalPrize == totalPrize)
            .OrderBy(combination => combination.Id)
            .ToArray();
        if (candidates.Length == 0)
        {
            return PpsMathInputResult.Fail(
                PpsMathInputStatus.MissingCombination,
                $"no PPS combination is configured for total prize {totalPrize}");
        }

        var combination = candidates[rng.Next(candidates.Length)];
        var validation = ValidateCombination(combination);
        if (!validation.IsValid) return validation.Result!;

        var entries = BuildEntries(combination);
        var input = BuildInput(combination, entries, spin.ExtraGoCount, spin.WinCompletionTurn);
        return PpsMathInputResult.Ok(
            $"resolved PPS combination #{combination.Id} for total prize {totalPrize}",
            new BundleResult
            {
                Input = input,
                Entries = entries,
                Covered = prizeAmounts.ToList(),
                Skipped = new List<decimal>(),
            });
    }

    private SpinSelectionResult ResolveSpinRule(decimal totalPrize, Random rng)
    {
        var rule = Settings.PpsSpinRules
            .FirstOrDefault(candidate => candidate.Matches(totalPrize));
        if (rule == null)
        {
            return SpinSelectionResult.Fail(PpsMathInputResult.Fail(
                PpsMathInputStatus.MissingSpinRule,
                $"no PPS spin rule matches total prize {totalPrize}"));
        }

        var maxExtraGo = Math.Min(
            rule.MaxExtraGo,
            Math.Min(Settings.ExtraSpinFeatureConfig.Max, Settings.MAX_SPINS - Settings.BASE_SPINS));
        var minExtraGo = Math.Max(0, rule.MinExtraGo);
        if (minExtraGo > maxExtraGo)
        {
            return SpinSelectionResult.Fail(PpsMathInputResult.Fail(
                PpsMathInputStatus.InvalidSpinRule,
                $"PPS extra-go range {rule.MinExtraGo}..{rule.MaxExtraGo} is outside configured envelope"));
        }

        if (totalPrize == 0m)
        {
            var extra = PickInclusive(rng, minExtraGo, maxExtraGo);
            return SpinSelectionResult.Ok(extra, null);
        }

        if (!rule.MinWinningTurn.HasValue || !rule.MaxWinningTurn.HasValue)
        {
            return SpinSelectionResult.Fail(PpsMathInputResult.Fail(
                PpsMathInputStatus.InvalidSpinRule,
                $"PPS winning turn range is missing for total prize {totalPrize}"));
        }

        var legalExtras = Enumerable.Range(minExtraGo, maxExtraGo - minExtraGo + 1)
            .Where(extra => CompatibleWinningTurns(rule, extra).Length > 0)
            .ToArray();
        if (legalExtras.Length == 0)
        {
            return SpinSelectionResult.Fail(PpsMathInputResult.Fail(
                PpsMathInputStatus.InvalidSpinRule,
                $"PPS winning turns {rule.MinWinningTurn}..{rule.MaxWinningTurn} cannot fit extra-go range {minExtraGo}..{maxExtraGo}"));
        }

        var chosenExtra = legalExtras[rng.Next(legalExtras.Length)];
        var winningTurns = CompatibleWinningTurns(rule, chosenExtra);
        return SpinSelectionResult.Ok(
            chosenExtra,
            winningTurns[rng.Next(winningTurns.Length)]);
    }

    private static int[] CompatibleWinningTurns(PpsSpinRule rule, int extraGoCount)
    {
        if (!rule.MinWinningTurn.HasValue || !rule.MaxWinningTurn.HasValue)
            return Array.Empty<int>();

        var totalTurns = Settings.BASE_SPINS + extraGoCount;
        var minTurn = Math.Max(1, rule.MinWinningTurn.Value);
        var maxTurn = Math.Min(totalTurns, rule.MaxWinningTurn.Value);
        if (minTurn > maxTurn) return Array.Empty<int>();
        return Enumerable.Range(minTurn, maxTurn - minTurn + 1).ToArray();
    }

    private PpsValidationResult ValidateCombination(PpsPrizeCombination combination)
    {
        if (combination.Components == null || combination.Components.Count == 0)
        {
            return PpsValidationResult.Fail(PpsMathInputResult.Fail(
                PpsMathInputStatus.InvalidCombination,
                $"PPS combination #{combination.Id} has no components"));
        }

        var duplicate = combination.Components
            .GroupBy(component => component.SymbolId)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
        {
            return PpsValidationResult.Fail(PpsMathInputResult.Fail(
                PpsMathInputStatus.InvalidCombination,
                $"PPS combination #{combination.Id} repeats symbol {duplicate.Key}"));
        }

        var total = 0m;
        foreach (var component in combination.Components)
        {
            var amount = ComponentAmount(component);
            if (!amount.HasValue)
            {
                return PpsValidationResult.Fail(PpsMathInputResult.Fail(
                    PpsMathInputStatus.InvalidCombination,
                    $"PPS combination #{combination.Id} references symbol {component.SymbolId} tier {component.Tier}, outside the prize ladder"));
            }

            total += amount.Value;
        }

        if (total != combination.TotalPrize)
        {
            return PpsValidationResult.Fail(PpsMathInputResult.Fail(
                PpsMathInputStatus.InvalidCombination,
                $"PPS combination #{combination.Id} components total {total}, expected {combination.TotalPrize}"));
        }

        return PpsValidationResult.Ok();
    }

    private List<BundleEntry> BuildEntries(PpsPrizeCombination combination) =>
        combination.Components
            .OrderBy(component => component.SymbolId)
            .Select(component =>
            {
                var row = Settings.PrizeLadderRows[component.SymbolId - 1];
                return new BundleEntry
                {
                    Sym = component.SymbolId,
                    Target = row.Target,
                    Tier = component.Tier,
                    Amounts = new List<decimal> { row.Tiers[component.Tier] },
                };
            })
            .ToList();

    private MathInput BuildInput(
        PpsPrizeCombination combination,
        IReadOnlyList<BundleEntry> entries,
        int extraGoCount,
        int? winCompletionTurn)
    {
        var targets = entries.ToDictionary(entry => entry.Sym, entry => entry.Target);
        var prizeTiers = entries
            .Where(entry => entry.Tier > 0)
            .ToDictionary(entry => entry.Sym, entry => entry.Tier);
        var required = new Dictionary<string, int>();
        var prizeUpgradeTokens = entries.Sum(entry => Math.Max(0, entry.Tier));
        if (prizeUpgradeTokens > 0)
            required["PRIZE_UPGRADE"] = prizeUpgradeTokens;
        if (extraGoCount > 0)
            required["EXTRA_SPIN"] = extraGoCount;

        return new MathInput
        {
            Targets = targets,
            BaseSpins = Settings.BASE_SPINS,
            Required = required,
            PrizeTiers = prizeTiers.Count > 0 ? prizeTiers : null,
            PrizeValues = BuildPrizeValues(),
            MaxSym = Math.Max(Settings.PrizeLadderRows.Count, targets.Count + 2),
            WinCompletionTurn = winCompletionTurn,
            LockExtraGoCount = true,
            PpsCombinationId = combination.Id,
            PpsTotalPrize = combination.TotalPrize,
        };
    }

    private MathInput BuildNoWinInput(int extraGoCount, int? winCompletionTurn)
    {
        var required = new Dictionary<string, int>();
        if (extraGoCount > 0)
            required["EXTRA_SPIN"] = extraGoCount;

        return new MathInput
        {
            Targets = new Dictionary<int, int>(),
            BaseSpins = Settings.BASE_SPINS,
            Required = required,
            PrizeTiers = null,
            PrizeValues = BuildPrizeValues(),
            MaxSym = Math.Max(Settings.PrizeLadderRows.Count, 2),
            WinCompletionTurn = winCompletionTurn,
            LockExtraGoCount = true,
            PpsCombinationId = null,
            PpsTotalPrize = 0m,
        };
    }

    private static Dictionary<int, IReadOnlyDictionary<int, decimal>> BuildPrizeValues()
    {
        var values = new Dictionary<int, IReadOnlyDictionary<int, decimal>>();
        for (var symbol = 1; symbol <= Settings.PrizeLadderRows.Count; symbol++)
        {
            values[symbol] = Settings.PrizeLadderRows[symbol - 1].Tiers
                .Select((amount, tier) => (amount, tier))
                .ToDictionary(item => item.tier, item => item.amount);
        }

        return values;
    }

    private static decimal? ComponentAmount(PpsPrizeComponent component)
    {
        if (component.SymbolId < 1 || component.SymbolId > Settings.PrizeLadderRows.Count)
            return null;

        var tiers = Settings.PrizeLadderRows[component.SymbolId - 1].Tiers;
        if (component.Tier < 0 || component.Tier >= tiers.Count)
            return null;

        return tiers[component.Tier];
    }

    private static int PickInclusive(Random rng, int min, int max) =>
        min == max ? min : rng.Next(min, max + 1);

    private sealed class SpinSelectionResult
    {
        private SpinSelectionResult(
            int extraGoCount,
            int? winCompletionTurn,
            PpsMathInputResult? result)
        {
            ExtraGoCount = extraGoCount;
            WinCompletionTurn = winCompletionTurn;
            Result = result;
        }

        internal int ExtraGoCount { get; }
        internal int? WinCompletionTurn { get; }
        internal PpsMathInputResult? Result { get; }
        internal bool IsValid => Result == null;

        internal static SpinSelectionResult Ok(int extraGoCount, int? winCompletionTurn) =>
            new(extraGoCount, winCompletionTurn, null);

        internal static SpinSelectionResult Fail(PpsMathInputResult result) =>
            new(0, null, result);
    }

    private sealed class PpsValidationResult
    {
        private PpsValidationResult(PpsMathInputResult? result)
        {
            Result = result;
        }

        internal PpsMathInputResult? Result { get; }
        internal bool IsValid => Result == null;

        internal static PpsValidationResult Ok() => new(null);

        internal static PpsValidationResult Fail(PpsMathInputResult result) => new(result);
    }
}
