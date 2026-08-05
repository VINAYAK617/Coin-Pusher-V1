using System.Diagnostics;
using CoinPusherEngine;

var count = ArgInt(args, 0, 1000);
var seed = ArgInt(args, 1, 20260806);
var progressEvery = Math.Max(1, ArgInt(args, 2, Math.Max(1, count / 20)));
var maxFailures = Math.Max(1, ArgInt(args, 3, 20));

var rng = new Random(seed);
var sw = Stopwatch.StartNew();
var failures = new List<string>();
var warningCount = 0;
var noWinCount = 0;
var winningCount = 0;
var maxTurnCount = 0;
var featureTicketCount = 0;
var nearMissTicketCount = 0;

Console.WriteLine($"Coin Pusher volume audit started: tickets={count}, seed={seed}");

for (var i = 0; i < count; i++)
{
    var ticketSeed = rng.Next(1, int.MaxValue);
    TicketSerializer.TicketDto ticket;
    try
    {
        var input = VolumeAuditInputs.Build(i, ticketSeed, rng);
        var plan = new Planner(input, ticketSeed).Plan();
        ticket = TicketSerializer.ToTicketObject(plan);
    }
    catch (Exception ex)
    {
        failures.Add($"ticket {i} seed {ticketSeed}: generation failed: {ex.Message}");
        if (failures.Count >= maxFailures) break;
        continue;
    }

    var report = TicketChecker.CheckTicket(ticket);
    if (!report.IsValid)
    {
        var errors = string.Join(" | ", report.Checks
            .Where(c => c.Result == TicketChecker.Status.Fail)
            .Select(c => $"{c.Category}/{c.Name}: {c.Detail}")
            .Take(5));
        failures.Add($"ticket {i} seed {ticketSeed}: checker failed: {errors}");
        if (failures.Count >= maxFailures) break;
    }

    warningCount += report.WarningCount;
    if (ticket.WinInfo.WinSymbols.Length == 0) noWinCount++;
    else winningCount++;
    if (ticket.WinInfo.TotalSpins == Settings.Default.MAX_SPINS) maxTurnCount++;
    if (ticket.Turns.Any(turn => turn.Spawns.Any(spawn => spawn.Feature != null))
        || ticket.Turns.Any(turn => turn.Pushers.Any(pusher => pusher.FeatureId.HasValue)))
        featureTicketCount++;
    if (ticket.WinInfo.NonWinSymbols.Any(symbol => symbol.MinTarget >= Settings.Default.NONWIN_MIN_TARGET))
        nearMissTicketCount++;

    var completed = i + 1;
    if (completed % progressEvery == 0 || completed == count)
    {
        var rate = completed / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        Console.WriteLine(
            $"progress {completed}/{count}, failures={failures.Count}, warnings={warningCount}, " +
            $"rate={rate:F1}/sec, elapsed={sw.Elapsed:hh\\:mm\\:ss}");
    }
}

sw.Stop();
Console.WriteLine(
    $"volume tickets={count}, seed={seed}, failures={failures.Count}, warnings={warningCount}, " +
    $"winning={winningCount}, noWin={noWinCount}, nearMiss={nearMissTicketCount}, " +
    $"featureTickets={featureTicketCount}, maxTurns={maxTurnCount}, elapsed={sw.Elapsed:hh\\:mm\\:ss}");

if (failures.Count > 0)
{
    Console.WriteLine("Failures:");
    foreach (var failure in failures.Take(maxFailures))
        Console.WriteLine(failure);
    Environment.ExitCode = 1;
}

static int ArgInt(string[] args, int index, int fallback) =>
    args.Length > index && int.TryParse(args[index], out var value) ? value : fallback;
