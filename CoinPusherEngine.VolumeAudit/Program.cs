using System.Collections.Concurrent;
using System.Diagnostics;
using CoinPusherEngine;
using CoinPusherEngine.VolumeAudit;

var count = ArgInt(args, 0, 1000);
var seed = ArgInt(args, 1, 20260806);
var progressEvery = Math.Max(1, ArgInt(args, 2, Math.Max(1, count / 20)));
var maxFailures = Math.Max(1, ArgInt(args, 3, 20));
var degreeOfParallelism = Math.Max(1, ArgInt(args, 4, Environment.ProcessorCount));
var settings = new Settings();

var rng = new Random(seed);
var sw = Stopwatch.StartNew();
var inputStage = Stopwatch.StartNew();
var cases = Enumerable.Range(0, count)
    .Select(i => (Index: i, Seed: rng.Next(1, int.MaxValue), Prizes: VolumeAuditInputs.Build(i, rng).ToArray()))
    .ToArray();
inputStage.Stop();

var generationTicks = 0L;
var checkerTicks = 0L;
var failures = new List<string>();
var failureCount = 0;
var warningCount = 0;
var completedCount = 0;
var winningCount = 0;
var noWinCount = 0;
var nearMissTicketCount = 0;
var featureTicketCount = 0;
var maxTurnCount = 0;

var spinCounts = new ConcurrentDictionary<int, int>();
var winSymbolCounts = new ConcurrentDictionary<int, int>();
var nearMissSymbolCounts = new ConcurrentDictionary<int, int>();
var featureCounts = new ConcurrentDictionary<string, int>();
var pushCounts = new ConcurrentDictionary<int, int>();
var wheelStackCounts = new ConcurrentDictionary<int, int>();
var prizeCaseCounts = new ConcurrentDictionary<string, int>();
var progressLock = new object();

Console.WriteLine(
    $"Coin Pusher guarded volume audit started: tickets={count}, seed={seed}, parallel={degreeOfParallelism}");

Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = degreeOfParallelism }, (i, state) =>
{
    if (Volatile.Read(ref failureCount) >= maxFailures)
    {
        state.Stop();
        return;
    }

    var item = cases[i];
    var prizeKey = PrizeKey(item.Prizes);
    Increment(prizeCaseCounts, prizeKey);

    var generationStage = Stopwatch.StartNew();
    var result = new CoinPusherTicketJsonGenerator(settings).Generate(item.Prizes, item.Seed);
    generationStage.Stop();
    Interlocked.Add(ref generationTicks, generationStage.ElapsedTicks);
    if (!result.IsValid || result.Ticket == null || result.Plan == null || string.IsNullOrWhiteSpace(result.Json))
    {
        AddFailure(
            $"ticket {item.Index} seed {item.Seed} prizes=[{prizeKey}]: generation failed: {result.Status}: {result.Detail}",
            state);
        return;
    }

    var ticket = result.Ticket;
    var checkerStage = Stopwatch.StartNew();
    var report = TicketChecker.CheckTicket(ticket);
    checkerStage.Stop();
    Interlocked.Add(ref checkerTicks, checkerStage.ElapsedTicks);
    if (!report.IsValid)
    {
        AddFailure(
            $"ticket {item.Index} seed {item.Seed} prizes=[{prizeKey}]: checker failed: {FirstFailures(report)}",
            state);
        return;
    }

    var invariantFailure = ValidateReleaseInvariants(result, settings);
    if (invariantFailure != null)
    {
        AddFailure(
            $"ticket {item.Index} seed {item.Seed} prizes=[{prizeKey}]: {invariantFailure}",
            state);
        return;
    }

    Interlocked.Add(ref warningCount, report.WarningCount);
    if (ticket.WinInfo.WinSymbols.Length == 0) Interlocked.Increment(ref noWinCount);
    else Interlocked.Increment(ref winningCount);
    if (result.Plan.NonWinTargets.Count > 0) Interlocked.Increment(ref nearMissTicketCount);
    if (HasAnyTicketFeature(ticket, settings)) Interlocked.Increment(ref featureTicketCount);
    if (ticket.WinInfo.TotalSpins == settings.MAX_SPINS) Interlocked.Increment(ref maxTurnCount);

    Increment(spinCounts, ticket.WinInfo.TotalSpins);
    Increment(winSymbolCounts, ticket.WinInfo.WinSymbols.Length);
    Increment(nearMissSymbolCounts, result.Plan.NonWinTargets.Count);
    AccumulatePushes(ticket);
    AccumulateFeatures(ticket, settings);

    var completed = Interlocked.Increment(ref completedCount);
    if (completed % progressEvery == 0 || completed == count)
    {
        lock (progressLock)
        {
            var rate = completed / Math.Max(0.001, sw.Elapsed.TotalSeconds);
            Console.WriteLine(
                $"progress {completed}/{count}, failures={failureCount}, warnings={warningCount}, " +
                $"rate={rate:F1}/sec, elapsed={sw.Elapsed:hh\\:mm\\:ss}");
        }
    }
});

sw.Stop();
Console.WriteLine(
    $"volume tickets={count}, seed={seed}, failures={failureCount}, warnings={warningCount}, " +
    $"winning={winningCount}, noWin={noWinCount}, plannedNearMiss={nearMissTicketCount}, " +
    $"featureTickets={featureTicketCount}, maxTurns={maxTurnCount}, elapsed={sw.Elapsed:hh\\:mm\\:ss}");
Console.WriteLine(
    $"stage totals: input={inputStage.Elapsed:hh\\:mm\\:ss\\.fff}, generation={Elapsed(generationTicks):hh\\:mm\\:ss\\.fff}, " +
    $"checker={Elapsed(checkerTicks):hh\\:mm\\:ss\\.fff}");
PrintDistribution("prize cases", prizeCaseCounts);
PrintDistribution("spins", spinCounts);
PrintDistribution("win symbol counts", winSymbolCounts);
PrintDistribution("planned near-miss symbol counts", nearMissSymbolCounts);
PrintDistribution("push values", pushCounts);
PrintDistribution("features", featureCounts);
PrintDistribution("wheel stack values", wheelStackCounts);

if (failureCount > 0)
{
    Console.WriteLine("Failures:");
    foreach (var failure in failures.Take(maxFailures))
        Console.WriteLine(failure);
    Environment.ExitCode = 1;
}

static int ArgInt(string[] args, int index, int fallback) =>
    args.Length > index && int.TryParse(args[index], out var value) ? value : fallback;

void AddFailure(string failure, ParallelLoopState state)
{
    lock (failures)
    {
        failures.Add(failure);
        failureCount = failures.Count;
        if (failureCount >= maxFailures)
            state.Stop();
    }
}

string? ValidateReleaseInvariants(CoinPusherTicketJsonGenerationResult result, Settings settings)
{
    var ticket = result.Ticket!;
    var plan = result.Plan!;

    if (ticket.WinInfo.TotalSpins != ticket.Turns.Length || ticket.WinInfo.TotalSpins != plan.TotalSpins)
    {
        return $"spin count mismatch ticket TotalSpins={ticket.WinInfo.TotalSpins}, turns={ticket.Turns.Length}, plan={plan.TotalSpins}";
    }

    if (ticket.Turns.Length > 0 && HasAnyTurnFeature(ticket.Turns[^1], settings))
        return "last turn contains a board feature or FLUSH pusher";

    var lowNearMiss = plan.NonWinTargets.FirstOrDefault(kv => kv.Value < settings.NONWIN_MIN_TARGET);
    if (lowNearMiss.Key != 0)
        return $"planned near-miss symbol {lowNearMiss.Key} target {lowNearMiss.Value} is below minimum {settings.NONWIN_MIN_TARGET}";

    var illegalPush = ticket.Turns
        .SelectMany(turn => turn.Pushers)
        .FirstOrDefault(pusher => !pusher.FeatureId.HasValue
            && (pusher.PushValue < settings.MIN_PUSH || pusher.PushValue > settings.MAX_PUSH));
    if (illegalPush != null)
        return $"illegal normal push value {illegalPush.PushValue}";

    var illegalFlush = ticket.Turns
        .SelectMany(turn => turn.Pushers)
        .FirstOrDefault(pusher => pusher.FeatureId == settings.F_FLUSH_ID && pusher.PushValue != settings.ROWS);
    if (illegalFlush != null)
        return $"FLUSH pusher has PushValue={illegalFlush.PushValue}, expected {settings.ROWS}";

    var illegalWheelStack = ticket.Turns
        .SelectMany(turn => turn.Spawns)
        .Where(spawn => spawn.Feature?.FeatureId == settings.F_WHEEL)
        .Select(spawn => spawn.Feature!.WheelStackValue)
        .FirstOrDefault(stack => stack.HasValue
            && (stack.Value < settings.MIN_WHEEL_STACK_VALUE || stack.Value > settings.MAX_WHEEL_STACK_VALUE));
    if (illegalWheelStack.HasValue)
        return $"WHEEL stack value {illegalWheelStack.Value} outside configured range";

    return null;
}

static string FirstFailures(TicketChecker.Report report) =>
    string.Join(" | ", report.Checks
        .Where(c => c.Result == TicketChecker.Status.Fail)
        .Select(c => $"{c.Category}/{c.Name}: {c.Detail}")
        .Take(5));

static bool HasAnyTicketFeature(TicketSerializer.TicketDto ticket, Settings settings) =>
    ticket.Turns.Any(turn => HasAnyTurnFeature(turn, settings));

static bool HasAnyTurnFeature(TicketSerializer.TurnDto turn, Settings settings) =>
    turn.Spawns.Any(spawn => spawn.Feature != null)
    || turn.Pushers.Any(pusher => pusher.FeatureId == settings.F_FLUSH_ID);

void AccumulatePushes(TicketSerializer.TicketDto ticket)
{
    foreach (var pusher in ticket.Turns.SelectMany(turn => turn.Pushers))
        Increment(pushCounts, pusher.PushValue);
}

void AccumulateFeatures(TicketSerializer.TicketDto ticket, Settings settings)
{
    foreach (var pusher in ticket.Turns.SelectMany(turn => turn.Pushers))
    {
        if (pusher.FeatureId == settings.F_FLUSH_ID)
            Increment(featureCounts, "FLUSH");
    }

    foreach (var feature in ticket.Turns.SelectMany(turn => turn.Spawns).Select(spawn => spawn.Feature).Where(feature => feature != null))
    {
        var name = feature!.FeatureId == settings.F_WHEEL ? "WHEEL"
            : feature.FeatureId == settings.F_XSPIN ? "EXTRA_GO"
            : feature.FeatureId == settings.F_PRUP ? "PRIZE_UPGRADE"
            : $"FEATURE_{feature.FeatureId}";
        Increment(featureCounts, name);

        if (feature.FeatureId == settings.F_WHEEL && feature.WheelStackValue.HasValue)
            Increment(wheelStackCounts, feature.WheelStackValue.Value);
    }
}

static void Increment<TKey>(ConcurrentDictionary<TKey, int> dictionary, TKey key)
    where TKey : notnull =>
    dictionary.AddOrUpdate(key, 1, (_, count) => count + 1);

static void PrintDistribution<TKey>(string label, ConcurrentDictionary<TKey, int> dictionary)
    where TKey : notnull
{
    var text = string.Join(", ", dictionary
        .OrderBy(kv => kv.Key)
        .Select(kv => $"{kv.Key}={kv.Value}"));
    Console.WriteLine($"{label}: {text}");
}

static TimeSpan Elapsed(long ticks) =>
    TimeSpan.FromSeconds(ticks / (double)Stopwatch.Frequency);

static string PrizeKey(IReadOnlyList<decimal> prizes) =>
    prizes.Count == 0 ? "0" : string.Join(",", prizes);
