using CoinPusherEngine;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            var (prizeList, seed) = ParseArgs(args);
            var settings = new Settings();
            var bundle = new LadderCombinator(settings.PrizeLadderRows, seed).Bundle(prizeList);
            var plan = new Planner(bundle.Input, settings, seed).Plan();

            Console.WriteLine(TicketSerializer.ToJson(plan, settings));
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
        var index = args[0].Equals("generate", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        int? seed = null;
        string? prizeListText = null;

        while (index < args.Length)
        {
            var current = args[index++];
            if (current.Equals("--seed", StringComparison.OrdinalIgnoreCase))
            {
                if (index >= args.Length)
                    throw new ArgumentException("Missing value for --seed.");
                seed = int.Parse(args[index++]);
                continue;
            }

            if (current.Equals("--prizes", StringComparison.OrdinalIgnoreCase)
                || current.Equals("--prizeList", StringComparison.OrdinalIgnoreCase)
                || current.Equals("--prize", StringComparison.OrdinalIgnoreCase))
            {
                if (index >= args.Length)
                    throw new ArgumentException($"Missing value for {current}.");
                prizeListText = args[index++];
                continue;
            }

            if (prizeListText == null)
            {
                prizeListText = current;
                continue;
            }

            throw new ArgumentException($"Unexpected argument '{current}'. Pass only prizeList and optional --seed.");
        }

        if (prizeListText == null)
            throw new ArgumentException("Missing prizeList.");

        return (ParsePrizeList(prizeListText), seed);
    }

    private static List<decimal> ParsePrizeList(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Equals("none", StringComparison.OrdinalIgnoreCase)
            || value.Equals("nowin", StringComparison.OrdinalIgnoreCase)
            || value.Equals("loss", StringComparison.OrdinalIgnoreCase)
            || value == "0")
        {
            return new List<decimal>();
        }

        return value
            .Trim('[', ']', '{', '}')
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(decimal.Parse)
            .ToList();
    }

    private static void PrintUsage()
    {
        Console.WriteLine("CoinPusherEngine ticket generator");
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project .\\CoinPusherEngine.csproj -- generate --prizes 10,2,100 --seed 123");
        Console.WriteLine("  dotnet run --project .\\CoinPusherEngine.csproj -- 10,2,100");
        Console.WriteLine("  dotnet run --project .\\CoinPusherEngine.csproj -- 0");
    }

    private static bool IsHelp(string value) =>
        value == "-h" || value == "--help" || value == "help";
}
