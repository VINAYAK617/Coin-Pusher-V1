namespace CoinPusherEngine.VolumeAudit;

internal sealed class PrizeMatrixAuditCase
{
    internal PrizeMatrixAuditCase(
        decimal[] prizes,
        string sortedKey,
        bool hasUpgradeOrigin,
        int originStateCount)
    {
        Prizes = prizes;
        SortedKey = sortedKey;
        HasUpgradeOrigin = hasUpgradeOrigin;
        OriginStateCount = originStateCount;
    }

    internal decimal[] Prizes { get; }
    internal string SortedKey { get; }
    internal bool HasUpgradeOrigin { get; }
    internal int OriginStateCount { get; }
}

internal sealed class PrizeMatrixAuditInputSet
{
    internal PrizeMatrixAuditInputSet(
        IReadOnlyList<PrizeMatrixAuditCase> cases,
        int uniqueMultisetCount,
        int feasibleUniqueMultisetCount,
        int infeasibleUniqueMultisetCount,
        int originSymbolTierStateCount,
        int feasibleOriginSymbolTierStateCount,
        IReadOnlyList<string> infeasibleExamples)
    {
        Cases = cases;
        UniqueMultisetCount = uniqueMultisetCount;
        FeasibleUniqueMultisetCount = feasibleUniqueMultisetCount;
        InfeasibleUniqueMultisetCount = infeasibleUniqueMultisetCount;
        OriginSymbolTierStateCount = originSymbolTierStateCount;
        FeasibleOriginSymbolTierStateCount = feasibleOriginSymbolTierStateCount;
        InfeasibleExamples = infeasibleExamples;
    }

    internal IReadOnlyList<PrizeMatrixAuditCase> Cases { get; }
    internal int UniqueMultisetCount { get; }
    internal int FeasibleUniqueMultisetCount { get; }
    internal int InfeasibleUniqueMultisetCount { get; }
    internal int OriginSymbolTierStateCount { get; }
    internal int FeasibleOriginSymbolTierStateCount { get; }
    internal IReadOnlyList<string> InfeasibleExamples { get; }
}

internal static class PrizeMatrixAuditInputs
{
    internal static PrizeMatrixAuditInputSet Build(GameEngine.ICustomProfileSettings settings)
    {
        var bySortedAmounts = new Dictionary<string, MatrixEntry>();
        var selected = new List<(int Tier, decimal Amount)>();

        AddState();
        WalkSymbol(0);

        var feasible = new List<MatrixEntry>();
        var infeasible = new List<MatrixEntry>();
        foreach (var entry in bySortedAmounts.Values.OrderBy(entry => entry.SortedAmounts.Length).ThenBy(entry => entry.Key))
        {
            if (IsFeasiblePrizeInput(settings, entry.SortedAmounts))
                feasible.Add(entry);
            else
                infeasible.Add(entry);
        }

        var cases = feasible
            .OrderBy(entry => entry.SortedAmounts.Length)
            .ThenBy(entry => entry.Key)
            .SelectMany(ToPermutedCases)
            .ToArray();

        return new PrizeMatrixAuditInputSet(
            cases,
            bySortedAmounts.Count,
            feasible.Count,
            infeasible.Count,
            bySortedAmounts.Values.Sum(entry => entry.OriginStateCount),
            feasible.Sum(entry => entry.OriginStateCount),
            infeasible.Take(20).Select(entry => entry.Key).ToArray());

        void WalkSymbol(int rowIndex)
        {
            if (rowIndex >= settings.PrizeLadderRows.Count)
            {
                if (selected.Count > 0)
                    AddState();
                return;
            }

            WalkSymbol(rowIndex + 1);

            var tiers = settings.PrizeLadderRows[rowIndex].Tiers;
            for (var tier = 0; tier < tiers.Count; tier++)
            {
                selected.Add((tier, tiers[tier]));
                WalkSymbol(rowIndex + 1);
                selected.RemoveAt(selected.Count - 1);
            }
        }

        void AddState()
        {
            var sortedAmounts = selected
                .Select(item => item.Amount)
                .OrderBy(amount => amount)
                .ToArray();
            var key = Key(sortedAmounts);

            if (!bySortedAmounts.TryGetValue(key, out var entry))
            {
                entry = new MatrixEntry(key, sortedAmounts);
                bySortedAmounts[key] = entry;
            }

            entry.OriginStateCount++;
            entry.HasUpgradeOrigin |= selected.Any(item => item.Tier > 0);
        }
    }

    private static bool IsFeasiblePrizeInput(GameEngine.ICustomProfileSettings settings, decimal[] sortedAmounts)
    {
        try
        {
            _ = new LadderCombinator(settings.PrizeLadderRows, seed: 17)
                .Bundle(sortedAmounts);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static IEnumerable<PrizeMatrixAuditCase> ToPermutedCases(MatrixEntry entry)
    {
        foreach (var permutation in DistinctPermutations(entry.SortedAmounts))
        {
            yield return new PrizeMatrixAuditCase(
                permutation,
                entry.Key,
                entry.HasUpgradeOrigin,
                entry.OriginStateCount);
        }
    }

    private static IEnumerable<decimal[]> DistinctPermutations(decimal[] sortedAmounts)
    {
        if (sortedAmounts.Length <= 1)
        {
            yield return sortedAmounts.ToArray();
            yield break;
        }

        var used = new bool[sortedAmounts.Length];
        var current = new decimal[sortedAmounts.Length];

        foreach (var permutation in Walk(0))
            yield return permutation;

        IEnumerable<decimal[]> Walk(int depth)
        {
            if (depth == sortedAmounts.Length)
            {
                yield return current.ToArray();
                yield break;
            }

            decimal? previousAtDepth = null;
            for (var i = 0; i < sortedAmounts.Length; i++)
            {
                if (used[i]) continue;
                if (previousAtDepth.HasValue && sortedAmounts[i] == previousAtDepth.Value)
                    continue;

                previousAtDepth = sortedAmounts[i];
                used[i] = true;
                current[depth] = sortedAmounts[i];

                foreach (var permutation in Walk(depth + 1))
                    yield return permutation;

                used[i] = false;
            }
        }
    }

    private static string Key(IReadOnlyList<decimal> sortedAmounts) =>
        sortedAmounts.Count == 0 ? "0" : string.Join(",", sortedAmounts);

    private sealed class MatrixEntry
    {
        internal MatrixEntry(string key, decimal[] sortedAmounts)
        {
            Key = key;
            SortedAmounts = sortedAmounts;
        }

        internal string Key { get; }
        internal decimal[] SortedAmounts { get; }
        internal bool HasUpgradeOrigin { get; set; }
        internal int OriginStateCount { get; set; }
    }
}
