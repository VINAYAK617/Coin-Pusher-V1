namespace CoinPusherEngine;

internal enum ForwardGamePlanAdapterStatus
{
    Valid,
    MissingBuildResult,
    SourceBuildInvalid,
    MissingBuildStages,
    MissingPipelinePlan,
    InvalidRecordedTurn,
    ReplayMismatch,
}

internal sealed class ForwardGamePlanAdapterResult
{
    internal ForwardGamePlanAdapterResult(
        ForwardGamePlanAdapterStatus status,
        string detail,
        GamePlan? plan)
    {
        Status = status;
        Detail = detail;
        Plan = plan;
    }

    internal ForwardGamePlanAdapterStatus Status { get; }
    internal string Detail { get; }
    internal GamePlan? Plan { get; }
    internal bool IsValid => Status == ForwardGamePlanAdapterStatus.Valid;
}

internal sealed class ForwardGamePlanAdapter
{
    internal ForwardGamePlanAdapterResult Adapt(ForwardTicketBuildResult? build)
    {
        if (build == null)
            return Fail(ForwardGamePlanAdapterStatus.MissingBuildResult, "build result is missing");
        if (!build.IsValid)
        {
            return Fail(
                ForwardGamePlanAdapterStatus.SourceBuildInvalid,
                $"{build.Status}: {build.Detail}");
        }

        var objectives = build.Objectives?.Objectives;
        var featurePlan = build.FeatureIntents?.Plan;
        var pipelinePlan = build.Pipeline?.Plan;
        if (objectives == null || featurePlan == null)
            return Fail(ForwardGamePlanAdapterStatus.MissingBuildStages, "objectives or feature intent plan is missing");
        if (pipelinePlan == null)
            return Fail(ForwardGamePlanAdapterStatus.MissingPipelinePlan, "pipeline plan is missing");

        var spins = new List<SpinPlan>(pipelinePlan.Turns.Count);
        foreach (var recordedTurn in pipelinePlan.Turns.OrderBy(turn => turn.Turn))
        {
            var spin = ToSpinPlan(recordedTurn, pipelinePlan.StartingBoard);
            if (spin.Result != null)
                return spin.Result;

            spins.Add(spin.Spin!);
        }

        var plan = new GamePlan
        {
            TotalSpins = pipelinePlan.Turns.Count,
            Targets = objectives.WinTargets.ToDictionary(kv => kv.Key, kv => kv.Value),
            WinSyms = objectives.WinSymbols.ToArray(),
            FillSyms = objectives.FillSymbols.ToArray(),
            PrizeTiers = featurePlan.EffectivePrizeTiers.ToDictionary(kv => kv.Key, kv => kv.Value),
            PrizeValues = objectives.PrizeValues.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyDictionary<int, decimal>)kv.Value.ToDictionary(tier => tier.Key, tier => tier.Value)),
            NonWinTargets = objectives.NearMissTargets.ToDictionary(kv => kv.Key, kv => kv.Value),
            NonWinPrizeTiers = featurePlan.EffectiveNonWinPrizeTiers.ToDictionary(kv => kv.Key, kv => kv.Value),
            Spins = spins,
            Verified = false,
        };

        var replay = Sim.Run(plan);
        var mismatch = CompareCollections(
            replay,
            pipelinePlan.ActualCollected,
            objectives.MaxSymbol);
        if (mismatch != null)
            return Fail(ForwardGamePlanAdapterStatus.ReplayMismatch, mismatch);

        plan.Verified = true;
        return new ForwardGamePlanAdapterResult(
            ForwardGamePlanAdapterStatus.Valid,
            "ok",
            plan);
    }

    private SpinBuildResult ToSpinPlan(
        ForwardRecordedTurn turn,
        Cell?[,] startingBoard)
    {
        if (turn.Pushers.Count != Settings.COLS)
        {
            return SpinBuildResult.Fail(Fail(
                ForwardGamePlanAdapterStatus.InvalidRecordedTurn,
                $"turn {turn.Turn}: expected {Settings.COLS} pushers, got {turn.Pushers.Count}"));
        }

        var push = new int[Settings.COLS];
        var flush = new bool[Settings.COLS];
        for (var col = 0; col < Settings.COLS; col++)
        {
            var pusher = turn.Pushers[col];
            flush[col] = pusher.FeatureId == Settings.F_FLUSH_ID;
            if (pusher.FeatureId.HasValue && pusher.FeatureId.Value != Settings.F_FLUSH_ID)
            {
                return SpinBuildResult.Fail(Fail(
                    ForwardGamePlanAdapterStatus.InvalidRecordedTurn,
                    $"turn {turn.Turn} col {col}: unexpected pusher FeatureId={pusher.FeatureId}"));
            }

            push[col] = pusher.PushValue;
        }

        var spawns = new Dictionary<(int, int), Cell>();
        var tokens = new List<(string Id, int Col, FP Fp)>();
        var tokenSlots = new List<(int r, int c)>();
        foreach (var spawn in turn.Spawns)
        {
            if (spawn.Row < 0 || spawn.Row >= Settings.ROWS || spawn.Col < 0 || spawn.Col >= Settings.COLS)
            {
                return SpinBuildResult.Fail(Fail(
                    ForwardGamePlanAdapterStatus.InvalidRecordedTurn,
                    $"turn {turn.Turn}: spawn ({spawn.Row},{spawn.Col}) outside board"));
            }

            if (!spawns.TryAdd((spawn.Row, spawn.Col), spawn.Cell.Clone()))
            {
                return SpinBuildResult.Fail(Fail(
                    ForwardGamePlanAdapterStatus.InvalidRecordedTurn,
                    $"turn {turn.Turn}: spawn ({spawn.Row},{spawn.Col}) appears more than once"));
            }

            if (spawn.Cell.IsFeat)
            {
                tokens.Add((spawn.Cell.FeatId ?? FeatureName(spawn.Cell.Sym), spawn.Col, spawn.Cell.Fp?.Clone() ?? new FP()));
                tokenSlots.Add((spawn.Row, spawn.Col));
            }
        }

        return SpinBuildResult.Ok(new SpinPlan
        {
            Spin = turn.Turn,
            IsExtra = turn.Turn > Settings.BASE_SPINS,
            Board = turn.Turn == 1 ? CloneBoard(startingBoard) : new Cell?[Settings.ROWS, Settings.COLS],
            Push = push,
            Flush = flush,
            Spawns = spawns,
            Tokens = tokens,
            TokenSlots = tokenSlots,
            Alloc = turn.Collected.ToDictionary(kv => kv.Key, kv => kv.Value),
        });
    }

    private string FeatureName(int symbol)
    {
        if (symbol == Settings.F_WHEEL) return "WHEEL";
        if (symbol == Settings.F_XSPIN) return "EXTRA_SPIN";
        if (symbol == Settings.F_PRUP) return "PRIZE_UPGRADE";
        return "";
    }

    private Cell?[,] CloneBoard(Cell?[,] board)
    {
        var clone = new Cell?[Settings.ROWS, Settings.COLS];
        for (var row = 0; row < Settings.ROWS; row++)
        {
            for (var col = 0; col < Settings.COLS; col++)
                clone[row, col] = board[row, col]?.Clone();
        }

        return clone;
    }

    private string? CompareCollections(
        IReadOnlyDictionary<int, int> replay,
        IReadOnlyDictionary<int, int> expected,
        int maxSymbol)
    {
        var symbols = replay.Keys
            .Concat(expected.Keys)
            .Where(symbol => symbol >= 1 && symbol <= maxSymbol && !Settings.IsFeat(symbol))
            .Distinct()
            .OrderBy(symbol => symbol);

        foreach (var symbol in symbols)
        {
            var replayCount = replay.GetValueOrDefault(symbol);
            var expectedCount = expected.GetValueOrDefault(symbol);
            if (replayCount != expectedCount)
            {
                return $"symbol {symbol} replay collected={replayCount}, pipeline collected={expectedCount}";
            }
        }

        return null;
    }

    private static ForwardGamePlanAdapterResult Fail(
        ForwardGamePlanAdapterStatus status,
        string detail) =>
        new(status, detail, null);

    private sealed class SpinBuildResult
    {
        private SpinBuildResult(SpinPlan? spin, ForwardGamePlanAdapterResult? result)
        {
            Spin = spin;
            Result = result;
        }

        internal SpinPlan? Spin { get; }
        internal ForwardGamePlanAdapterResult? Result { get; }

        internal static SpinBuildResult Ok(SpinPlan spin) =>
            new(spin, null);

        internal static SpinBuildResult Fail(ForwardGamePlanAdapterResult result) =>
            new(null, result);
    }
}
