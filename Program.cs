using CoinPusherEngine;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage();
            return 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "check" => CheckTicketFile(args),
            "sample" => WriteSampleTicket(args),
            _ => UnknownCommand(args[0]),
        };
    }

    private static int CheckTicketFile(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Missing ticket JSON file path.");
            PrintUsage();
            return 2;
        }

        var json = File.ReadAllText(args[1]);
        var result = TicketChecker.CheckJson(json);
        foreach (var error in result.Errors)
            Console.Error.WriteLine(error);

        return result.IsValid ? 0 : 1;
    }

    private static int WriteSampleTicket(string[] args)
    {
        var seed = args.Length > 1 && int.TryParse(args[1], out var parsedSeed)
            ? parsedSeed
            : 20260806;
        var input = new MathInput
        {
            Targets = new Dictionary<int, int> { [2] = 20, [4] = 20 },
            BaseSpins = Settings.Default.BASE_SPINS,
            MaxSym = 6,
        };

        var plan = new Planner(input, seed).Plan();
        Console.WriteLine(TicketSerializer.ToJson(plan));
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("CoinPusherEngine");
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project .\\CoinPusherEngine.csproj -- check <ticket.json>");
        Console.WriteLine("  dotnet run --project .\\CoinPusherEngine.csproj -- sample [seed]");
    }

    private static bool IsHelp(string value) =>
        value == "-h" || value == "--help" || value == "help";
}
