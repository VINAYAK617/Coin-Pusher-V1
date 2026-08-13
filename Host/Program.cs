using CoinPusherEngine;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var (prizeList, seed) = ParseArgs(args);
            var result = new CoinPusherTicketJsonGenerator().Generate(prizeList, seed);
            if (!result.IsValid)
                throw new InvalidOperationException(result.Detail);

            Console.WriteLine(result.Json);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static (IReadOnlyList<decimal> PrizeList, int? Seed) ParseArgs(string[] args)
    {
        var prizeList = args.Length > 0
            ? ParsePrizeList(args[0])
            : new List<decimal>();
        var seed = args.Length > 1 && int.TryParse(args[1], out var parsedSeed)
            ? parsedSeed
            : (int?)null;

        return (prizeList, seed);
    }

    private static List<decimal> ParsePrizeList(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "0")
            return new List<decimal>();

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => decimal.Parse(part.Trim()))
            .ToList();
    }
}
