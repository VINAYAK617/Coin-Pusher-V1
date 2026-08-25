namespace CoinPusherEngine;

internal enum PpsMathInputStatus
{
    Valid,
    MissingPrizeLadder,
    MissingCombination,
    InvalidCombination,
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

/// <summary>
/// Resolves the exact ALW PPS prize row. Spin count and winning completion turn
/// are handled by the shared forward winning-round planner, configured from the
/// same PPS spin-rule table.
/// </summary>
internal sealed class PpsMathInputResolver
{
    private readonly ICustomProfileSettings _settings;

    internal PpsMathInputResolver(ICustomProfileSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    internal PpsMathInputResult Resolve(
        IReadOnlyList<decimal> prizeAmounts,
        int seed,
        int? exactCombinationId = null)
    {
        if (_settings.PrizeLadderRows == null || _settings.PrizeLadderRows.Count == 0)
        {
            return PpsMathInputResult.Fail(
                PpsMathInputStatus.MissingPrizeLadder,
                "settings.PrizeLadderRows is empty");
        }

        var totalPrize = prizeAmounts.Sum();
        if (totalPrize == 0m)
        {
            if (exactCombinationId.HasValue)
            {
                return PpsMathInputResult.Fail(
                    PpsMathInputStatus.InvalidCombination,
                    $"PPS combination #{exactCombinationId.Value} cannot be requested for a zero-prize ticket");
            }

            return PpsMathInputResult.Ok(
                "resolved PPS no-win input",
                new BundleResult
                {
                    Input = BuildNoWinInput(),
                    Entries = new List<BundleEntry>(),
                    Covered = prizeAmounts.ToList(),
                    Skipped = new List<decimal>(),
                });
        }

        var candidates = _settings.PpsCombinations
            .Where(combination => exactCombinationId.HasValue
                ? combination.Id == exactCombinationId.Value
                : combination.TotalPrize == totalPrize)
            .OrderBy(combination => combination.Id)
            .ToArray();
        if (candidates.Length == 0)
        {
            return PpsMathInputResult.Fail(
                PpsMathInputStatus.MissingCombination,
                exactCombinationId.HasValue
                    ? $"PPS combination #{exactCombinationId.Value} is not configured"
                    : $"no PPS combination is configured for total prize {totalPrize}");
        }

        var combination = candidates[new Random(seed).Next(candidates.Length)];
        if (combination.TotalPrize != totalPrize)
        {
            return PpsMathInputResult.Fail(
                PpsMathInputStatus.InvalidCombination,
                $"PPS combination #{combination.Id} totals {combination.TotalPrize}, requested prize is {totalPrize}");
        }

        var validationError = ValidateCombination(combination);
        if (validationError != null)
            return PpsMathInputResult.Fail(PpsMathInputStatus.InvalidCombination, validationError);

        var entries = BuildEntries(combination);
        return PpsMathInputResult.Ok(
            $"resolved PPS combination #{combination.Id} for total prize {totalPrize}",
            new BundleResult
            {
                Input = BuildInput(combination, entries),
                Entries = entries,
                Covered = prizeAmounts.ToList(),
                Skipped = new List<decimal>(),
            });
    }

    private string? ValidateCombination(PpsPrizeCombination combination)
    {
        if (combination.Components == null || combination.Components.Count == 0)
            return $"PPS combination #{combination.Id} has no components";

        var duplicate = combination.Components
            .GroupBy(component => component.SymbolId)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
            return $"PPS combination #{combination.Id} repeats symbol {duplicate.Key}";

        var resolved = combination.Components
            .Select(ComponentAmount)
            .ToArray();
        if (resolved.Any(amount => !amount.HasValue))
            return $"PPS combination #{combination.Id} contains a symbol/tier outside the configured prize ladder";

        var total = resolved.Sum(amount => amount!.Value);
        return total == combination.TotalPrize
            ? null
            : $"PPS combination #{combination.Id} components total {total}, expected {combination.TotalPrize}";
    }

    private List<BundleEntry> BuildEntries(PpsPrizeCombination combination) =>
        combination.Components
            .OrderBy(component => component.SymbolId)
            .Select(component =>
            {
                var row = _settings.PrizeLadderRows[component.SymbolId - 1];
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
        IReadOnlyList<BundleEntry> entries)
    {
        var prizeTiers = entries
            .Where(entry => entry.Tier > 0)
            .ToDictionary(entry => entry.Sym, entry => entry.Tier);
        var prizeUpgradeCount = entries.Sum(entry => Math.Max(0, entry.Tier));
        var required = prizeUpgradeCount > 0
            ? new Dictionary<string, int> { ["PRIZE_UPGRADE"] = prizeUpgradeCount }
            : new Dictionary<string, int>();

        return new MathInput
        {
            Targets = entries.ToDictionary(entry => entry.Sym, entry => entry.Target),
            BaseSpins = _settings.BASE_SPINS,
            Required = required,
            PrizeTiers = prizeTiers.Count > 0 ? prizeTiers : null,
            PrizeValues = BuildPrizeValues(),
            MaxSym = _settings.PrizeLadderRows.Count,
            PpsCombinationId = combination.Id,
            PpsTotalPrize = combination.TotalPrize,
        };
    }

    private MathInput BuildNoWinInput() =>
        new()
        {
            Targets = new Dictionary<int, int>(),
            BaseSpins = _settings.BASE_SPINS,
            PrizeValues = BuildPrizeValues(),
            MaxSym = _settings.PrizeLadderRows.Count,
            PpsCombinationId = null,
            PpsTotalPrize = 0m,
        };

    private Dictionary<int, IReadOnlyDictionary<int, decimal>> BuildPrizeValues()
    {
        var values = new Dictionary<int, IReadOnlyDictionary<int, decimal>>();
        for (var symbol = 1; symbol <= _settings.PrizeLadderRows.Count; symbol++)
        {
            values[symbol] = _settings.PrizeLadderRows[symbol - 1].Tiers
                .Select((amount, tier) => (amount, tier))
                .ToDictionary(item => item.tier, item => item.amount);
        }

        return values;
    }

    private decimal? ComponentAmount(PpsPrizeComponent component)
    {
        if (component.SymbolId < 1 || component.SymbolId > _settings.PrizeLadderRows.Count)
            return null;

        var tiers = _settings.PrizeLadderRows[component.SymbolId - 1].Tiers;
        return component.Tier >= 0 && component.Tier < tiers.Count
            ? tiers[component.Tier]
            : null;
    }
}
