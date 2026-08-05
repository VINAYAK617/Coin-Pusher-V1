using CoinPusherEngine;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && IsHelp(args[0]))
        {
            PrintUsage();
            return 0;
        }

        try
        {
            var options = ParseOptions(args);
            var input = new MathInput
            {
                Targets = options.Targets,
                BaseSpins = options.Settings.BASE_SPINS,
                Required = options.Required,
                WheelSymOrder = options.WheelSymOrder.Count > 0 ? options.WheelSymOrder : null,
                PrizeTiers = options.PrizeTiers.Count > 0 ? options.PrizeTiers : null,
                NonWinTargets = options.NonWinTargets.Count > 0 ? options.NonWinTargets : null,
                MaxSym = options.MaxSym,
            };

            var plan = new Planner(input, options.Settings, options.Seed).Plan();
            Console.WriteLine(TicketSerializer.ToJson(plan, options.Settings));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static GeneratorOptions ParseOptions(string[] args)
    {
        var options = new GeneratorOptions();
        var index = args.Length > 0 && args[0].Equals("generate", StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;

        while (index < args.Length)
        {
            var name = args[index++];
            var value = ReadValue(args, ref index, name);

            switch (name.ToLowerInvariant())
            {
                case "--seed":
                    options.Seed = int.Parse(value);
                    break;
                case "--targets":
                    options.Targets = ParseIntMap(value);
                    break;
                case "--nonwin":
                    options.NonWinTargets = ParseIntMap(value);
                    break;
                case "--required":
                    options.Required = ParseStringIntMap(value);
                    break;
                case "--wheel":
                    options.WheelSymOrder = ParseIntList(value);
                    break;
                case "--prizetiers":
                    options.PrizeTiers = ParseIntMap(value);
                    break;
                case "--maxsym":
                    options.MaxSym = int.Parse(value);
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{name}'.");
            }
        }

        return options;
    }

    private static string ReadValue(string[] args, ref int index, string name)
    {
        if (index >= args.Length)
            throw new ArgumentException($"Missing value for {name}.");
        return args[index++];
    }

    private static Dictionary<int, int> ParseIntMap(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new Dictionary<int, int>();

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParsePair)
            .ToDictionary(pair => int.Parse(pair.Key), pair => int.Parse(pair.Value));
    }

    private static Dictionary<string, int> ParseStringIntMap(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new Dictionary<string, int>();

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParsePair)
            .ToDictionary(pair => pair.Key, pair => int.Parse(pair.Value));
    }

    private static List<int> ParseIntList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new List<int>();

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse)
            .ToList();
    }

    private static (string Key, string Value) ParsePair(string value)
    {
        var parts = value.Split('=', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            throw new ArgumentException($"Expected key=value pair, found '{value}'.");
        return (parts[0], parts[1]);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("CoinPusherEngine ticket generator");
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project .\\CoinPusherEngine.csproj -- generate [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --seed <number>                 Optional deterministic seed");
        Console.WriteLine("  --targets <id=count,...>         Winning targets, default 2=20,4=20");
        Console.WriteLine("  --nonwin <id=count,...>          Optional fixed near-miss targets");
        Console.WriteLine("  --required <feature=count,...>   Optional required features");
        Console.WriteLine("  --wheel <id,id,...>              Optional wheel symbol order");
        Console.WriteLine("  --prizeTiers <id=tier,...>       Optional prize tiers");
        Console.WriteLine("  --maxSym <number>                Max symbol id, default 6");
    }

    private static bool IsHelp(string value) =>
        value == "-h" || value == "--help" || value == "help";

    private sealed class GeneratorOptions
    {
        internal Dictionary<int, int> Targets { get; set; } = new() { [2] = 20, [4] = 20 };
        internal Dictionary<int, int> NonWinTargets { get; set; } = new();
        internal Dictionary<string, int> Required { get; set; } = new();
        internal List<int> WheelSymOrder { get; set; } = new();
        internal Dictionary<int, int> PrizeTiers { get; set; } = new();
        internal int MaxSym { get; set; } = 6;
        internal int? Seed { get; set; }
        internal Settings Settings { get; } = new();
    }
}
