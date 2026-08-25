namespace CoinPusherEngine.VolumeAudit;

internal sealed class AlwFixedTicketTier
{
    internal AlwFixedTicketTier(int tierNumber, int ppsCombinationId, int ticketCount)
    {
        TierNumber = tierNumber;
        PpsCombinationId = ppsCombinationId;
        TicketCount = ticketCount;
    }

    internal int TierNumber { get; }
    internal int PpsCombinationId { get; }
    internal int TicketCount { get; }
    internal bool IsNoWin => TierNumber == 0;
}

internal static class AlwFixedTicketCatalog
{
    private static readonly int[] WinningTierCounts =
    {
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 2, 2, 1, 2, 1, 1, 1, 1, 3,
        3, 2, 2, 2, 2, 3, 2, 2, 2, 1,
        5, 1, 2, 1, 1, 4, 4, 2, 2, 2,
        2, 1, 1, 1, 1, 1, 4, 3, 10, 9,
        8, 9, 38, 35, 34, 73,
    };

    internal static IReadOnlyList<AlwFixedTicketTier> Build(ICustomProfileSettings settings)
    {
        if (settings.PpsCombinations.Count != WinningTierCounts.Length)
        {
            throw new InvalidOperationException(
                $"fixed catalog has {WinningTierCounts.Length} winning tiers, " +
                $"but settings contain {settings.PpsCombinations.Count} PPS combinations");
        }

        var rows = settings.PpsCombinations.OrderBy(row => row.Id).ToArray();
        for (var index = 0; index < rows.Length; index++)
        {
            var expectedId = index + 1;
            if (rows[index].Id != expectedId)
            {
                throw new InvalidOperationException(
                    $"PPS combination ids must be continuous 1..{rows.Length}; " +
                    $"expected {expectedId}, found {rows[index].Id}");
            }
        }

        var catalog = new List<AlwFixedTicketTier>
        {
            new(tierNumber: 0, ppsCombinationId: 0, ticketCount: 80),
        };
        catalog.AddRange(rows.Select((row, index) =>
            new AlwFixedTicketTier(
                tierNumber: row.Id,
                ppsCombinationId: row.Id,
                ticketCount: WinningTierCounts[index])));

        if (catalog.Sum(item => item.TicketCount) != 400)
            throw new InvalidOperationException("fixed ALW ticket catalog must contain exactly 400 tickets");

        return catalog.OrderBy(item => item.TierNumber).ToArray();
    }
}
