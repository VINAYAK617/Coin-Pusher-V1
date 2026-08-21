namespace CoinPusherEngine.VolumeAudit;

internal static class VolumeAuditInputs
{
    private static readonly decimal[][] PrizeCases =
    {
        Array.Empty<decimal>(),
        new decimal[] { 1 },
        new decimal[] { 2 },
        new decimal[] { 4 },
        new decimal[] { 5 },
        new decimal[] { 8 },
        new decimal[] { 10 },
        new decimal[] { 20 },
        new decimal[] { 25 },
        new decimal[] { 50 },
        new decimal[] { 100 },
        new decimal[] { 200 },
        new decimal[] { 500 },
        new decimal[] { 10000 },
        new decimal[] { 1, 1 },
        new decimal[] { 1, 1, 1 },
        new decimal[] { 1, 2 },
        new decimal[] { 1, 2, 5 },
        new decimal[] { 10, 2, 50 },
        new decimal[] { 1, 4, 10 },
        new decimal[] { 1, 4, 10, 20 },
        new decimal[] { 1, 4, 10, 20, 200 },
        new decimal[] { 1, 4, 10, 20, 200, 10000 },
        new decimal[] { 2, 4, 10, 20, 100, 10000 },
        new decimal[] { 3, 7, 18 },
        new decimal[] { 55, 200 },
    };

    internal static IReadOnlyList<decimal> Build(int index, Random rng)
    {
        if (index % 10 == 0)
            return Array.Empty<decimal>();

        if (index % 25 == 0)
            return new decimal[] { 1, 2, 5, 10, 100, 10000 };

        return PrizeCases[rng.Next(PrizeCases.Length)];
    }
}
