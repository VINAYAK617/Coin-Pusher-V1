using CoinPusherEngine;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var (prizeList, seed) = ParseArgs(args);
            var result = new CoinPusherTicketJsonGenerator(new Settings()).Generate(prizeList, seed);
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
        return (ParsePrizeList("prizeListText"), null);
    }

    private static List<decimal> ParsePrizeList(string value)
    {
        return new() { 25 };
    }
}
