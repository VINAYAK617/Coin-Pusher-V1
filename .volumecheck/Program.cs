using CoinPusherEngine;
using Newtonsoft.Json;

if (args.Length > 0 && args[0].Equals("prupcase", StringComparison.OrdinalIgnoreCase))
{
    var diagSeed = args.Length > 1 && int.TryParse(args[1], out var parsedSeed) ? parsedSeed : 8181;
    var input = new MathInput
    {
        Targets = new Dictionary<int, int> { [2] = 20, [4] = 20 },
        BaseSpins = 5,
        Required = new Dictionary<string, int> { ["PRIZE_UPGRADE"] = 2 },
        PrizeTiers = new Dictionary<int, int> { [2] = 1, [4] = 1 },
        PrizeValues = PrizeValues(6, 3),
        MaxSym = 6,
    };
    var plan = new Planner(input, diagSeed).Plan();
    var json = TicketSerializer.ToJson(plan);
    var ticket = JsonConvert.DeserializeObject<TicketSerializer.TicketDto>(json)
        ?? throw new InvalidOperationException("serializer returned null ticket");
    var report = TicketChecker.CheckTicket(ticket);
    Console.WriteLine($"seed={diagSeed}");
    Console.WriteLine($"verified={plan.Verified}");
    Console.WriteLine($"valid={report.IsValid}");
    foreach (var check in report.Checks.Where(c => c.Result == TicketChecker.Status.Fail))
        Console.WriteLine($"{check.Category}/{check.Name}: {check.Detail}");
    for (var i = 0; i < ticket.Turns.Length; i++)
    {
        var features = ticket.Turns[i].Spawns
            .Where(spawn => spawn.Feature != null)
            .Select(spawn => $"{spawn.Pos}:{Name(spawn.Feature!.FeatureId)}->{string.Join("+", spawn.Feature.ReTrigger.Select(child => Name(child.FeatureId)))}");
        Console.WriteLine($"turn{i + 1}: {string.Join(",", features)}");
    }
    return;
}

var count = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 1000;
var baseSeed = args.Length > 1 && int.TryParse(args[1], out var seed) ? seed : 20260717;

var failures = new List<string>();
var totalSpins = new Dictionary<int, int>();
var nearMissCounts = new Dictionary<int, int>();
var pushCounts = new Dictionary<int, int>();
var featureCounts = new Dictionary<int, int>();
var wheelStackValues = new Dictionary<int, int>();
var retriggerPairs = new Dictionary<string, int>();
var mixedScreens = 0;
var all123Screens = 0;
var monotonicScreens = 0;
var flatScreens = 0;
var finalAnyFeature = 0;
var finalWheel = 0;
var finalExtra = 0;
var finalPrizeUpgrade = 0;
var ticketsWithNearMiss = 0;
var ticketsWithFeature = 0;
var ticketsWithRetrigger = 0;
var ticketsWithRepeatedWheelSymbol = 0;
var stopwatch = System.Diagnostics.Stopwatch.StartNew();

for (var i = 0; i < count; i++)
{
    var seedForTicket = Mix(baseSeed, i);
    try
    {
        var input = BuildInput(i, seedForTicket);
        var plan = new Planner(input, seedForTicket).Plan();
        var json = TicketSerializer.ToJson(plan);
        var ticket = JsonConvert.DeserializeObject<TicketSerializer.TicketDto>(json)
            ?? throw new InvalidOperationException("serializer returned null ticket");
        var report = TicketChecker.CheckTicket(ticket);
        if (!report.IsValid)
        {
            failures.Add($"#{i} seed={seedForTicket}: " + string.Join(" | ",
                report.Checks.Where(c => c.Result == TicketChecker.Status.Fail)
                    .Select(c => $"{c.Category}/{c.Name}:{c.Detail}")));
            continue;
        }

        Add(totalSpins, ticket.WinInfo.TotalSpins);
        Add(nearMissCounts, ticket.WinInfo.NonWinSymbols.Length);
        if (ticket.WinInfo.NonWinSymbols.Length > 0) ticketsWithNearMiss++;

        var hasFeature = false;
        var hasRetrigger = false;
        var wheelSymbolsThisTicket = new Dictionary<int, int>();
        for (var turnIndex = 0; turnIndex < ticket.Turns.Length; turnIndex++)
        {
            var turn = ticket.Turns[turnIndex];
            var normalPushes = turn.Pushers.Where(p => p.FeatureId == null).Select(p => p.PushValue).ToArray();
            foreach (var push in normalPushes) Add(pushCounts, push);
            if (normalPushes.Distinct().Count() > 1) mixedScreens++;
            if (normalPushes.Contains(1) && normalPushes.Contains(2) && normalPushes.Contains(3)) all123Screens++;
            if (normalPushes.Length > 1 && IsMonotonic(normalPushes)) monotonicScreens++;
            if (normalPushes.Length > 1 && normalPushes.Distinct().Count() == 1) flatScreens++;

            if (turnIndex == ticket.Turns.Length - 1)
            {
                if (turn.Spawns.Any(s => s.Feature != null)) finalAnyFeature++;
                if (turn.Spawns.Any(s => s.Feature?.FeatureId == 11)) finalWheel++;
                if (turn.Spawns.Any(s => s.Feature?.FeatureId == 12)) finalExtra++;
                if (turn.Spawns.Any(s => s.Feature?.FeatureId == 13)) finalPrizeUpgrade++;
            }

            foreach (var spawn in turn.Spawns)
            {
                if (spawn.Feature == null) continue;
                hasFeature = true;
                if (spawn.Feature.FeatureId == 11)
                {
                    Add(wheelStackValues, spawn.Feature.WheelStackValue ?? 0);
                    var wheelSym = spawn.Feature.WheelSymbolId ?? 0;
                    if (wheelSym > 0)
                        wheelSymbolsThisTicket[wheelSym] = wheelSymbolsThisTicket.GetValueOrDefault(wheelSym) + 1;
                }
                WalkFeature(spawn.Feature, parent: null);
            }
        }

        if (hasFeature) ticketsWithFeature++;
        if (hasRetrigger) ticketsWithRetrigger++;
        if (wheelSymbolsThisTicket.Values.Any(value => value > 1)) ticketsWithRepeatedWheelSymbol++;

        void WalkFeature(TicketSerializer.FeatureDto feature, int? parent)
        {
            Add(featureCounts, feature.FeatureId);
            if (parent.HasValue)
            {
                hasRetrigger = true;
                var pair = $"{Name(parent.Value)}->{Name(feature.FeatureId)}";
                retriggerPairs[pair] = retriggerPairs.GetValueOrDefault(pair) + 1;
            }
            foreach (var child in feature.ReTrigger)
                WalkFeature(child, feature.FeatureId);
        }
    }
    catch (Exception ex)
    {
        failures.Add($"#{i} seed={seedForTicket}: {ex.GetType().Name}: {ex.Message}");
    }
}

stopwatch.Stop();

Console.WriteLine($"tickets={count}");
Console.WriteLine($"failures={failures.Count}");
Console.WriteLine($"elapsedMs={stopwatch.ElapsedMilliseconds}");
Console.WriteLine($"ticketsPerSecond={(count / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds)):F1}");
Console.WriteLine($"totalSpins={FormatIntMap(totalSpins)}");
Console.WriteLine($"nearMissTickets={ticketsWithNearMiss}/{count} ({Percent(ticketsWithNearMiss, count)})");
Console.WriteLine($"nearMissCounts={FormatIntMap(nearMissCounts)}");
Console.WriteLine($"featureTickets={ticketsWithFeature}/{count} ({Percent(ticketsWithFeature, count)})");
Console.WriteLine($"featureCounts={FormatNamedIntMap(featureCounts, Name)}");
Console.WriteLine($"wheelStackValues={FormatIntMap(wheelStackValues)}");
Console.WriteLine($"repeatedWheelSymbolTickets={ticketsWithRepeatedWheelSymbol}/{count} ({Percent(ticketsWithRepeatedWheelSymbol, count)})");
Console.WriteLine($"retriggerTickets={ticketsWithRetrigger}/{count} ({Percent(ticketsWithRetrigger, count)})");
Console.WriteLine($"retriggerPairs={FormatStringMap(retriggerPairs)}");
Console.WriteLine($"pushCounts={FormatIntMap(pushCounts)}");
Console.WriteLine($"mixedScreens={mixedScreens}");
Console.WriteLine($"all123Screens={all123Screens}");
Console.WriteLine($"monotonicScreens={monotonicScreens}");
Console.WriteLine($"flatScreens={flatScreens}");
Console.WriteLine($"finalAnyFeature={finalAnyFeature}");
Console.WriteLine($"finalWheel={finalWheel}");
Console.WriteLine($"finalExtra={finalExtra}");
Console.WriteLine($"finalPrizeUpgrade={finalPrizeUpgrade}");
if (failures.Count > 0)
{
    Console.WriteLine("firstFailures:");
    foreach (var failure in failures.Take(10))
        Console.WriteLine(failure);
    Environment.ExitCode = 1;
}

static MathInput BuildInput(int index, int ticketSeed)
{
    var prizeValues = PrizeValues(6, 3);
    return (index % 6) switch
    {
        0 => new MathInput
        {
            Targets = new Dictionary<int, int> { [2] = 18, [4] = 18, [5] = 13 },
            BaseSpins = 5,
            PrizeValues = prizeValues,
            MaxSym = 6,
        },
        1 => new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = 20, [2] = 20, [3] = 20, [4] = 25 },
            BaseSpins = 5,
            PrizeValues = prizeValues,
            MaxSym = 7,
        },
        2 => new MathInput
        {
            Targets = new Dictionary<int, int> { [3] = 30 },
            BaseSpins = 5,
            Required = new Dictionary<string, int> { ["WHEEL"] = 2 },
            WheelSymOrder = new[] { 3, 3 },
            PrizeValues = prizeValues,
            MaxSym = 6,
        },
        3 => new MathInput
        {
            Targets = new Dictionary<int, int>(),
            BaseSpins = 5,
            PrizeValues = prizeValues,
            MaxSym = 6,
        },
        4 => new MathInput
        {
            Targets = new Dictionary<int, int> { [2] = 20, [5] = 25 },
            BaseSpins = 5,
            Required = new Dictionary<string, int> { ["PRIZE_UPGRADE"] = 1 },
            PrizeTiers = new Dictionary<int, int> { [2] = 1 },
            PrizeValues = prizeValues,
            MaxSym = 6,
        },
        _ => BuildBundleInput(ticketSeed),
    };
}

static MathInput BuildBundleInput(int seed)
{
    var rows = new[]
    {
        new PrizeLadderRow { Target = 20, Tiers = new decimal[] { 1, 2, 5 } },
        new PrizeLadderRow { Target = 20, Tiers = new decimal[] { 2, 5, 10 } },
        new PrizeLadderRow { Target = 20, Tiers = new decimal[] { 5, 10, 25 } },
        new PrizeLadderRow { Target = 25, Tiers = new decimal[] { 10, 25, 100 } },
        new PrizeLadderRow { Target = 25, Tiers = new decimal[] { 100, 250, 1000 } },
        new PrizeLadderRow { Target = 30, Tiers = new decimal[] { 10000 } },
    };
    return new LadderCombinator(rows, seed).Bundle(new decimal[] { 1, 2, 5, 10 }).Input;
}

static Dictionary<int, IReadOnlyDictionary<int, decimal>> PrizeValues(int maxSym, int tiers)
{
    var result = new Dictionary<int, IReadOnlyDictionary<int, decimal>>();
    for (var sym = 1; sym <= maxSym; sym++)
        result[sym] = Enumerable.Range(0, tiers).ToDictionary(tier => tier, tier => (decimal)(sym * 10 + tier));
    return result;
}

static int Mix(int seed, int index)
{
    unchecked
    {
        var x = (uint)seed ^ ((uint)index + 1u) * 0x9E3779B9u;
        x ^= x >> 16;
        x *= 0x85EBCA6Bu;
        x ^= x >> 13;
        x *= 0xC2B2AE35u;
        x ^= x >> 16;
        return (int)x;
    }
}

static bool IsMonotonic(IReadOnlyList<int> values)
{
    if (values.Distinct().Count() <= 1) return false;
    var up = true;
    var down = true;
    for (var i = 1; i < values.Count; i++)
    {
        up &= values[i] >= values[i - 1];
        down &= values[i] <= values[i - 1];
    }
    return up || down;
}

static void Add(Dictionary<int, int> map, int key) => map[key] = map.GetValueOrDefault(key) + 1;

static string Name(int id) => id switch
{
    11 => "WHEEL",
    12 => "EXTRA_SPIN",
    13 => "PRIZE_UPGRADE",
    14 => "FLUSH",
    _ => id.ToString(),
};

static string FormatIntMap(Dictionary<int, int> map) =>
    string.Join(",", map.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));

static string FormatNamedIntMap(Dictionary<int, int> map, Func<int, string> keyName) =>
    string.Join(",", map.OrderBy(kv => kv.Key).Select(kv => $"{keyName(kv.Key)}:{kv.Value}"));

static string FormatStringMap(Dictionary<string, int> map) =>
    string.Join(",", map.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));

static string Percent(int value, int total) => $"{(100.0 * value / Math.Max(1, total)):F2}%";
