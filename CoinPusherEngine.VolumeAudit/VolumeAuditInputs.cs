namespace CoinPusherEngine.VolumeAudit;

internal static class VolumeAuditInputs
{
    private static readonly decimal[][] PrizeCases =
    {
        Array.Empty<decimal>(),
        new decimal[] { 1 },
        new decimal[] { 2 },
        new decimal[] { 5 },
        new decimal[] { 10 },
        new decimal[] { 100 },
        new decimal[] { 10000 },
        new decimal[] { 1, 1 },
        new decimal[] { 1, 1, 1 },
        new decimal[] { 1, 2 },
        new decimal[] { 1, 2, 5 },
        new decimal[] { 1, 2, 5, 10 },
        new decimal[] { 10, 100 },
        new decimal[] { 5, 10, 100, 10000 },
        new decimal[] { 1, 2, 5, 10, 100, 10000 },
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
