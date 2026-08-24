using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using CoinPusherEngine;
using CoinPusherEngine.VolumeAudit;
using GameEngine;

var count = ArgInt(args, 0, 5000000);
var seed = ArgInt(args, 1, 20260806);
var progressEvery = 5000;// Math.Max(1, ArgInt(args, 2, Math.Max(1, count / 20)));
var maxFailures = Math.Max(1, ArgInt(args, 3, 20));
var degreeOfParallelism = Math.Max(1, ArgInt(args, 4, Environment.ProcessorCount));
var inputMode = InputMode(args);
var alwMode = inputMode is "alw" or "pps" or "alw-pps" or "alw-random";
var settings = alwMode
    ? AlwMoneyMachinePps.CreateSettings()
    : new DefaultProfileSettings();
GameEngine.Engine.Settings = settings;
var checker = new CoinPusherTicketCheckerPlugin(settings);

var rng = new Random(seed);
var sw = Stopwatch.StartNew();
var inputStage = Stopwatch.StartNew();
var cases = Enumerable.Range(0, count)
    .Select(i => (Index: i, Seed: rng.Next(1, int.MaxValue), Prizes: BuildInput(i, rng, inputMode, settings).ToArray()))
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
var nearMissEligibleCount = 0;
var featureTicketCount = 0;
var multiFeatureTicketCount = 0;
var sameTypeMultiFeatureTicketCount = 0;
var reTriggerTicketCount = 0;
var multiPrizeUpgradeTicketCount = 0;
var stackedPrizeUpgradeTicketCount = 0;
var spreadPrizeUpgradeTicketCount = 0;
var sameSymbolWheelTicketCount = 0;
var multiSymbolWheelTicketCount = 0;
var wheelCollectedEventCount = 0;
var wheelNextTurnCollectionCount = 0;
var wheelLaterTurnCollectionCount = 0;
var wheelResidueEventCount = 0;
var maxTurnCount = 0;

var spinCounts = new ConcurrentDictionary<int, int>();
var winSymbolCounts = new ConcurrentDictionary<int, int>();
var nearMissSymbolCounts = new ConcurrentDictionary<int, int>();
var featureCounts = new ConcurrentDictionary<string, int>();
var featureTicketCounts = new ConcurrentDictionary<string, int>();
var logicalFeatureCounts = new ConcurrentDictionary<string, int>();
var featureTurnCounts = new ConcurrentDictionary<string, int>();
var featureRelativeTurnCounts = new ConcurrentDictionary<string, int>();
var featurePhaseCounts = new ConcurrentDictionary<string, int>();
var reTriggerPairCounts = new ConcurrentDictionary<string, int>();
var pushCounts = new ConcurrentDictionary<int, int>();
var wheelSymbolCounts = new ConcurrentDictionary<int, int>();
var wheelDistinctSymbolCounts = new ConcurrentDictionary<int, int>();
var wheelTargetTypeCounts = new ConcurrentDictionary<string, int>();
var wheelStackCounts = new ConcurrentDictionary<int, int>();
var wheelAffectedCellCounts = new ConcurrentDictionary<int, int>();
var prizeUpgradeNearMissTargetCounts = new ConcurrentDictionary<int, int>();
var prizeCaseCounts = new ConcurrentDictionary<string, int>();
var nearMissEligibleByPrize = new ConcurrentDictionary<string, int>();
var nearMissHitByPrize = new ConcurrentDictionary<string, int>();
var progressLock = new object();

Console.WriteLine(
    $"Coin Pusher guarded volume audit started: tickets={count}, seed={seed}, parallel={degreeOfParallelism}, mode={inputMode}");

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
    var result = new CoinPusherTicketJsonGenerator().Generate(item.Prizes, item.Seed);
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
    var report = checker.CheckTicket(ticket);
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
    AccumulateWheelAffectedCells(report);
    var wheelLineage = AuditWheelLineage(ticket, settings);
    Interlocked.Add(ref wheelCollectedEventCount, wheelLineage.CollectedEvents);
    Interlocked.Add(ref wheelNextTurnCollectionCount, wheelLineage.NextTurnCollections);
    Interlocked.Add(ref wheelLaterTurnCollectionCount, wheelLineage.LaterTurnCollections);
    Interlocked.Add(ref wheelResidueEventCount, wheelLineage.ResidueEvents);
    if (ticket.WinInfo.WinSymbols.Length == 0) Interlocked.Increment(ref noWinCount);
    else Interlocked.Increment(ref winningCount);
    if (result.Plan.NonWinTargets.Count > 0) Interlocked.Increment(ref nearMissTicketCount);
    if (HasEligibleNearMissSymbol(result.Plan, settings))
    {
        Interlocked.Increment(ref nearMissEligibleCount);
        Increment(nearMissEligibleByPrize, prizeKey);
        if (result.Plan.NonWinTargets.Count > 0)
            Increment(nearMissHitByPrize, prizeKey);
    }
    var physicalFeatureNames = PhysicalFeatureNames(ticket, settings).ToArray();
    if (physicalFeatureNames.Length > 0) Interlocked.Increment(ref featureTicketCount);
    foreach (var featureName in physicalFeatureNames.Distinct())
        Increment(featureTicketCounts, featureName);
    if (physicalFeatureNames.Length >= 2) Interlocked.Increment(ref multiFeatureTicketCount);
    if (physicalFeatureNames.GroupBy(name => name).Any(group => group.Count() > 1))
        Interlocked.Increment(ref sameTypeMultiFeatureTicketCount);
    var reTriggerCount = ReTriggerCount(ticket);
    if (reTriggerCount > 0) Interlocked.Increment(ref reTriggerTicketCount);
    foreach (var pair in ReTriggerPairs(ticket, settings))
        Increment(reTriggerPairCounts, pair);
    var prizeUpgradeSymbols = PrizeUpgradeSymbols(ticket, settings).ToArray();
    foreach (var symbol in prizeUpgradeSymbols.Distinct())
    {
        if (result.Plan.NonWinTargets.TryGetValue(symbol, out var target))
            Increment(prizeUpgradeNearMissTargetCounts, target);
    }
    if (prizeUpgradeSymbols.Length >= 2) Interlocked.Increment(ref multiPrizeUpgradeTicketCount);
    if (prizeUpgradeSymbols.GroupBy(symbol => symbol).Any(group => group.Count() >= 2))
        Interlocked.Increment(ref stackedPrizeUpgradeTicketCount);
    if (prizeUpgradeSymbols.Distinct().Count() >= 2)
        Interlocked.Increment(ref spreadPrizeUpgradeTicketCount);
    var wheelSymbols = WheelSymbols(ticket, settings).ToArray();
    if (wheelSymbols.GroupBy(symbol => symbol).Any(group => group.Count() >= 2))
        Interlocked.Increment(ref sameSymbolWheelTicketCount);
    if (wheelSymbols.Distinct().Count() >= 2)
        Interlocked.Increment(ref multiSymbolWheelTicketCount);
    if (wheelSymbols.Length > 0)
        Increment(wheelDistinctSymbolCounts, wheelSymbols.Distinct().Count());
    foreach (var symbol in wheelSymbols)
    {
        Increment(wheelSymbolCounts, symbol);
        Increment(wheelTargetTypeCounts, WheelTargetType(symbol, result.Plan));
    }
    if (ticket.WinInfo.TotalSpins == settings.MAX_SPINS) Interlocked.Increment(ref maxTurnCount);

    Increment(spinCounts, ticket.WinInfo.TotalSpins);
    Increment(winSymbolCounts, ticket.WinInfo.WinSymbols.Length);
    Increment(nearMissSymbolCounts, result.Plan.NonWinTargets.Count);
    AccumulatePushes(ticket);
    AccumulateFeatures(ticket, settings);
    AccumulateLogicalFeatures(ticket, settings);
    AccumulateFeatureTurns(ticket, settings);

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
    $"nearMissEligible={nearMissEligibleCount}, featureTickets={featureTicketCount}, " +
    $"multiFeatureTickets={multiFeatureTicketCount}, sameTypeMultiFeatureTickets={sameTypeMultiFeatureTicketCount}, " +
    $"reTriggerTickets={reTriggerTicketCount}, " +
    $"multiPrizeUpgradeTickets={multiPrizeUpgradeTicketCount}, " +
    $"stackedPrizeUpgradeTickets={stackedPrizeUpgradeTicketCount}, " +
    $"spreadPrizeUpgradeTickets={spreadPrizeUpgradeTicketCount}, " +
    $"sameSymbolWheelTickets={sameSymbolWheelTicketCount}, " +
    $"multiSymbolWheelTickets={multiSymbolWheelTicketCount}, " +
    $"wheelCollectedEvents={wheelCollectedEventCount}, " +
    $"wheelNextTurnCollections={wheelNextTurnCollectionCount}, " +
    $"wheelLaterTurnCollections={wheelLaterTurnCollectionCount}, " +
    $"wheelResidueEvents={wheelResidueEventCount}, " +
    $"maxTurns={maxTurnCount}, elapsed={sw.Elapsed:hh\\:mm\\:ss}");
Console.WriteLine(
    $"stage totals: input={inputStage.Elapsed:hh\\:mm\\:ss\\.fff}, generation={Elapsed(generationTicks):hh\\:mm\\:ss\\.fff}, " +
    $"checker={Elapsed(checkerTicks):hh\\:mm\\:ss\\.fff}");
PrintDistribution("prize cases", prizeCaseCounts);
PrintDistribution("spins", spinCounts);
PrintDistribution("win symbol counts", winSymbolCounts);
PrintDistribution("planned near-miss symbol counts", nearMissSymbolCounts);
PrintNearMissCoverage("near-miss eligible coverage by prize", nearMissEligibleByPrize, nearMissHitByPrize);
PrintDistribution("push values", pushCounts);
PrintDistribution("feature ticket occurrence", featureTicketCounts);
PrintDistribution("features", featureCounts);
PrintDistribution("logical features", logicalFeatureCounts);
PrintDistribution("feature turns", featureTurnCounts);
PrintDistribution("feature relative turns", featureRelativeTurnCounts);
PrintDistribution("feature phases", featurePhaseCounts);
PrintDistribution("retrigger pairs", reTriggerPairCounts);
PrintDistribution("PRIZE_UPGRADE near-miss targets", prizeUpgradeNearMissTargetCounts);
PrintDistribution("wheel stack values", wheelStackCounts);
PrintDistribution("wheel affected existing cells", wheelAffectedCellCounts);
PrintDistribution("wheel target symbols", wheelSymbolCounts);
PrintDistribution("wheel distinct target count per wheel ticket", wheelDistinctSymbolCounts);
PrintDistribution("wheel target types", wheelTargetTypeCounts);

if (failureCount > 0)
{
    Console.WriteLine("Failures:");
    foreach (var failure in failures.Take(maxFailures))
        Console.WriteLine(failure);
    Environment.ExitCode = 1;
}

static int ArgInt(string[] args, int index, int fallback) =>
    args.Length > index && int.TryParse(args[index], out var value) ? value : fallback;

static string InputMode(string[] args) =>
    args
        .Where(arg => !int.TryParse(arg, out _))
        .Select(arg => arg.Trim().ToLowerInvariant())
        .FirstOrDefault(arg => arg.Length > 0)
    ?? "mixed";

static IReadOnlyList<decimal> BuildInput(
    int index,
    Random rng,
    string inputMode,
    ICustomProfileSettings settings) =>
    inputMode switch
    {
        "nowin" or "no-win" or "zero" or "0" => Array.Empty<decimal>(),
        "alw" or "pps" or "alw-pps" or "alw-random" => AlwPrizeInput(index, rng, settings),
        _ when inputMode.StartsWith("prizes=", StringComparison.OrdinalIgnoreCase) =>
            MatrixPrizeInput(index, inputMode),
        _ => VolumeAuditInputs.Build(index, rng),
    };

static IReadOnlyList<decimal> AlwPrizeInput(
    int index,
    Random rng,
    ICustomProfileSettings settings)
{
    if (index % 10 == 0)
        return Array.Empty<decimal>();

    var totals = settings.PpsCombinations
        .Select(combination => combination.TotalPrize)
        .Distinct()
        .OrderBy(total => total)
        .ToArray();
    if (totals.Length == 0)
        throw new InvalidOperationException("ALW audit mode requires PPS combinations");

    return new[] { totals[rng.Next(totals.Length)] };
}

static IReadOnlyList<decimal> MatrixPrizeInput(int index, string inputMode)
{
    var values = inputMode["prizes=".Length..]
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(value => decimal.Parse(value.Trim(), CultureInfo.InvariantCulture))
        .ToArray();
    if (values.Length == 0)
        throw new ArgumentException("prizes= mode requires at least one prize value", nameof(inputMode));

    var prize = values[index % values.Length];
    return prize == 0m ? Array.Empty<decimal>() : new[] { prize };
}

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

string? ValidateReleaseInvariants(CoinPusherTicketJsonGenerationResult result, ICustomProfileSettings settings)
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

static bool HasEligibleNearMissSymbol(GamePlan plan, ICustomProfileSettings settings) =>
    plan.FillSyms.Any(symbol => settings.SymbolFillCap(symbol) > settings.NONWIN_MIN_TARGET);

static bool HasAnyTurnFeature(TicketSerializer.TurnDto turn, ICustomProfileSettings settings) =>
    turn.Spawns.Any(spawn => spawn.Feature != null)
    || turn.Pushers.Any(pusher => pusher.FeatureId == settings.F_FLUSH_ID);

static IEnumerable<string> PhysicalFeatureNames(TicketSerializer.TicketDto ticket, ICustomProfileSettings settings)
{
    foreach (var pusher in ticket.Turns.SelectMany(turn => turn.Pushers))
    {
        if (pusher.FeatureId == settings.F_FLUSH_ID)
            yield return "FLUSH";
    }

    foreach (var feature in ticket.Turns
                 .SelectMany(turn => turn.Spawns)
                 .Select(spawn => spawn.Feature)
                 .Where(feature => feature != null))
    {
        yield return FeatureName(feature!.FeatureId, settings);
    }
}

void AccumulateWheelAffectedCells(TicketChecker.Report report)
{
    const string prefix = "existing increased cells=";
    foreach (var check in report.Checks.Where(check =>
                 check.Category == "Feature"
                 && check.Name.Contains("WHEEL fire", StringComparison.Ordinal)
                 && check.Name.EndsWith("affects board", StringComparison.Ordinal)
                 && check.Result == TicketChecker.Status.Pass))
    {
        var start = check.Detail.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0) continue;
        start += prefix.Length;
        var end = check.Detail.IndexOf(',', start);
        var value = end < 0 ? check.Detail[start..] : check.Detail[start..end];
        if (int.TryParse(value, out var affected))
            Increment(wheelAffectedCellCounts, affected);
    }
}

WheelLineageSummary AuditWheelLineage(
    TicketSerializer.TicketDto ticket,
    ICustomProfileSettings profile)
{
    var board = new WheelAuditCell?[profile.ROWS, profile.COLS];
    for (var row = 0; row < profile.ROWS; row++)
    {
        for (var col = 0; col < profile.COLS; col++)
            board[row, col] = WheelAuditCell.Normal(ticket.StartingBoard[row][col].Id, 1);
    }

    var fireTurns = new Dictionary<int, int>();
    var collectionTurns = new Dictionary<int, int>();
    var nextEventId = 1;

    for (var turnIndex = 0; turnIndex < ticket.Turns.Length; turnIndex++)
    {
        var turnNumber = turnIndex + 1;
        var turn = ticket.Turns[turnIndex];

        for (var row = 0; row < profile.ROWS; row++)
        {
            for (var col = 0; col < profile.COLS; col++)
            {
                if (board[row, col]?.Feature != null)
                    board[row, col] = ConvertWheelAuditFeature(board[row, col]!.Feature!, profile, null);
            }
        }

        for (var col = 0; col < profile.COLS; col++)
        {
            var pusher = turn.Pushers[col];
            if (pusher.FeatureId == profile.F_FLUSH_ID)
            {
                for (var row = 0; row < profile.ROWS; row++)
                {
                    CollectWheelAuditCell(board[row, col], turnNumber, collectionTurns);
                    board[row, col] = null;
                }
            }
            else
            {
                var push = pusher.PushValue;
                for (var row = profile.ROWS - push; row < profile.ROWS; row++)
                    CollectWheelAuditCell(board[row, col], turnNumber, collectionTurns);
                for (var row = profile.ROWS - 1; row >= 0; row--)
                {
                    var source = row - push;
                    board[row, col] = source >= 0 ? board[source, col] : null;
                }
            }
        }

        board = RotateWheelAuditBoard(board, profile);
        foreach (var spawn in turn.Spawns)
        {
            var row = spawn.Pos / profile.COLS;
            var col = spawn.Pos % profile.COLS;
            board[row, col] = spawn.Feature == null
                ? WheelAuditCell.Normal(spawn.Id, spawn.Stack ?? 1)
                : WheelAuditCell.FeatureCell(spawn.Feature);
        }

        FireWheelAuditPass(board, profile, turnNumber, wheelPass: false, fireTurns, ref nextEventId);
        FireWheelAuditPass(board, profile, turnNumber, wheelPass: true, fireTurns, ref nextEventId);
    }

    var nextTurn = 0;
    var laterTurn = 0;
    foreach (var item in collectionTurns)
    {
        if (item.Value == fireTurns[item.Key] + 1) nextTurn++;
        else laterTurn++;
    }

    return new WheelLineageSummary(
        fireTurns.Count,
        collectionTurns.Count,
        nextTurn,
        laterTurn,
        fireTurns.Count - collectionTurns.Count);
}

void FireWheelAuditPass(
    WheelAuditCell?[,] board,
    ICustomProfileSettings profile,
    int turn,
    bool wheelPass,
    Dictionary<int, int> fireTurns,
    ref int nextEventId)
{
    bool fired;
    do
    {
        fired = false;
        for (var row = 0; row < profile.ROWS; row++)
        {
            for (var col = 0; col < profile.COLS; col++)
            {
                var cell = board[row, col];
                var feature = cell?.Feature;
                if (feature == null || (feature.FeatureId == profile.F_WHEEL) != wheelPass)
                    continue;

                int? wheelEventId = null;
                if (wheelPass)
                {
                    wheelEventId = nextEventId++;
                    fireTurns[wheelEventId.Value] = turn;
                    var stackAdd = feature.WheelStackValue.GetValueOrDefault();
                    var wheelSymbol = feature.WheelSymbolId.GetValueOrDefault();
                    for (var targetRow = 0; targetRow < profile.ROWS; targetRow++)
                    {
                        for (var targetCol = 0; targetCol < profile.COLS; targetCol++)
                        {
                            var target = board[targetRow, targetCol];
                            if (target?.Feature != null || target?.Symbol != wheelSymbol)
                                continue;
                            target.Stack = Math.Min(profile.MAX_COIN_STACK, target.Stack + stackAdd);
                            target.WheelEventIds.Add(wheelEventId.Value);
                        }
                    }
                }

                board[row, col] = feature.ReTrigger is { Length: > 0 }
                    ? WheelAuditCell.FeatureCell(feature.ReTrigger[0])
                    : ConvertWheelAuditFeature(feature, profile, wheelEventId);
                fired = true;
            }
        }
    }
    while (fired && WheelAuditBoardHasFeature(board, profile, wheelPass));
}

bool WheelAuditBoardHasFeature(
    WheelAuditCell?[,] board,
    ICustomProfileSettings profile,
    bool wheelPass)
{
    foreach (var cell in board)
    {
        if (cell?.Feature != null && (cell.Feature.FeatureId == profile.F_WHEEL) == wheelPass)
            return true;
    }
    return false;
}

WheelAuditCell ConvertWheelAuditFeature(
    TicketSerializer.FeatureDto feature,
    ICustomProfileSettings profile,
    int? wheelEventId)
{
    var final = feature;
    while (profile.IsFeat(final.ConvertToId) && final.ReTrigger is { Length: > 0 })
        final = final.ReTrigger[0];

    var cell = WheelAuditCell.Normal(final.ConvertToId, 1);
    if (feature.FeatureId == profile.F_WHEEL
        && feature.WheelSymbolId == final.ConvertToId
        && wheelEventId.HasValue)
    {
        cell.Stack = Math.Min(profile.MAX_COIN_STACK, feature.WheelStackValue.GetValueOrDefault() + 1);
        cell.WheelEventIds.Add(wheelEventId.Value);
    }
    return cell;
}

void CollectWheelAuditCell(
    WheelAuditCell? cell,
    int turn,
    Dictionary<int, int> collectionTurns)
{
    if (cell == null) return;
    foreach (var eventId in cell.WheelEventIds)
    {
        if (!collectionTurns.ContainsKey(eventId))
            collectionTurns[eventId] = turn;
    }
}

WheelAuditCell?[,] RotateWheelAuditBoard(
    WheelAuditCell?[,] source,
    ICustomProfileSettings profile)
{
    var rotated = new WheelAuditCell?[profile.ROWS, profile.COLS];
    for (var row = 0; row < profile.ROWS; row++)
    {
        for (var col = 0; col < profile.COLS; col++)
            rotated[col, profile.ROWS - 1 - row] = source[row, col];
    }
    return rotated;
}

static int CountReTriggers(TicketSerializer.FeatureDto feature) =>
    (feature.ReTrigger?.Length ?? 0)
    + (feature.ReTrigger ?? Array.Empty<TicketSerializer.FeatureDto>()).Sum(CountReTriggers);

static int ReTriggerCount(TicketSerializer.TicketDto ticket) =>
    ticket.Turns
        .SelectMany(turn => turn.Spawns)
        .Select(spawn => spawn.Feature)
        .Where(feature => feature != null)
        .Sum(feature => CountReTriggers(feature!));

static IEnumerable<string> ReTriggerPairs(TicketSerializer.TicketDto ticket, ICustomProfileSettings settings)
{
    foreach (var feature in ticket.Turns
                 .SelectMany(turn => turn.Spawns)
                 .Select(spawn => spawn.Feature)
                 .Where(feature => feature != null))
    {
        foreach (var pair in ReTriggerPairsFromFeature(feature!, settings))
            yield return pair;
    }
}

static IEnumerable<string> ReTriggerPairsFromFeature(TicketSerializer.FeatureDto feature, ICustomProfileSettings settings)
{
    var parent = FeatureName(feature.FeatureId, settings);
    foreach (var child in feature.ReTrigger ?? Array.Empty<TicketSerializer.FeatureDto>())
    {
        yield return $"{parent}->{FeatureName(child.FeatureId, settings)}";
        foreach (var nested in ReTriggerPairsFromFeature(child, settings))
            yield return nested;
    }
}

static IEnumerable<int> PrizeUpgradeSymbols(TicketSerializer.TicketDto ticket, ICustomProfileSettings settings)
{
    foreach (var feature in ticket.Turns
                 .SelectMany(turn => turn.Spawns)
                 .Select(spawn => spawn.Feature)
                 .Where(feature => feature != null))
    {
        foreach (var symbol in PrizeUpgradeSymbolsFromFeature(feature!, settings))
            yield return symbol;
    }
}

static IEnumerable<int> PrizeUpgradeSymbolsFromFeature(TicketSerializer.FeatureDto feature, ICustomProfileSettings settings)
{
    if (feature.FeatureId == settings.F_PRUP && feature.UpgradeSymbolId.HasValue)
        yield return feature.UpgradeSymbolId.Value;

    foreach (var child in feature.ReTrigger ?? Array.Empty<TicketSerializer.FeatureDto>())
    {
        foreach (var symbol in PrizeUpgradeSymbolsFromFeature(child, settings))
            yield return symbol;
    }
}

static IEnumerable<int> WheelSymbols(TicketSerializer.TicketDto ticket, ICustomProfileSettings settings)
{
    foreach (var feature in ticket.Turns
                 .SelectMany(turn => turn.Spawns)
                 .Select(spawn => spawn.Feature)
                 .Where(feature => feature != null))
    {
        foreach (var symbol in WheelSymbolsFromFeature(feature!, settings))
            yield return symbol;
    }
}

static IEnumerable<int> WheelSymbolsFromFeature(TicketSerializer.FeatureDto feature, ICustomProfileSettings settings)
{
    if (feature.FeatureId == settings.F_WHEEL && feature.WheelSymbolId.HasValue)
        yield return feature.WheelSymbolId.Value;

    foreach (var child in feature.ReTrigger ?? Array.Empty<TicketSerializer.FeatureDto>())
    {
        foreach (var symbol in WheelSymbolsFromFeature(child, settings))
            yield return symbol;
    }
}

static string WheelTargetType(int symbol, GamePlan plan)
{
    if (plan.Targets.ContainsKey(symbol)) return "win";
    if (plan.NonWinTargets.ContainsKey(symbol)) return "nearMiss";
    return "filler";
}

void AccumulatePushes(TicketSerializer.TicketDto ticket)
{
    foreach (var pusher in ticket.Turns.SelectMany(turn => turn.Pushers))
        Increment(pushCounts, pusher.PushValue);
}

void AccumulateFeatures(TicketSerializer.TicketDto ticket, ICustomProfileSettings settings)
{
    foreach (var pusher in ticket.Turns.SelectMany(turn => turn.Pushers))
    {
        if (pusher.FeatureId == settings.F_FLUSH_ID)
            Increment(featureCounts, "FLUSH");
    }

    foreach (var feature in ticket.Turns.SelectMany(turn => turn.Spawns).Select(spawn => spawn.Feature).Where(feature => feature != null))
    {
        var name = FeatureName(feature!.FeatureId, settings);
        Increment(featureCounts, name);

        if (feature.FeatureId == settings.F_WHEEL && feature.WheelStackValue.HasValue)
            Increment(wheelStackCounts, feature.WheelStackValue.Value);
    }
}

void AccumulateLogicalFeatures(TicketSerializer.TicketDto ticket, ICustomProfileSettings settings)
{
    foreach (var pusher in ticket.Turns.SelectMany(turn => turn.Pushers))
    {
        if (pusher.FeatureId == settings.F_FLUSH_ID)
            Increment(logicalFeatureCounts, "FLUSH");
    }

    foreach (var feature in ticket.Turns
                 .SelectMany(turn => turn.Spawns)
                 .Select(spawn => spawn.Feature)
                 .Where(feature => feature != null))
    {
        AccumulateFeatureTree(feature!, settings);
    }
}

void AccumulateFeatureTurns(TicketSerializer.TicketDto ticket, ICustomProfileSettings settings)
{
    for (var index = 0; index < ticket.Turns.Length; index++)
    {
        var turnNumber = index + 1;
        var relativeTurn = ticket.Turns.Length - turnNumber;
        var phase = turnNumber <= settings.BASE_SPINS ? "base" : "extra";

        foreach (var pusher in ticket.Turns[index].Pushers)
        {
            if (pusher.FeatureId == settings.F_FLUSH_ID)
                Record("FLUSH");
        }

        foreach (var feature in ticket.Turns[index].Spawns
                     .Select(spawn => spawn.Feature)
                     .Where(feature => feature != null))
        {
            Record(FeatureName(feature!.FeatureId, settings));
        }

        void Record(string featureName)
        {
            Increment(featureTurnCounts, $"{featureName}@{turnNumber}");
            Increment(featureRelativeTurnCounts, $"{featureName}@T-{relativeTurn}");
            Increment(featurePhaseCounts, $"{featureName}@{phase}");
        }
    }
}

void AccumulateFeatureTree(TicketSerializer.FeatureDto feature, ICustomProfileSettings settings)
{
    Increment(logicalFeatureCounts, FeatureName(feature.FeatureId, settings));
    foreach (var child in feature.ReTrigger ?? Array.Empty<TicketSerializer.FeatureDto>())
        AccumulateFeatureTree(child, settings);
}

static string FeatureName(int featureId, ICustomProfileSettings settings) =>
    featureId == settings.F_WHEEL ? "WHEEL"
    : featureId == settings.F_XSPIN ? "EXTRA_GO"
    : featureId == settings.F_PRUP ? "PRIZE_UPGRADE"
    : $"FEATURE_{featureId}";

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

static void PrintNearMissCoverage(
    string label,
    ConcurrentDictionary<string, int> eligible,
    ConcurrentDictionary<string, int> hits)
{
    var text = string.Join(", ", eligible
        .OrderBy(kv => kv.Key)
        .Select(kv =>
        {
            var hit = hits.GetValueOrDefault(kv.Key);
            return $"{kv.Key}={hit}/{kv.Value}";
        }));
    Console.WriteLine($"{label}: {text}");
}

static TimeSpan Elapsed(long ticks) =>
    TimeSpan.FromSeconds(ticks / (double)Stopwatch.Frequency);

static string PrizeKey(IReadOnlyList<decimal> prizes) =>
    prizes.Count == 0 ? "0" : string.Join(",", prizes);

internal sealed class WheelAuditCell
{
    private WheelAuditCell(int symbol, int stack, TicketSerializer.FeatureDto? feature)
    {
        Symbol = symbol;
        Stack = stack;
        Feature = feature;
    }

    internal int Symbol { get; }
    internal int Stack { get; set; }
    internal TicketSerializer.FeatureDto? Feature { get; }
    internal HashSet<int> WheelEventIds { get; } = new();

    internal static WheelAuditCell Normal(int symbol, int stack) =>
        new(symbol, stack, null);

    internal static WheelAuditCell FeatureCell(TicketSerializer.FeatureDto feature) =>
        new(feature.FeatureId, 1, feature);
}

internal readonly struct WheelLineageSummary
{
    internal WheelLineageSummary(
        int totalEvents,
        int collectedEvents,
        int nextTurnCollections,
        int laterTurnCollections,
        int residueEvents)
    {
        TotalEvents = totalEvents;
        CollectedEvents = collectedEvents;
        NextTurnCollections = nextTurnCollections;
        LaterTurnCollections = laterTurnCollections;
        ResidueEvents = residueEvents;
    }

    internal int TotalEvents { get; }
    internal int CollectedEvents { get; }
    internal int NextTurnCollections { get; }
    internal int LaterTurnCollections { get; }
    internal int ResidueEvents { get; }
}
