using CoinPusherEngine;
using System.Diagnostics;

Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Length > 0 && args[0] == "selftest") { StressTest.Run(); return; }
if (args.Length > 0 && args[0] == "volume")
{
    int count = args.Length > 1 && int.TryParse(args[1], out var parsedCount) ? parsedCount : 1000;
    int seed = args.Length > 2 && int.TryParse(args[2], out var parsedSeed) ? parsedSeed : 20260623;
    RunVolumeTest(count, seed);
    return;
}
if (args.Length > 0 && args[0] == "nearmiss")
{
    int seedsPerScenario = args.Length > 1 && int.TryParse(args[1], out var parsedCount) ? parsedCount : 50;
    RunNearMissDiagnostic(seedsPerScenario);
    return;
}
if (args.Length > 0 && args[0] == "bench")
{
    int count = args.Length > 1 && int.TryParse(args[1], out var parsedCount) ? parsedCount : 10000;
    int seed = args.Length > 2 && int.TryParse(args[2], out var parsedSeed) ? parsedSeed : 20260703;
    RunGenerationBenchmark(count, seed);
    return;
}
if (args.Length > 0 && args[0] == "featureaudit")
{
    int count = args.Length > 1 && int.TryParse(args[1], out var parsedCount) ? parsedCount : 5000;
    int seed = args.Length > 2 && int.TryParse(args[2], out var parsedSeed) ? parsedSeed : 20260703;
    RunFeatureAudit(count, seed);
    return;
}
if (args.Length > 0 && args[0] == "spinprobe")
{
    int seed = args.Length > 1 && int.TryParse(args[1], out var parsedSeed) ? parsedSeed : 20260703;
    RunSpinProbe(seed);
    return;
}
if (args.Length > 0 && args[0] == "bundlecheck")
{
    int count = args.Length > 1 && int.TryParse(args[1], out var parsedCount) ? parsedCount : 1000;
    int seed = args.Length > 2 && int.TryParse(args[2], out var parsedSeed) ? parsedSeed : 20260703;
    RunBundleCheck(count, seed);
    return;
}
if (args.Length > 0 && args[0] == "pusheraudit")
{
    int count = args.Length > 1 && int.TryParse(args[1], out var parsedCount) ? parsedCount : 1000;
    int seed = args.Length > 2 && int.TryParse(args[2], out var parsedSeed) ? parsedSeed : 20260703;
    RunPusherAudit(count, seed);
    return;
}

var rows = new List<PrizeLadderRow>
{
    new() { Target = 20, Tiers = new decimal[] { 1, 2, 5 } },
    new() { Target = 20, Tiers = new decimal[] { 2, 4, 8 } },
    new() { Target = 20, Tiers = new decimal[] { 5, 10, 25 } },
    new() { Target = 25, Tiers = new decimal[] { 10, 20, 50 } },
    new() { Target = 25, Tiers = new decimal[] { 100, 200, 500 } },
    new() { Target = 30, Tiers = new decimal[] { 10000 } },
};
var bundle = new LadderCombinator(rows).Bundle(new decimal[] { 1, 20,100});
Console.WriteLine($"Covered: [${string.Join(",",bundle.Covered.Select(a=>$"${a}"))}]");
Console.WriteLine($"Entries: [{string.Join(";",bundle.Entries.Select(e=>$"sym{e.Sym}@tier{e.Tier}"))}]");
Console.WriteLine($"Skipped: [${string.Join(",",bundle.Skipped.Select(a=>$"${a}"))}]");
Console.WriteLine($"Targets: [{string.Join(",",bundle.Input.Targets.Select(kv=>$"sym{kv.Key}={kv.Value}"))}]");
Console.WriteLine($"BaseSpins: {bundle.Input.BaseSpins}");

var plan = new Planner(bundle.Input, seed: null).Plan();
var json = TicketSerializer.ToJson(plan);
Console.WriteLine(json);

static void RunVolumeTest(int count, int seed)
{
    var rng = new Random(seed);
    var failures = new List<string>();
    var warnings = new List<string>();
    var spinCounts = new Dictionary<int, int>();
    var featureCounts = new Dictionary<int, int>();
    var nonWinTickets = 0;
    var retriggerTickets = 0;
    var maxWheelStackValue = 0;
    var totalWarnings = 0;
    var stopwatch = Stopwatch.StartNew();
    long planMs = 0, internalMs = 0, serializeMs = 0, checkerMs = 0;
    var inputAttempts = 0;
    var maxInputAttempts = 0;
    var plannerAttempts = 0;
    var maxPlannerAttempts = 0;
    var localAttempts = 0;
    var maxLocalAttempts = 0;
    var plannerRetryCauses = new Dictionary<string, int>();
    var gate = new object();
    var completed = 0;
    var progressStep = Math.Max(1, count / 10);

    Console.WriteLine($"CoinPusherEngine volume test: {count} random tickets, seed={seed}");

    Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, index =>
    {
        var localRng = new Random(VolumeSeed(seed, index));
        var plannerSeed = seed + index;
        try
        {
            var step = Stopwatch.StartNew();
            var plan = GenerateRandomValidPlan(localRng, plannerSeed, out var attemptsUsed);
            step.Stop();
            Interlocked.Add(ref planMs, step.ElapsedMilliseconds);
            Interlocked.Add(ref inputAttempts, attemptsUsed);
            var planAttempts = PlannerAttempts(plan);
            Interlocked.Add(ref plannerAttempts, planAttempts);
            var suffixAttempts = LocalSuffixAttempts(plan);
            Interlocked.Add(ref localAttempts, suffixAttempts);
            lock (gate)
            {
                maxInputAttempts = Math.Max(maxInputAttempts, attemptsUsed);
                maxPlannerAttempts = Math.Max(maxPlannerAttempts, planAttempts);
                maxLocalAttempts = Math.Max(maxLocalAttempts, suffixAttempts);
                foreach (var (cause, amount) in PlannerRetryCauses(plan))
                    plannerRetryCauses[cause] = plannerRetryCauses.GetValueOrDefault(cause) + amount;
            }

            step.Restart();
            VerifyInternalReplay(plan);
            step.Stop();
            Interlocked.Add(ref internalMs, step.ElapsedMilliseconds);

            step.Restart();
            var ticket = TicketSerializer.ToTicketObject(plan);
            VerifyTicketShape(ticket);
            step.Stop();
            Interlocked.Add(ref serializeMs, step.ElapsedMilliseconds);

            step.Restart();
            var report = TicketChecker.CheckTicket(ticket);
            step.Stop();
            Interlocked.Add(ref checkerMs, step.ElapsedMilliseconds);
            var failure = report.Checks.FirstOrDefault(check => check.Result == TicketChecker.Status.Fail);
            if (failure != null)
            {
                throw new InvalidOperationException(
                    $"TicketChecker failed {failure.Category}/{failure.Name}: {failure.Detail}");
            }

            var hasRetrigger = false;
            var ticketFeatureCounts = new Dictionary<int, int>();
            var ticketMaxWheelStack = 0;
            foreach (var turn in ticket.Turns)
            foreach (var spawn in turn.Spawns)
            {
                if (spawn.Feature == null) continue;
                CountFeature(spawn.Feature, ticketFeatureCounts);
                hasRetrigger |= spawn.Feature.ReTrigger.Length > 0;
                if (spawn.Feature.WheelStackValue.HasValue)
                    ticketMaxWheelStack = Math.Max(ticketMaxWheelStack, spawn.Feature.WheelStackValue.Value);
            }

            lock (gate)
            {
                var ticketWarnings = report.Checks
                    .Where(check => check.Result == TicketChecker.Status.Warning)
                    .ToArray();
                totalWarnings += ticketWarnings.Length;
                foreach (var warning in ticketWarnings)
                {
                    if (warnings.Count < 20)
                    {
                        warnings.Add(
                            $"#{index} plannerSeed={plannerSeed}: " +
                            $"{warning.Category}/{warning.Name}: {warning.Detail}");
                    }
                }
                spinCounts[ticket.WinInfo.TotalSpins] = spinCounts.GetValueOrDefault(ticket.WinInfo.TotalSpins) + 1;
                if (ticket.WinInfo.NonWinSymbols.Length > 0) nonWinTickets++;
                if (hasRetrigger) retriggerTickets++;
                maxWheelStackValue = Math.Max(maxWheelStackValue, ticketMaxWheelStack);
                foreach (var (featureId, amount) in ticketFeatureCounts)
                {
                    featureCounts[featureId] = featureCounts.GetValueOrDefault(featureId) + amount;
                }
            }
        }
        catch (Exception ex)
        {
            var message = $"#{index} plannerSeed={plannerSeed}: {ex.InnerException?.Message ?? ex.Message}";
            lock (gate)
            {
                failures.Add(message);
                if (failures.Count <= 20)
                {
                    Console.WriteLine("FAIL " + message);
                }
            }
        }

        var done = Interlocked.Increment(ref completed);
        if (done % progressStep == 0)
        {
            lock (gate)
            {
                Console.WriteLine($"progress {done}/{count} failures={failures.Count}");
            }
        }
    });

    Console.WriteLine("==== VOLUME TEST SUMMARY ====");
    Console.WriteLine($"tickets={count}");
    Console.WriteLine($"failures={failures.Count}");
    Console.WriteLine($"warnings={totalWarnings}");
    Console.WriteLine($"nonWinTickets={nonWinTickets}");
    Console.WriteLine($"retriggerTickets={retriggerTickets}");
    Console.WriteLine($"maxWheelStackValue={maxWheelStackValue}");
    Console.WriteLine("spins=" + string.Join(",", spinCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}")));
    Console.WriteLine("features=" + string.Join(",", featureCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}")));
    Console.WriteLine($"elapsedMs={stopwatch.ElapsedMilliseconds}");
    Console.WriteLine($"planMs={planMs} internalReplayMs={internalMs} serializeMs={serializeMs} ticketCheckerMs={checkerMs}");
    Console.WriteLine($"inputAttempts={inputAttempts} avgInputAttempts={(count == 0 ? 0 : inputAttempts / (double)count):F2} maxInputAttempts={maxInputAttempts}");
    Console.WriteLine($"plannerAttempts={plannerAttempts} avgPlannerAttempts={(count == 0 ? 0 : plannerAttempts / (double)count):F2} maxPlannerAttempts={maxPlannerAttempts}");
    Console.WriteLine($"localSuffixAttempts={localAttempts} avgLocalSuffixAttempts={(count == 0 ? 0 : localAttempts / (double)count):F2} maxLocalSuffixAttempts={maxLocalAttempts}");
    Console.WriteLine("plannerRetryCauses=" + (plannerRetryCauses.Count == 0
        ? "none"
        : string.Join(",", plannerRetryCauses.OrderByDescending(kv => kv.Value)
                                             .Select(kv => $"{kv.Key}:{kv.Value}"))));

    if (failures.Count > 0)
    {
        Console.WriteLine("sample failures:");
        foreach (var failure in failures.Take(20))
            Console.WriteLine(failure);
        Environment.Exit(1);
    }
    if (warnings.Count > 0)
    {
        Console.WriteLine("sample warnings:");
        foreach (var warning in warnings.Take(20))
            Console.WriteLine(warning);
    }
}

static void RunBundleCheck(int count, int seed)
{
    var rows = new List<PrizeLadderRow>
    {
        new() { Target = 20, Tiers = new decimal[] { 1, 2, 5 } },
        new() { Target = 20, Tiers = new decimal[] { 2, 4, 8 } },
        new() { Target = 20, Tiers = new decimal[] { 5, 10, 25 } },
        new() { Target = 25, Tiers = new decimal[] { 10, 20, 50 } },
        new() { Target = 25, Tiers = new decimal[] { 100, 200, 500 } },
        new() { Target = 30, Tiers = new decimal[] { 10000 } },
    };
    var amounts = new decimal[] { 1, 2, 5, 10, 100, 10000 };
    var failures = new List<string>();
    var nonWinCounts = new Dictionary<int, int>();
    var spinCounts = new Dictionary<int, int>();
    var coveredCounts = new Dictionary<string, int>();
    var skippedCounts = new Dictionary<string, int>();

    for (var i = 0; i < count; i++)
    {
        var bundleSeed = VolumeSeed(seed, i);
        var plannerSeed = VolumeSeed(seed + 1, i);
        try
        {
            var bundle = new LadderCombinator(rows, bundleSeed).Bundle(amounts);
            var coveredKey = string.Join(",", bundle.Covered);
            var skippedKey = string.Join(",", bundle.Skipped);
            coveredCounts[coveredKey] = coveredCounts.GetValueOrDefault(coveredKey) + 1;
            skippedCounts[skippedKey] = skippedCounts.GetValueOrDefault(skippedKey) + 1;

            var plan = new Planner(bundle.Input, plannerSeed).Plan();
            var ticket = TicketSerializer.ToTicketObject(plan);
            var report = TicketChecker.CheckTicket(ticket);
            var failure = report.Checks.FirstOrDefault(check => check.Result == TicketChecker.Status.Fail);
            if (failure != null)
                throw new InvalidOperationException(
                    $"TicketChecker failed {failure.Category}/{failure.Name}: {failure.Detail}");

            nonWinCounts[plan.NonWinTargets.Count] = nonWinCounts.GetValueOrDefault(plan.NonWinTargets.Count) + 1;
            spinCounts[plan.TotalSpins] = spinCounts.GetValueOrDefault(plan.TotalSpins) + 1;
        }
        catch (Exception ex)
        {
            var bundle = new LadderCombinator(rows, bundleSeed).Bundle(amounts);
            failures.Add(
                $"#{i} bundleSeed={bundleSeed} plannerSeed={plannerSeed}: " +
                $"{DeepestMessage(ex)} " +
                $"covered=[{string.Join(",", bundle.Covered)}] skipped=[{string.Join(",", bundle.Skipped)}] " +
                $"targets=[{string.Join(",", bundle.Input.Targets.Select(kv => $"sym{kv.Key}={kv.Value}"))}] " +
                $"required=[{string.Join(",", bundle.Input.Required.Select(kv => $"{kv.Key}={kv.Value}"))}]");
            if (failures.Count >= 20) break;
        }
    }

    Console.WriteLine("==== BUNDLE CHECK ====");
    Console.WriteLine($"tickets={count}");
    Console.WriteLine($"failures={failures.Count}");
    Console.WriteLine("nonWinCounts=" + FormatCounts(nonWinCounts));
    Console.WriteLine("spins=" + FormatCounts(spinCounts));
    Console.WriteLine("covered=" + FormatStringCounts(coveredCounts));
    Console.WriteLine("skipped=" + FormatStringCounts(skippedCounts));
    foreach (var failure in failures)
        Console.WriteLine("failure: " + failure);
    if (failures.Count > 0) Environment.Exit(1);
}

static void RunPusherAudit(int count, int seed)
{
    var rows = new List<PrizeLadderRow>
    {
        new() { Target = 20, Tiers = new decimal[] { 1, 2, 5 } },
        new() { Target = 20, Tiers = new decimal[] { 2, 4, 8 } },
        new() { Target = 20, Tiers = new decimal[] { 5, 10, 25 } },
        new() { Target = 25, Tiers = new decimal[] { 10, 20, 50 } },
        new() { Target = 25, Tiers = new decimal[] { 100, 200, 500 } },
        new() { Target = 30, Tiers = new decimal[] { 10000 } },
    };
    var amounts = new decimal[] { 1, 2, 5, 10, 100, 10000 };
    var pushValueCounts = new Dictionary<int, int>();
    var patternCounts = new Dictionary<string, int>();
    var failures = new List<string>();
    var totalTurns = 0;
    var allThreeTurns = 0;
    var monotonicMixedTurns = 0;
    var flushPushers = 0;

    for (var i = 0; i < count; i++)
    {
        var bundleSeed = VolumeSeed(seed, i);
        var plannerSeed = VolumeSeed(seed + 1, i);
        try
        {
            var bundle = new LadderCombinator(rows, bundleSeed).Bundle(amounts);
            var plan = new Planner(bundle.Input, plannerSeed).Plan();
            var ticket = TicketSerializer.ToTicketObject(plan);
            var report = TicketChecker.CheckTicket(ticket);
            var failure = report.Checks.FirstOrDefault(check => check.Result == TicketChecker.Status.Fail);
            if (failure != null)
                throw new InvalidOperationException(
                    $"TicketChecker failed {failure.Category}/{failure.Name}: {failure.Detail}");

            foreach (var turn in ticket.Turns)
            {
                totalTurns++;
                var normal = turn.Pushers
                    .Where(pusher => pusher.FeatureId != K.F_FLUSH_ID)
                    .Select(pusher => pusher.PushValue)
                    .ToArray();
                foreach (var push in normal)
                    pushValueCounts[push] = pushValueCounts.GetValueOrDefault(push) + 1;
                flushPushers += turn.Pushers.Count(pusher => pusher.FeatureId == K.F_FLUSH_ID);

                var pattern = string.Join("-", turn.Pushers.Select(pusher =>
                    pusher.FeatureId == K.F_FLUSH_ID ? "F" : pusher.PushValue.ToString()));
                patternCounts[pattern] = patternCounts.GetValueOrDefault(pattern) + 1;
                if (normal.Length == K.COLS && normal.All(push => push == K.MAX_PUSH))
                    allThreeTurns++;
                if (normal.Length > 1
                    && normal.Distinct().Count() > 1
                    && (normal.SequenceEqual(normal.OrderBy(x => x))
                        || normal.SequenceEqual(normal.OrderByDescending(x => x))))
                {
                    monotonicMixedTurns++;
                }
            }
        }
        catch (Exception ex)
        {
            failures.Add($"#{i} bundleSeed={bundleSeed} plannerSeed={plannerSeed}: {DeepestMessage(ex)}");
        }
    }

    Console.WriteLine("==== PUSHER AUDIT ====");
    Console.WriteLine($"tickets={count}");
    Console.WriteLine($"failures={failures.Count}");
    Console.WriteLine($"turns={totalTurns}");
    Console.WriteLine($"allThreeTurns={allThreeTurns}");
    Console.WriteLine($"monotonicMixedTurns={monotonicMixedTurns}");
    Console.WriteLine($"flushPushers={flushPushers}");
    Console.WriteLine("pushValues=" + FormatCounts(pushValueCounts));
    Console.WriteLine("topPatterns=" + string.Join(",", patternCounts.OrderByDescending(kv => kv.Value)
        .ThenBy(kv => kv.Key)
        .Take(12)
        .Select(kv => $"{kv.Key}:{kv.Value}")));
    foreach (var failure in failures.Take(10))
        Console.WriteLine("failure: " + failure);
    if (failures.Count > 0) Environment.Exit(1);
}

static string DeepestMessage(Exception ex)
{
    var leaf = ex;
    while (leaf.InnerException != null) leaf = leaf.InnerException;
    return leaf.Message;
}

static void RunNearMissDiagnostic(int seedsPerScenario)
{
    var rows = new List<PrizeLadderRow>
    {
        new() { Target = 20, Tiers = new decimal[] { 1, 2, 5 } },
        new() { Target = 20, Tiers = new decimal[] { 2, 4, 8 } },
        new() { Target = 20, Tiers = new decimal[] { 5, 10, 25 } },
        new() { Target = 25, Tiers = new decimal[] { 10, 20, 50 } },
        new() { Target = 25, Tiers = new decimal[] { 100, 200, 500 } },
        new() { Target = 30, Tiers = new decimal[] { 10000 } },
    };
    var scenarios = new[]
    {
        new[] { 1m }, new[] { 5m }, new[] { 50m },
        new[] { 1m, 2m }, new[] { 1m, 5m }, new[] { 8m, 50m },
        new[] { 1m, 2m, 5m }, new[] { 45m }, new[] { 1m, 2m, 5m, 10m },
    };

    var tickets = 0;
    var nearTickets = 0;
    var nearSymbols = 0;
    var nearWithWheel = 0;
    var nearWithPrizeUpgrade = 0;
    var minActual = int.MaxValue;
    var maxActual = 0;
    var nearSymbolCounts = new Dictionary<int, int>();
    var failures = new List<string>();
    var samples = new List<string>();

    foreach (var amounts in scenarios)
    {
        for (var seed = 0; seed < seedsPerScenario; seed++)
        {
            GamePlan plan;
            try
            {
                var bundle = new LadderCombinator(rows, seed).Bundle(amounts);
                try
                {
                    plan = new Planner(bundle.Input, seed).Plan();
                }
                catch (Exception ex)
                {
                    var detail = ex.ToString().Replace(Environment.NewLine, " ");
                    if (detail.Length > 1200) detail = detail[..1200];
                    failures.Add(
                        $"amounts=[{string.Join(",", amounts)}] seed={seed}: " +
                        $"{detail}; " +
                        $"targets=[{string.Join(",", bundle.Input.Targets.Select(kv => $"sym{kv.Key}={kv.Value}"))}] " +
                        $"required=[{string.Join(",", bundle.Input.Required.Select(kv => $"{kv.Key}={kv.Value}"))}] " +
                        $"tiers=[{string.Join(",", (bundle.Input.PrizeTiers ?? new Dictionary<int, int>()).Select(kv => $"sym{kv.Key}=tier{kv.Value}"))}]");
                    continue;
                }
            }
            catch (Exception ex)
            {
                failures.Add($"amounts=[{string.Join(",", amounts)}] seed={seed}: {ex.InnerException?.Message ?? ex.Message}");
                continue;
            }

            var got = Sim.Run(plan);
            tickets++;

            if (plan.NonWinTargets.Count == 0) continue;
            nearTickets++;
            nearSymbolCounts[plan.NonWinTargets.Count] =
                nearSymbolCounts.GetValueOrDefault(plan.NonWinTargets.Count) + 1;

            foreach (var (sym, target) in plan.NonWinTargets)
            {
                nearSymbols++;
                got.TryGetValue(sym, out var actual);
                minActual = Math.Min(minActual, actual);
                maxActual = Math.Max(maxActual, actual);

                var hasWheel = plan.Spins.SelectMany(spin => spin.Spawns.Values)
                    .Any(cell => cell.IsFeat && cell.Sym == K.F_WHEEL && cell.Fp?.WheelSym == sym);
                var hasPrizeUpgrade = plan.Spins.SelectMany(spin => spin.Spawns.Values)
                    .Any(cell => cell.IsFeat && cell.Sym == K.F_PRUP && cell.Fp?.PrupSym == sym);

                if (hasWheel) nearWithWheel++;
                if (hasPrizeUpgrade) nearWithPrizeUpgrade++;

                if (samples.Count < 8)
                {
                    samples.Add(
                        $"amounts=[{string.Join(",", amounts)}] seed={seed} sym={sym} " +
                        $"target>={target}<{K.SymbolFillCap(sym)} actual={actual} wheel={hasWheel} prup={hasPrizeUpgrade}");
                }
            }
        }
    }

    Console.WriteLine("==== NEAR MISS DIAGNOSTIC ====");
    Console.WriteLine($"tickets={tickets}");
    Console.WriteLine($"nearTickets={nearTickets}");
    Console.WriteLine($"nearSymbols={nearSymbols}");
    Console.WriteLine("nearSymbolCounts=" + string.Join(",",
        nearSymbolCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}")));
    Console.WriteLine($"actualCollectionRange={(nearSymbols == 0 ? "none" : $"{minActual}..{maxActual}")}");
    Console.WriteLine($"nearSymbolsWithWheel={nearWithWheel}");
    Console.WriteLine($"nearSymbolsWithPrizeUpgrade={nearWithPrizeUpgrade}");
    Console.WriteLine($"failures={failures.Count}");
    foreach (var failure in failures.Take(10))
        Console.WriteLine("failure: " + failure);
    Console.WriteLine("samples:");
    foreach (var sample in samples)
        Console.WriteLine(sample);
}

static void RunGenerationBenchmark(int count, int seed)
{
    var rows = new List<PrizeLadderRow>
    {
        new() { Target = 20, Tiers = new decimal[] { 1, 2, 5 } },
        new() { Target = 20, Tiers = new decimal[] { 2, 4, 8 } },
        new() { Target = 20, Tiers = new decimal[] { 5, 10, 25 } },
        new() { Target = 25, Tiers = new decimal[] { 10, 20, 50 } },
        new() { Target = 25, Tiers = new decimal[] { 100, 200, 500 } },
        new() { Target = 30, Tiers = new decimal[] { 10000 } },
    };
    var scenarios = new[]
    {
        new[] { 1m }, new[] { 5m }, new[] { 50m },
        new[] { 1m, 2m }, new[] { 1m, 5m }, new[] { 8m, 50m },
        new[] { 1m, 2m, 5m }, new[] { 45m }, new[] { 1m, 2m, 5m, 10m },
    };

    var stopwatch = Stopwatch.StartNew();
    long bundleTicks = 0, planTicks = 0, serializeTicks = 0;
    for (var i = 0; i < count; i++)
    {
        var amounts = scenarios[i % scenarios.Length];
        var ticketSeed = VolumeSeed(seed, i);

        var step = Stopwatch.StartNew();
        var bundle = new LadderCombinator(rows, ticketSeed).Bundle(amounts);
        step.Stop();
        bundleTicks += step.ElapsedTicks;

        step.Restart();
        var plan = new Planner(bundle.Input, ticketSeed).Plan();
        step.Stop();
        planTicks += step.ElapsedTicks;

        step.Restart();
        _ = TicketSerializer.ToTicketObject(plan);
        step.Stop();
        serializeTicks += step.ElapsedTicks;
    }
    stopwatch.Stop();

    var tickMs = 1000.0 / Stopwatch.Frequency;
    Console.WriteLine("==== GENERATION BENCHMARK ====");
    Console.WriteLine($"tickets={count}");
    Console.WriteLine($"elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F1}");
    Console.WriteLine($"avgMsPerTicket={stopwatch.Elapsed.TotalMilliseconds / count:F4}");
    Console.WriteLine($"bundleMs={bundleTicks * tickMs:F1}");
    Console.WriteLine($"planMs={planTicks * tickMs:F1}");
    Console.WriteLine($"serializeMs={serializeTicks * tickMs:F1}");
}

static void RunFeatureAudit(int count, int seed)
{
    var rows = new List<PrizeLadderRow>
    {
        new() { Target = 20, Tiers = new decimal[] { 1, 2, 5 } },
        new() { Target = 20, Tiers = new decimal[] { 2, 4, 8 } },
        new() { Target = 20, Tiers = new decimal[] { 5, 10, 25 } },
        new() { Target = 25, Tiers = new decimal[] { 10, 20, 50 } },
        new() { Target = 25, Tiers = new decimal[] { 100, 200, 500 } },
        new() { Target = 30, Tiers = new decimal[] { 10000 } },
    };
    var scenarios = new[]
    {
        new[] { 1m }, new[] { 5m }, new[] { 50m },
        new[] { 1m, 2m }, new[] { 1m, 5m }, new[] { 8m, 50m },
        new[] { 1m, 2m, 5m }, new[] { 45m }, new[] { 1m, 2m, 5m, 10m },
    };

    var failures = new List<string>();
    var wheelValues = new Dictionary<int, int>();
    var rootFeatureCounts = new Dictionary<int, int>();
    var allFeatureCounts = new Dictionary<int, int>();
    var chainRootCounts = new Dictionary<int, int>();
    var chainPairCounts = new Dictionary<string, int>();
    var firstSpinFeatureTokens = 0;
    var finalSpinWheelTokens = 0;
    var prizeUpgradeTokens = 0;
    var latePrizeUpgradeTokens = 0;
    var flushPushers = 0;
    var physicalExtraTokens = 0;
    var serializedExtraTokens = 0;
    var physicalPrizeUpgradeTokens = 0;
    var serializedPrizeUpgradeTokens = 0;
    var ticketsWithRetrigger = 0;

    for (var i = 0; i < count; i++)
    {
        var amounts = scenarios[i % scenarios.Length];
        var ticketSeed = VolumeSeed(seed, i);
        try
        {
            var bundle = new LadderCombinator(rows, ticketSeed).Bundle(amounts);
            var plan = new Planner(bundle.Input, ticketSeed).Plan();
            var ticket = TicketSerializer.ToTicketObject(plan);
            VerifyTicketShape(ticket);

            var report = TicketChecker.CheckTicket(ticket);
            var failure = report.Checks.FirstOrDefault(check => check.Result == TicketChecker.Status.Fail);
            if (failure != null)
                throw new InvalidOperationException(
                    $"TicketChecker failed {failure.Category}/{failure.Name}: {failure.Detail}");

            physicalExtraTokens += plan.Spins.SelectMany(spin => spin.Spawns.Values)
                .Count(cell => cell.IsFeat && cell.Sym == K.F_XSPIN);
            physicalPrizeUpgradeTokens += plan.Spins.SelectMany(spin => spin.Spawns.Values)
                .Count(cell => cell.IsFeat && cell.Sym == K.F_PRUP);

            var hasRetrigger = false;
            foreach (var (turn, turnIndex) in ticket.Turns.Select((turn, index) => (turn, index)))
            {
                flushPushers += turn.Pushers.Count(pusher => pusher.FeatureId == K.F_FLUSH_ID);

                foreach (var spawn in turn.Spawns)
                {
                    if (spawn.Feature == null) continue;
                    if (turnIndex == 0) firstSpinFeatureTokens++;
                    if (turnIndex == ticket.Turns.Length - 1 && spawn.Feature.FeatureId == K.F_WHEEL)
                        finalSpinWheelTokens++;

                    CountFeatureAudit(spawn.Feature, rootFeatureCounts);
                    CountFeatureAuditRecursive(spawn.Feature, allFeatureCounts);
                    serializedExtraTokens += CountFeatureIdAudit(spawn.Feature, K.F_XSPIN);
                    serializedPrizeUpgradeTokens += CountFeatureIdAudit(spawn.Feature, K.F_PRUP);

                    if (spawn.Feature.FeatureId == K.F_PRUP)
                    {
                        prizeUpgradeTokens++;
                        if (turnIndex >= Math.Max(0, ticket.Turns.Length - 3))
                            latePrizeUpgradeTokens++;
                    }

                    if (spawn.Feature.FeatureId == K.F_WHEEL && spawn.Feature.WheelStackValue.HasValue)
                    {
                        var value = spawn.Feature.WheelStackValue.Value;
                        wheelValues[value] = wheelValues.GetValueOrDefault(value) + 1;
                    }

                    if (spawn.Feature.ReTrigger.Length > 0)
                    {
                        hasRetrigger = true;
                        chainRootCounts[spawn.Feature.FeatureId] = chainRootCounts.GetValueOrDefault(spawn.Feature.FeatureId) + 1;
                        foreach (var nestedId in FlattenFeatureIds(spawn.Feature.ReTrigger))
                        {
                            var pair = $"{spawn.Feature.FeatureId}->{nestedId}";
                            chainPairCounts[pair] = chainPairCounts.GetValueOrDefault(pair) + 1;
                        }
                    }
                }
            }

            if (hasRetrigger) ticketsWithRetrigger++;
        }
        catch (Exception ex)
        {
            failures.Add($"#{i} seed={ticketSeed}: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    Console.WriteLine("==== FEATURE AUDIT ====");
    Console.WriteLine($"tickets={count}");
    Console.WriteLine($"failures={failures.Count}");
    Console.WriteLine($"wheelStackValues={FormatCounts(wheelValues)}");
    Console.WriteLine($"rootFeatureCounts={FormatCounts(rootFeatureCounts)}");
    Console.WriteLine($"allFeatureCounts={FormatCounts(allFeatureCounts)}");
    Console.WriteLine($"physicalExtraSpinTokens={physicalExtraTokens}");
    Console.WriteLine($"serializedExtraSpinTokens={serializedExtraTokens}");
    Console.WriteLine($"physicalPrizeUpgradeTokens={physicalPrizeUpgradeTokens}");
    Console.WriteLine($"serializedPrizeUpgradeTokens={serializedPrizeUpgradeTokens}");
    Console.WriteLine($"firstSpinFeatureTokens={firstSpinFeatureTokens}");
    Console.WriteLine($"finalSpinWheelTokens={finalSpinWheelTokens}");
    Console.WriteLine($"latePrizeUpgradeTokens={latePrizeUpgradeTokens}/{prizeUpgradeTokens}");
    Console.WriteLine($"flushPushers={flushPushers}");
    Console.WriteLine($"ticketsWithRetrigger={ticketsWithRetrigger}");
    Console.WriteLine($"chainRootCounts={FormatCounts(chainRootCounts)}");
    Console.WriteLine($"chainPairs={FormatStringCounts(chainPairCounts)}");
    foreach (var failure in failures.Take(10))
        Console.WriteLine("failure: " + failure);
}

static void RunSpinProbe(int seed)
{
    var cases = new[]
    {
        ("base", 0),
        ("one-extra", 1),
        ("two-extra", 2),
        ("three-extra", 3),
    };

    Console.WriteLine("==== SPIN PROBE ====");
    foreach (var (label, extras) in cases)
    {
        var required = extras == 0
            ? new Dictionary<string, int>()
            : new Dictionary<string, int> { ["EXTRA_SPIN"] = extras };
        var input = new MathInput
        {
            Targets = new Dictionary<int, int> { [1] = 12, [2] = 10 },
            BaseSpins = K.BASE_SPINS,
            Required = required,
            PrizeValues = BuildPrizeValues(8),
            MaxSym = 8,
        };

        GamePlan? plan = null;
        Exception? last = null;
        for (var attempt = 0; attempt < 50 && plan == null; attempt++)
        {
            try
            {
                plan = new Planner(input, seed + extras * 1000 + attempt).Plan();
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        if (plan == null)
        {
            Console.WriteLine($"{label}: FAILED {last?.InnerException?.Message ?? last?.Message}");
            continue;
        }

        var ticket = TicketSerializer.ToTicketObject(plan);
        VerifyTicketShape(ticket);
        var report = TicketChecker.CheckTicket(ticket);
        var failure = report.Checks.FirstOrDefault(check => check.Result == TicketChecker.Status.Fail);
        if (failure != null)
        {
            Console.WriteLine($"{label}: FAILED TicketChecker {failure.Category}/{failure.Name}: {failure.Detail}");
            continue;
        }

        var extraTokens = plan.Spins.SelectMany(spin => spin.Spawns.Values)
            .Count(cell => cell.IsFeat && cell.Sym == K.F_XSPIN);
        Console.WriteLine($"{label}: TotalSpins={plan.TotalSpins} EXTRA_SPIN={extraTokens} turns={ticket.Turns.Length}");
    }
}

static void CountFeatureAudit(TicketSerializer.FeatureDto feature, Dictionary<int, int> counts)
{
    counts[feature.FeatureId] = counts.GetValueOrDefault(feature.FeatureId) + 1;
}

static void CountFeatureAuditRecursive(TicketSerializer.FeatureDto feature, Dictionary<int, int> counts)
{
    counts[feature.FeatureId] = counts.GetValueOrDefault(feature.FeatureId) + 1;
    foreach (var nested in feature.ReTrigger)
        CountFeatureAuditRecursive(nested, counts);
}

static int CountFeatureIdAudit(TicketSerializer.FeatureDto feature, int featureId)
{
    var count = feature.FeatureId == featureId ? 1 : 0;
    foreach (var nested in feature.ReTrigger)
        count += CountFeatureIdAudit(nested, featureId);
    return count;
}

static IEnumerable<int> FlattenFeatureIds(IEnumerable<TicketSerializer.FeatureDto> features)
{
    foreach (var feature in features)
    {
        yield return feature.FeatureId;
        foreach (var nested in FlattenFeatureIds(feature.ReTrigger))
            yield return nested;
    }
}

static string FormatCounts(Dictionary<int, int> counts) =>
    counts.Count == 0
        ? "none"
        : string.Join(",", counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));

static string FormatStringCounts(Dictionary<string, int> counts) =>
    counts.Count == 0
        ? "none"
        : string.Join(",", counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));

static int VolumeSeed(int seed, int index)
{
    unchecked
    {
        uint x = (uint)seed;
        x ^= (uint)(index + 1) * 0x9E3779B9u;
        x ^= x >> 16;
        x *= 0x85EBCA6Bu;
        x ^= x >> 13;
        x *= 0xC2B2AE35u;
        x ^= x >> 16;
        return (int)x;
    }
}

static int PlannerAttempts(GamePlan plan)
{
    var marker = plan.Log.FirstOrDefault(line => line.StartsWith("planned after ", StringComparison.Ordinal));
    if (marker == null) return 1;
    var parts = marker.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    return parts.Length >= 3 && int.TryParse(parts[2], out var attempts) ? attempts : 1;
}

static int LocalSuffixAttempts(GamePlan plan)
{
    var marker = plan.Log.FirstOrDefault(line => line.StartsWith("local realization succeeded after ", StringComparison.Ordinal));
    if (marker == null) return 1;
    var parts = marker.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    return parts.Length >= 5 && int.TryParse(parts[4], out var attempts) ? attempts : 1;
}

static IReadOnlyDictionary<string, int> PlannerRetryCauses(GamePlan plan)
{
    var marker = plan.Log.FirstOrDefault(line => line.StartsWith("planner retry causes: ", StringComparison.Ordinal));
    if (marker == null) return new Dictionary<string, int>();

    return marker["planner retry causes: ".Length..]
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Trim().Split('=', StringSplitOptions.RemoveEmptyEntries))
        .Where(parts => parts.Length == 2 && int.TryParse(parts[1], out _))
        .ToDictionary(parts => parts[0], parts => int.Parse(parts[1]));
}

static GamePlan GenerateRandomValidPlan(Random rng, int plannerSeed, out int attemptsUsed)
{
    Exception? lastPlanningFailure = null;
    for (var attempt = 0; attempt < 50; attempt++)
    {
        attemptsUsed = attempt + 1;
        var input = RandomInput(rng);
        try
        {
            return new Planner(input, plannerSeed + attempt * 10_000).Plan();
        }
        catch (Exception ex)
        {
            // A random input can be valid in shape but too dense for this engine's
            // feature/spin limits. Volume mode wants random valid tickets, so keep
            // sampling until a plan is actually produced; validation after this
            // point is never retried/hidden.
            lastPlanningFailure = ex;
        }
    }

    attemptsUsed = 50;
    throw new InvalidOperationException(
        "Unable to generate a random valid ticket after 50 input attempts.",
        lastPlanningFailure);
}

static MathInput RandomInput(Random rng)
{
    var maxSym = rng.Next(8, 11);
    var maxWinSymbols = Math.Min(5, maxSym - 2);
    var winCount = rng.Next(1, Math.Min(4, maxWinSymbols) + 1);
    var symbols = Enumerable.Range(1, maxSym).OrderBy(_ => rng.Next()).ToArray();
    var winSymbols = symbols.Take(winCount).OrderBy(sym => sym).ToArray();

    var targets = new Dictionary<int, int>();
    foreach (var sym in winSymbols)
    {
        var maxTarget = winCount switch
        {
            1 => 24,
            2 => 20,
            3 => 16,
            _ => 14,
        };
        targets[sym] = rng.Next(6, maxTarget + 1);
    }

    var required = new Dictionary<string, int>();
    // Keep random volume inputs feasible; the planner still adds WHEEL/FLUSH/
    // EXTRA_SPIN naturally through optional and capacity-driven paths. Forced
    // feature combinations are covered by StressTest's named scenarios.
    if (rng.NextDouble() < 0.05) required["WHEEL"] = 1;
    if (rng.NextDouble() < 0.03) required["FLUSH"] = 1;
    if (rng.NextDouble() < 0.03) required["EXTRA_SPIN"] = 1;

    Dictionary<int, int>? prizeTiers = null;
    if (rng.NextDouble() < 0.08)
    {
        prizeTiers = new Dictionary<int, int>();
        var sym = winSymbols[rng.Next(winSymbols.Length)];
        prizeTiers[sym] = 1;
        required["PRIZE_UPGRADE"] = prizeTiers.Values.Sum();
    }

    return new MathInput
    {
        Targets = targets,
        BaseSpins = K.BASE_SPINS,
        Required = required,
        PrizeTiers = prizeTiers,
        PrizeValues = BuildPrizeValues(maxSym),
        MaxSym = maxSym,
    };
}

static void VerifyInternalReplay(GamePlan plan)
{
    var replay = Sim.Run(plan);
    foreach (var (sym, target) in plan.Targets)
    {
        replay.TryGetValue(sym, out var got);
        if (got != target)
            throw new InvalidOperationException($"Internal replay win mismatch sym={sym} got={got} target={target}");
    }

    foreach (var (sym, target) in plan.NonWinTargets)
    {
        replay.TryGetValue(sym, out var got);
        var cap = K.SymbolFillCap(sym);
        if (got < target || got >= cap)
            throw new InvalidOperationException($"Internal replay non-win mismatch sym={sym} got={got} target>={target} cap<{cap}");
    }
}

static void VerifyTicketShape(TicketSerializer.TicketDto ticket)
{
    if (ticket.WinInfo.TotalSpins != ticket.Turns.Length)
        throw new InvalidOperationException($"TotalSpins={ticket.WinInfo.TotalSpins} but turns={ticket.Turns.Length}");
    if (ticket.StartingBoard.Length != K.ROWS || ticket.StartingBoard.Any(row => row.Length != K.COLS))
        throw new InvalidOperationException("StartingBoard must be 5x5.");

    foreach (var (turn, turnIndex) in ticket.Turns.Select((turn, index) => (turn, index)))
    {
        if (turn.Pushers.Length != K.COLS)
            throw new InvalidOperationException($"Turn {turnIndex + 1} has {turn.Pushers.Length} pushers.");

        var seen = new HashSet<int>();
        foreach (var spawn in turn.Spawns)
        {
            if (spawn.Pos < 0 || spawn.Pos >= K.ROWS * K.COLS)
                throw new InvalidOperationException($"Turn {turnIndex + 1} spawn Pos={spawn.Pos} is out of range.");
            if (!seen.Add(spawn.Pos))
                throw new InvalidOperationException($"Turn {turnIndex + 1} has duplicate spawn Pos={spawn.Pos}.");
            VerifyFeaturePayload(spawn.Feature);
        }
    }
}

static void VerifyFeaturePayload(TicketSerializer.FeatureDto? feature)
{
    if (feature == null) return;
    if (feature.FeatureId == K.F_WHEEL)
    {
        if (!feature.WheelSymbolId.HasValue)
            throw new InvalidOperationException("WHEEL feature missing WheelSymbolId.");
        if (!feature.WheelStackValue.HasValue
            || feature.WheelStackValue < K.MIN_WHEEL_STACK_VALUE
            || feature.WheelStackValue > K.MAX_WHEEL_STACK_VALUE)
            throw new InvalidOperationException($"WHEEL feature has invalid WheelStackValue={feature.WheelStackValue}.");
    }

    if (feature.FeatureId == K.F_PRUP && !feature.UpgradeSymbolId.HasValue)
        throw new InvalidOperationException("PRIZE_UPGRADE feature missing UpgradeSymbolId.");

    foreach (var nested in feature.ReTrigger)
        VerifyFeaturePayload(nested);
}

static void CountFeature(TicketSerializer.FeatureDto feature, Dictionary<int, int> counts)
{
    counts[feature.FeatureId] = counts.GetValueOrDefault(feature.FeatureId) + 1;
    foreach (var nested in feature.ReTrigger)
        CountFeature(nested, counts);
}

static IReadOnlyDictionary<int, IReadOnlyDictionary<int, decimal>> BuildPrizeValues(int maxSym)
{
    var values = new Dictionary<int, IReadOnlyDictionary<int, decimal>>();
    for (var sym = 1; sym <= maxSym; sym++)
    {
        values[sym] = new Dictionary<int, decimal>
        {
            [0] = sym,
            [1] = sym * 2,
            [2] = sym * 4,
        };
    }
    return values;
}
