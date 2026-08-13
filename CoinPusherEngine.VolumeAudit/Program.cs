using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using CoinPusherEngine;
using CoinPusherEngine.VolumeAudit;

var hasMode = args.Length > 0 && IsKnownMode(args[0]);
var mode = hasMode ? args[0].Trim().ToLowerInvariant() : "random";
if (mode == "inspect")
{
    InspectCase(args);
    return;
}

var alwMode = IsAlwMode(mode);
var matrixMode = IsMatrixMode(mode);
var argOffset = hasMode ? 1 : 0;
var settings = alwMode
    ? AlwMoneyMachinePps.CreateSettings()
    : new GameEngine.DefaultCoinPusherSettings();
GameEngine.Engine.Settings = settings;
var checker = new CoinPusherTicketCheckerPlugin();
var inputStage = Stopwatch.StartNew();
var matrixInputSet = matrixMode
    ? PrizeMatrixAuditInputs.Build(settings)
    : null;
var matrixCases = matrixInputSet?.Cases;
inputStage.Stop();

var count = matrixMode ? matrixCases!.Count : ArgInt(args, argOffset, 1000);
var seedArgIndex = matrixMode ? argOffset : argOffset + 1;
var progressArgIndex = seedArgIndex + 1;
var seed = ArgInt(args, seedArgIndex, 20260806);
var progressEvery = Math.Max(1, ArgInt(args, progressArgIndex, Math.Max(1, count / 20)));
var maxFailures = Math.Max(1, ArgInt(args, progressArgIndex + 1, 20));
var degreeOfParallelism = Math.Max(1, ArgInt(args, progressArgIndex + 2, Environment.ProcessorCount));
var matrixUniqueMultisets = matrixCases?.Select(item => item.SortedKey).Distinct().Count() ?? 0;
var matrixUpgradePermutationCases = matrixCases?.Count(item => item.HasUpgradeOrigin) ?? 0;
var sw = Stopwatch.StartNew();
using var generatorLocal = new ThreadLocal<CoinPusherTicketJsonGenerator>(() => new CoinPusherTicketJsonGenerator());

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
var maxTurnCount = 0;

var spinCounts = new ConcurrentDictionary<int, int>();
var winSymbolCounts = new ConcurrentDictionary<int, int>();
var nearMissSymbolCounts = new ConcurrentDictionary<int, int>();
var featureCounts = new ConcurrentDictionary<string, int>();
var pushCounts = new ConcurrentDictionary<int, int>();
var poppedCellCounts = new ConcurrentDictionary<int, int>();
var wheelStackCounts = new ConcurrentDictionary<int, int>();
var prizeCaseCounts = new ConcurrentDictionary<string, int>();
var nearMissEligibleByPrize = new ConcurrentDictionary<string, int>();
var nearMissHitByPrize = new ConcurrentDictionary<string, int>();
var progressLock = new object();

Console.WriteLine(
    $"Coin Pusher guarded volume audit started: mode={mode}, tickets={count}, seed={seed}, parallel={degreeOfParallelism}");
if (matrixMode)
{
    Console.WriteLine(
        $"prize matrix: uniqueMultisets={matrixInputSet!.UniqueMultisetCount}, " +
        $"feasibleUniqueMultisets={matrixInputSet.FeasibleUniqueMultisetCount}, " +
        $"infeasibleUniqueMultisets={matrixInputSet.InfeasibleUniqueMultisetCount}, " +
        $"permutationCases={matrixCases!.Count}, upgradeOriginPermutationCases={matrixUpgradePermutationCases}, " +
        $"originSymbolTierStates={matrixInputSet.OriginSymbolTierStateCount}, " +
        $"feasibleOriginSymbolTierStates={matrixInputSet.FeasibleOriginSymbolTierStateCount}");
    if (matrixInputSet.InfeasibleExamples.Count > 0)
    {
        Console.WriteLine(
            "infeasible prize multiset examples: " +
            string.Join(" | ", matrixInputSet.InfeasibleExamples));
    }
}

Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = degreeOfParallelism }, (i, state) =>
{
    if (Volatile.Read(ref failureCount) >= maxFailures)
    {
        state.Stop();
        return;
    }

    var item = BuildCase(i);
    var prizeKey = PrizeKey(item.Prizes);
    Increment(prizeCaseCounts, prizeKey);

    var generationStage = Stopwatch.StartNew();
    var result = generatorLocal.Value!.Generate(item.Prizes, item.Seed);
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

    var invariantFailure = ValidateReleaseInvariants(result);
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
    if (HasEligibleNearMissSymbol(result.Plan))
    {
        Interlocked.Increment(ref nearMissEligibleCount);
        Increment(nearMissEligibleByPrize, prizeKey);
        if (result.Plan.NonWinTargets.Count > 0)
            Increment(nearMissHitByPrize, prizeKey);
    }
    if (HasAnyTicketFeature(ticket)) Interlocked.Increment(ref featureTicketCount);
    if (ticket.WinInfo.TotalSpins == settings.MAX_SPINS) Interlocked.Increment(ref maxTurnCount);

    Increment(spinCounts, ticket.WinInfo.TotalSpins);
    Increment(winSymbolCounts, ticket.WinInfo.WinSymbols.Length);
    Increment(nearMissSymbolCounts, result.Plan.NonWinTargets.Count);
    AccumulatePushes(ticket);
    AccumulateFeatures(ticket);

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
PrintDistribution("popped cells per spin", poppedCellCounts);
PrintDistribution("features", featureCounts);
PrintDistribution("wheel stack values", wheelStackCounts);

if (failureCount > 0)
{
    Console.WriteLine("Failures:");
    foreach (var failure in failures.Take(maxFailures))
        Console.WriteLine(failure);
    Environment.ExitCode = 1;
}

bool IsKnownMode(string value) =>
    IsMatrixMode(value)
    || IsAlwMode(value)
    || value.Trim().Equals("random", StringComparison.OrdinalIgnoreCase)
    || value.Trim().Equals("inspect", StringComparison.OrdinalIgnoreCase);

bool IsAlwMode(string value)
{
    var normalized = value.Trim().ToLowerInvariant();
    return normalized == "alw"
        || normalized == "alw-random"
        || normalized == "pps"
        || normalized == "alw-pps";
}

bool IsMatrixMode(string value)
{
    var normalized = value.Trim().ToLowerInvariant();
    return normalized == "matrix"
        || normalized == "exhaustive"
        || normalized == "prizematrix"
        || normalized == "prize-matrix";
}

(int Index, int Seed, decimal[] Prizes) BuildCase(int index)
{
    if (matrixMode)
    {
        var matrixCase = matrixCases![index];
        return (index, VolumeSeed(seed, index), matrixCase.Prizes.ToArray());
    }

    var prizeRng = new Random(VolumeSeed(seed ^ 0x5f3759df, index));
    if (alwMode)
        return (index, VolumeSeed(seed, index), BuildAlwCase(index, prizeRng).ToArray());

    return (index, VolumeSeed(seed, index), VolumeAuditInputs.Build(index, prizeRng).ToArray());
}

IReadOnlyList<decimal> BuildAlwCase(int index, Random rng)
{
    if (index % 10 == 0)
        return Array.Empty<decimal>();

    var totals = Settings.PpsCombinations
        .Select(combination => combination.TotalPrize)
        .Distinct()
        .OrderBy(total => total)
        .ToArray();
    return new[] { totals[rng.Next(totals.Length)] };
}

static int ArgInt(string[] args, int index, int fallback) =>
    args.Length > index && int.TryParse(args[index], out var value) ? value : fallback;

static int VolumeSeed(int seed, int index)
{
    unchecked
    {
        var hash = seed;
        hash = (hash * 397) ^ index;
        hash ^= 0x6d2b79f5;
        return hash == int.MinValue ? 0 : Math.Abs(hash);
    }
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

string? ValidateReleaseInvariants(CoinPusherTicketJsonGenerationResult result)
{
    var ticket = result.Ticket!;
    var plan = result.Plan!;

    if (ticket.WinInfo.TotalSpins != ticket.Turns.Length || ticket.WinInfo.TotalSpins != plan.TotalSpins)
    {
        return $"spin count mismatch ticket TotalSpins={ticket.WinInfo.TotalSpins}, turns={ticket.Turns.Length}, plan={plan.TotalSpins}";
    }

    if (ticket.Turns.Length > 0 && HasAnyTurnFeature(ticket.Turns[^1]))
        return "last turn contains a board feature or FLUSH pusher";

    var lowNearMiss = plan.NonWinTargets.FirstOrDefault(kv => kv.Value < Settings.NONWIN_MIN_TARGET);
    if (lowNearMiss.Key != 0)
        return $"planned near-miss symbol {lowNearMiss.Key} target {lowNearMiss.Value} is below minimum {Settings.NONWIN_MIN_TARGET}";

    var illegalPush = ticket.Turns
        .SelectMany(turn => turn.Pushers)
        .FirstOrDefault(pusher => !pusher.FeatureId.HasValue
            && (pusher.PushValue < Settings.MIN_PUSH || pusher.PushValue > Settings.MAX_PUSH));
    if (illegalPush != null)
        return $"illegal normal push value {illegalPush.PushValue}";

    var illegalFlush = ticket.Turns
        .SelectMany(turn => turn.Pushers)
        .FirstOrDefault(pusher => pusher.FeatureId == Settings.F_FLUSH_ID && pusher.PushValue != Settings.ROWS);
    if (illegalFlush != null)
        return $"FLUSH pusher has PushValue={illegalFlush.PushValue}, expected {Settings.ROWS}";

    var illegalWheelStack = ticket.Turns
        .SelectMany(turn => turn.Spawns)
        .Where(spawn => spawn.Feature?.FeatureId == Settings.F_WHEEL)
        .Select(spawn => spawn.Feature!.WheelStackValue)
        .FirstOrDefault(stack => stack.HasValue
            && (stack.Value < Settings.MIN_WHEEL_STACK_VALUE || stack.Value > Settings.MAX_WHEEL_STACK_VALUE));
    if (illegalWheelStack.HasValue)
        return $"WHEEL stack value {illegalWheelStack.Value} outside configured range";

    return null;
}

static string FirstFailures(TicketChecker.Report report) =>
    string.Join(" | ", report.Checks
        .Where(c => c.Result == TicketChecker.Status.Fail)
        .Select(c => $"{c.Category}/{c.Name}: {c.Detail}")
        .Take(5));

static bool HasAnyTicketFeature(TicketSerializer.TicketDto ticket) =>
    ticket.Turns.Any(turn => HasAnyTurnFeature(turn));

static bool HasEligibleNearMissSymbol(GamePlan plan) =>
    plan.FillSyms.Any(symbol => Settings.SymbolFillCap(symbol) > Settings.NONWIN_MIN_TARGET);

static bool HasAnyTurnFeature(TicketSerializer.TurnDto turn) =>
    turn.Spawns.Any(spawn => spawn.Feature != null)
    || turn.Pushers.Any(pusher => pusher.FeatureId == Settings.F_FLUSH_ID);

void AccumulatePushes(TicketSerializer.TicketDto ticket)
{
    foreach (var turn in ticket.Turns)
    {
        var poppedCells = 0;
        foreach (var pusher in turn.Pushers)
        {
            Increment(pushCounts, pusher.PushValue);
            poppedCells += pusher.PushValue;
        }

        Increment(poppedCellCounts, poppedCells);
    }
}

void AccumulateFeatures(TicketSerializer.TicketDto ticket)
{
    foreach (var pusher in ticket.Turns.SelectMany(turn => turn.Pushers))
    {
        if (pusher.FeatureId == Settings.F_FLUSH_ID)
            Increment(featureCounts, "FLUSH");
    }

    foreach (var feature in ticket.Turns.SelectMany(turn => turn.Spawns).Select(spawn => spawn.Feature).Where(feature => feature != null))
    {
        var name = feature!.FeatureId == Settings.F_WHEEL ? "WHEEL"
            : feature.FeatureId == Settings.F_XSPIN ? "EXTRA_GO"
            : feature.FeatureId == Settings.F_PRUP ? "PRIZE_UPGRADE"
            : $"FEATURE_{feature.FeatureId}";
        Increment(featureCounts, name);

        if (feature.FeatureId == settings.F_WHEEL && feature.WheelStackValue.HasValue)
            Increment(wheelStackCounts, feature.WheelStackValue.Value);
    }
}

static void Increment<TKey>(ConcurrentDictionary<TKey, int> dictionary, TKey key)
    where TKey : notnull =>
    dictionary.AddOrUpdate(key, 1, (_, count) => count + 1);

static void PrintDistribution<TKey>(string label, ConcurrentDictionary<TKey, int> dictionary, int maxItems = 80)
    where TKey : notnull
{
    var ordered = dictionary
        .OrderBy(kv => kv.Key)
        .ToArray();
    var text = string.Join(", ", ordered
        .Take(maxItems)
        .Select(kv => $"{kv.Key}={kv.Value}"));
    var suffix = ordered.Length > maxItems
        ? $", ... ({ordered.Length - maxItems} more)"
        : "";
    Console.WriteLine($"{label}: {text}{suffix}");
}

static void PrintNearMissCoverage(
    string label,
    ConcurrentDictionary<string, int> eligible,
    ConcurrentDictionary<string, int> hits,
    int maxItems = 80)
{
    var ordered = eligible
        .OrderBy(kv => kv.Key)
        .ToArray();
    var text = string.Join(", ", ordered
        .Take(maxItems)
        .Select(kv =>
        {
            var hit = hits.GetValueOrDefault(kv.Key);
            return $"{kv.Key}={hit}/{kv.Value}";
        }));
    var suffix = ordered.Length > maxItems
        ? $", ... ({ordered.Length - maxItems} more)"
        : "";
    Console.WriteLine($"{label}: {text}{suffix}");
}

static TimeSpan Elapsed(long ticks) =>
    TimeSpan.FromSeconds(ticks / (double)Stopwatch.Frequency);

static string PrizeKey(IReadOnlyList<decimal> prizes) =>
    prizes.Count == 0 ? "0" : string.Join(",", prizes);

static void InspectCase(string[] args)
{
    var settings = new GameEngine.DefaultCoinPusherSettings();
    GameEngine.Engine.Settings = settings;
    var prizes = args.Length > 1
        ? ParsePrizeList(args[1])
        : Array.Empty<decimal>();
    var seed = ArgInt(args, 2, 20260812);

    int SeedFor(string scope)
    {
        unchecked
        {
            var hash = seed;
            foreach (var ch in scope)
                hash = (hash * 397) ^ ch;
            return hash == int.MinValue ? 0 : Math.Abs(hash);
        }
    }

    Console.WriteLine($"inspect seed={seed} prizes=[{PrizeKey(prizes)}]");
    var math = new ForwardMathInputResolver().Resolve(prizes, SeedFor("math"));
    Console.WriteLine($"math {math.Status}: {math.Detail}");
    if (!math.IsValid) return;
    Console.WriteLine($"covered=[{PrizeKey(math.Bundle!.Covered)}] skipped=[{PrizeKey(math.Bundle.Skipped)}]");
    Console.WriteLine($"targets={Map(math.Bundle.Input.Targets)} required={Map(math.Bundle.Input.Required)}");

    var objectives = new ForwardObjectivePlanner().Resolve(math.Bundle.Input, SeedFor("objectives"));
    Console.WriteLine($"objectives {objectives.Status}: {objectives.Detail}");
    if (!objectives.IsValid) return;
    Console.WriteLine($"wins={Map(objectives.Objectives!.WinTargets)} near={Map(objectives.Objectives.NearMissTargets)} fill=[{string.Join(",", objectives.Objectives.FillSymbols)}]");

    var budget = new ForwardFeatureBudgetPlanner().Plan(math.Bundle.Input, objectives.Objectives, SeedFor("budget"));
    Console.WriteLine($"budget {budget.Status}: {budget.Detail}");
    if (!budget.IsValid) return;
    Console.WriteLine($"turns={budget.Budget!.TotalTurns} wheel={budget.Budget.WheelCount} flush={budget.Budget.FlushCount} extraGo={budget.Budget.ExtraGoCount} prup={budget.Budget.PrizeUpgradeCount}");

    var timing = new ForwardFeatureTimingPlanner(SeedFor("timing")).Plan(budget.Budget, objectives.Objectives);
    Console.WriteLine($"timing {timing.Status}: {timing.Detail}");
    if (!timing.IsValid) return;
    Console.WriteLine("timed=" + string.Join(", ", timing.Timing!.Events.Select(e => $"{e.Turn}:{e.Kind}")));

    var intents = new ForwardFeatureIntentPlanner(SeedFor("intents")).Plan(objectives.Objectives, timing.Timing);
    Console.WriteLine($"intents {intents.Status}: {intents.Detail}");
    if (!intents.IsValid) return;
    Console.WriteLine("intents=" + string.Join(", ", intents.Plan!.Intents.Select(DescribeIntent)));

    var frames = new ForwardTurnFramePlanner(SeedFor("frames")).Plan(budget.Budget, intents.Plan, objectives.Objectives);
    Console.WriteLine($"frames {frames.Status}: {frames.Detail}");
    if (!frames.IsValid) return;
    foreach (var frame in frames.Plan!.Frames)
    {
        Console.WriteLine(
            $"turn {frame.Turn}: pushes=[{string.Join(",", frame.Shape.Pushers.Select(p => p.FeatureId.HasValue ? $"{p.PushValue}/{p.FeatureId}" : p.PushValue.ToString()))}] popped={frame.Shape.PoppedCellCount} " +
            $"flushCols=[{string.Join(",", frame.FlushColumns.OrderBy(x => x))}] features=[{string.Join(",", frame.FeatureIntents.Select(DescribeIntent))}]");
    }

    var finalized = new ForwardObjectiveFinalizer().Finalize(
        objectives.Objectives,
        intents.Plan,
        frames.Plan);
    Console.WriteLine($"finalized {finalized.Status}: {finalized.Detail}");
    if (!finalized.IsValid) return;

    TracePipeline(settings, finalized.Objectives!, frames.Plan, SeedFor("pipeline"));
}

static decimal[] ParsePrizeList(string value)
{
    if (string.IsNullOrWhiteSpace(value) || value.Trim() == "0")
        return Array.Empty<decimal>();

    return value
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => decimal.Parse(part.Trim()))
        .ToArray();
}

static string Map<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> map)
    where TKey : notnull =>
    string.Join(",", map.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));

static string DescribeIntent(ForwardFeatureIntent intent)
{
    return intent.Kind switch
    {
        ForwardTimedFeatureKind.Wheel => $"{intent.Turn}:WHEEL sym={intent.WheelSymbol} stack={intent.WheelStackValue}",
        ForwardTimedFeatureKind.PrizeUpgrade => $"{intent.Turn}:PRUP sym={intent.UpgradeSymbol} tier={intent.UpgradeTier} value={intent.UpgradePrizeValue}",
        _ => $"{intent.Turn}:{intent.Kind}",
    };
}

static void TracePipeline(
    GameEngine.ICustomProfileSettings settings,
    ForwardObjectives objectives,
    ForwardTurnFramePlan framePlan,
    int pipelineSeed)
{
    int PipelineSeedFor(string scope, int turn)
    {
        unchecked
        {
            var hash = pipelineSeed;
            foreach (var ch in scope)
                hash = (hash * 397) ^ ch;
            hash = (hash * 397) ^ turn;
            return hash == int.MinValue ? 0 : Math.Abs(hash);
        }
    }

    var symbolLedger = new SymbolLedger(
        objectives.WinTargets,
        objectives.NearMissTargets,
        objectives.MaxSymbol);
    var extraSpinLedger = new ForwardExtraSpinLedger(framePlan.TotalTurns);
    var prizeUpgradeLedger = new ForwardPrizeUpgradeLedger(
        objectives.PrizeTiers.Concat(objectives.NonWinPrizeTiers).ToDictionary(kv => kv.Key, kv => kv.Value),
        objectives.PrizeValues,
        objectives.MaxSymbol);
    var starting = new ForwardStartingBoardPlanner(PipelineSeedFor("start", 0)).Plan(
        objectives,
        framePlan,
        symbolLedger);
    Console.WriteLine($"trace start {starting.Status}: {starting.Detail} planned=[{Map(symbolLedger.Collected)}]");
    if (!starting.IsValid || starting.Board == null) return;

    var board = new ForwardBoardState(starting.Board);
    var actual = new Dictionary<int, int>();
    var remainingCapacity = new Dictionary<ForwardFeatureKind, int>
    {
        [ForwardFeatureKind.Wheel] = framePlan.Frames.Sum(frame => frame.FeatureIntents.Count(intent => intent.Kind == ForwardTimedFeatureKind.Wheel)),
        [ForwardFeatureKind.ExtraGo] = framePlan.Frames.Sum(frame => frame.FeatureIntents.Count(intent => intent.Kind == ForwardTimedFeatureKind.ExtraGo)),
        [ForwardFeatureKind.PrizeUpgrade] = framePlan.Frames.Sum(frame => frame.FeatureIntents.Count(intent => intent.Kind == ForwardTimedFeatureKind.PrizeUpgrade)),
    };

    foreach (var frame in framePlan.Frames.OrderBy(frame => frame.Turn))
    {
        var cycle = new ForwardTurnCycleExecutor(PipelineSeedFor("turn", frame.Turn)).ExecuteAndAdvance(
            frame,
            framePlan.TotalTurns,
            board,
            objectives,
            framePlan.FutureTurnsAfter(frame.Turn),
            remainingCapacity,
            symbolLedger,
            extraSpinLedger,
            prizeUpgradeLedger);
        if (!cycle.IsValid)
        {
            Console.WriteLine($"trace turn {frame.Turn} failed {cycle.Status}: {cycle.Detail}");
            return;
        }

        foreach (var (symbol, count) in cycle.Realization!.Collected)
            actual[symbol] = actual.GetValueOrDefault(symbol) + count;

        foreach (var group in frame.FeatureIntents.Select(ToFeatureKind).Where(kind => kind.HasValue).GroupBy(kind => kind!.Value))
            remainingCapacity[group.Key] -= group.Count();

        var featureText = string.Join(", ", cycle.Realization.Spawns
            .Where(spawn => spawn.Cell.IsFeat)
            .Select(spawn => $"{spawn.Row},{spawn.Col}:id={spawn.Cell.Sym},cvt={spawn.Cell.CvtSym},wheel={spawn.Cell.Fp?.WheelSym},stack={spawn.Cell.Fp?.WheelStack},prup={spawn.Cell.Fp?.PrupSym}/{spawn.Cell.Fp?.PrupTier}"));
        Console.WriteLine(
            $"trace turn {frame.Turn}: actualTurn=[{Map(cycle.Realization.Collected)}] " +
            $"actualTotal=[{Map(actual)}] planned=[{Map(symbolLedger.Collected)}] features=[{featureText}]");
    }
}

static ForwardFeatureKind? ToFeatureKind(ForwardFeatureIntent intent) =>
    intent.Kind switch
    {
        ForwardTimedFeatureKind.Wheel => ForwardFeatureKind.Wheel,
        ForwardTimedFeatureKind.ExtraGo => ForwardFeatureKind.ExtraGo,
        ForwardTimedFeatureKind.PrizeUpgrade => ForwardFeatureKind.PrizeUpgrade,
        _ => null,
    };
