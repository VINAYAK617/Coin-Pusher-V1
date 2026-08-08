using System.Diagnostics;
using CoinPusherEngine;
using CoinPusherEngine.VolumeAudit;

var count = ArgInt(args, 0, 1000);
var seed = ArgInt(args, 1, 20260806);
var progressEvery = Math.Max(1, ArgInt(args, 2, Math.Max(1, count / 20)));
var maxFailures = Math.Max(1, ArgInt(args, 3, 20));
var degreeOfParallelism = Math.Max(1, ArgInt(args, 4, Environment.ProcessorCount));

var rng = new Random(seed);
var sw = Stopwatch.StartNew();
var inputTicks = 0L;
var planTicks = 0L;
var serializeTicks = 0L;
var checkerTicks = 0L;
var plannerRetryTicketCount = 0;
var localRetryTicketCount = 0;
var plannerRetryAttempts = 0L;
var localRetryAttempts = 0L;
var plannerRetryCauses = new Dictionary<string, int>();
var failures = new List<string>();
var failureCount = 0;
var warningCount = 0;
var noWinCount = 0;
var winningCount = 0;
var maxTurnCount = 0;
var featureTicketCount = 0;
var nearMissTicketCount = 0;

var inputStage = Stopwatch.StartNew();
var tickets = Enumerable.Range(0, count)
    .Select(i =>
    {
        var ticketSeed = rng.Next(1, int.MaxValue);
        return (Seed: ticketSeed, Input: VolumeAuditInputs.Build(i, ticketSeed, rng));
    })
    .ToArray();
inputStage.Stop();
inputTicks = inputStage.ElapsedTicks;
var progressLock = new object();
var completedCount = 0;

Console.WriteLine(
    $"Coin Pusher volume audit started: tickets={count}, seed={seed}, parallel={degreeOfParallelism}");

Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = degreeOfParallelism }, (i, state) =>
{
    if (Volatile.Read(ref completedCount) >= count || Volatile.Read(ref failureCount) >= maxFailures)
    {
        state.Stop();
        return;
    }

    var ticketSeed = tickets[i].Seed;
    TicketSerializer.TicketDto ticket;
    try
    {
        var stage = Stopwatch.StartNew();
        var input = tickets[i].Input;
        var plan = new Planner(input, ticketSeed).Plan();
        AccumulateRetryMetrics(plan);
        stage.Stop();
        Interlocked.Add(ref planTicks, stage.ElapsedTicks);

        stage.Restart();
        ticket = TicketSerializer.ToTicketObject(plan);
        stage.Stop();
        Interlocked.Add(ref serializeTicks, stage.ElapsedTicks);
    }
    catch (Exception ex)
    {
        AddFailure($"ticket {i} seed {ticketSeed}: generation failed: {ex.Message}", state);
        return;
    }

    var checkerStage = Stopwatch.StartNew();
    var report = TicketChecker.CheckTicket(ticket);
    checkerStage.Stop();
    Interlocked.Add(ref checkerTicks, checkerStage.ElapsedTicks);
    if (!report.IsValid)
    {
        var errors = string.Join(" | ", report.Checks
            .Where(c => c.Result == TicketChecker.Status.Fail)
            .Select(c => $"{c.Category}/{c.Name}: {c.Detail}")
            .Take(5));
        AddFailure($"ticket {i} seed {ticketSeed}: checker failed: {errors}", state);
        return;
    }

    Interlocked.Add(ref warningCount, report.WarningCount);
    if (ticket.WinInfo.WinSymbols.Length == 0) Interlocked.Increment(ref noWinCount);
    else Interlocked.Increment(ref winningCount);
    if (ticket.WinInfo.TotalSpins == Settings.Default.MAX_SPINS) Interlocked.Increment(ref maxTurnCount);
    if (ticket.Turns.Any(turn => turn.Spawns.Any(spawn => spawn.Feature != null))
        || ticket.Turns.Any(turn => turn.Pushers.Any(pusher => pusher.FeatureId.HasValue)))
        Interlocked.Increment(ref featureTicketCount);
    if (ticket.WinInfo.NonWinSymbols.Any(symbol => symbol.MinTarget >= Settings.Default.NONWIN_MIN_TARGET))
        Interlocked.Increment(ref nearMissTicketCount);

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
    $"winning={winningCount}, noWin={noWinCount}, nearMiss={nearMissTicketCount}, " +
    $"featureTickets={featureTicketCount}, maxTurns={maxTurnCount}, elapsed={sw.Elapsed:hh\\:mm\\:ss}");
Console.WriteLine(
    $"stage totals: input={Elapsed(inputTicks):hh\\:mm\\:ss\\.fff}, plan={Elapsed(planTicks):hh\\:mm\\:ss\\.fff}, " +
    $"serialize={Elapsed(serializeTicks):hh\\:mm\\:ss\\.fff}, checker={Elapsed(checkerTicks):hh\\:mm\\:ss\\.fff}");
Console.WriteLine(
    $"retry totals: plannerTickets={plannerRetryTicketCount}, plannerExtraAttempts={plannerRetryAttempts}, " +
    $"localTickets={localRetryTicketCount}, localExtraAttempts={localRetryAttempts}");
if (plannerRetryCauses.Count > 0)
{
    Console.WriteLine("planner retry causes: " + string.Join(", ",
        plannerRetryCauses.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
}

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

static TimeSpan Elapsed(long ticks) =>
    TimeSpan.FromSeconds(ticks / (double)Stopwatch.Frequency);

void AccumulateRetryMetrics(GamePlan plan)
{
    foreach (var line in plan.Log)
    {
        if (line.StartsWith("planned after ", StringComparison.Ordinal)
            && TryReadFirstNumber(line, out var plannerAttempts)
            && plannerAttempts > 1)
        {
            Interlocked.Increment(ref plannerRetryTicketCount);
            Interlocked.Add(ref plannerRetryAttempts, plannerAttempts - 1);
        }

        if (line.StartsWith("local realization succeeded after ", StringComparison.Ordinal)
            && TryReadFirstNumber(line, out var localAttempts)
            && localAttempts > 1)
        {
            Interlocked.Increment(ref localRetryTicketCount);
            Interlocked.Add(ref localRetryAttempts, localAttempts - 1);
        }

        if (line.StartsWith("planner retry causes: ", StringComparison.Ordinal))
            AccumulatePlannerRetryCauses(line["planner retry causes: ".Length..]);
    }
}

void AccumulatePlannerRetryCauses(string causes)
{
    lock (plannerRetryCauses)
    {
        foreach (var item in causes.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Trim().Split('=', 2);
            if (parts.Length != 2 || !int.TryParse(parts[1], out var count)) continue;
            plannerRetryCauses[parts[0]] = plannerRetryCauses.GetValueOrDefault(parts[0]) + count;
        }
    }
}

static bool TryReadFirstNumber(string text, out int value)
{
    value = 0;
    var digits = new string(text.SkipWhile(ch => !char.IsDigit(ch))
        .TakeWhile(char.IsDigit)
        .ToArray());
    return digits.Length > 0 && int.TryParse(digits, out value);
}
