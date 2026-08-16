namespace CoinPusherEngine;

internal enum ForwardTurnRealizationStatus
{
    Valid,
    MissingFrame,
    MissingBoardState,
    MissingObjectives,
    MissingFutureTurns,
    MissingFeatureCapacity,
    MissingLedgers,
    InvalidTurn,
    BoardPreviewInvalid,
    ExtraSpinTimelineInvalid,
    FeaturePlacementInvalid,
    NormalIntentPlanningFailed,
    NormalIntentCannotBeSatisfied,
    NormalSpawnInvalid,
    BoardAdvanceInvalid,
}

internal sealed class ForwardTurnRealizationResult
{
    internal ForwardTurnRealizationResult(
        ForwardTurnRealizationStatus status,
        string detail,
        IReadOnlyList<ForwardSpawn> spawns,
        IReadOnlyDictionary<int, int> collected,
        IReadOnlyList<ForwardWheelImpact> wheelImpacts,
        ForwardNormalIntentResult? normalIntentResult,
        int flushCount)
    {
        Status = status;
        Detail = detail;
        Spawns = spawns;
        Collected = collected;
        WheelImpacts = wheelImpacts;
        NormalIntentResult = normalIntentResult;
        FlushCount = flushCount;
    }

    internal ForwardTurnRealizationStatus Status { get; }
    internal string Detail { get; }
    internal IReadOnlyList<ForwardSpawn> Spawns { get; }
    internal IReadOnlyDictionary<int, int> Collected { get; }
    internal IReadOnlyList<ForwardWheelImpact> WheelImpacts { get; }
    internal ForwardNormalIntentResult? NormalIntentResult { get; }
    internal int FlushCount { get; }
    internal bool IsValid => Status == ForwardTurnRealizationStatus.Valid;
}

internal sealed class ForwardTurnRealizer
{
    private readonly ICustomProfileSettings _settings;
    private readonly Random _rng;
    private readonly ForwardCellFateAnalyzer _fateAnalyzer;

    internal ForwardTurnRealizer(ICustomProfileSettings settings, int seed)
    {
        _settings = settings;
        _rng = new Random(seed);
        _fateAnalyzer = new ForwardCellFateAnalyzer(settings);
    }

    internal ForwardTurnRealizationResult RealizeAndAdvance(
        ForwardTurnFrame? frame,
        int plannedTotalTurns,
        ForwardBoardState? boardState,
        ForwardObjectives? objectives,
        IReadOnlyList<ForwardFutureTurn>? futureTurns,
        IReadOnlyDictionary<ForwardFeatureKind, int>? remainingFeatureCapacity,
        SymbolLedger? symbolLedger,
        ForwardExtraSpinLedger? extraSpinLedger,
        ForwardPrizeUpgradeLedger? prizeUpgradeLedger)
    {
        if (frame == null)
            return Fail(ForwardTurnRealizationStatus.MissingFrame, "turn frame is missing");
        if (frame.Turn <= 0 || plannedTotalTurns <= 0 || frame.Turn > plannedTotalTurns)
            return Fail(ForwardTurnRealizationStatus.InvalidTurn, $"turn={frame.Turn}, plannedTotalTurns={plannedTotalTurns}");
        if (boardState == null)
            return Fail(ForwardTurnRealizationStatus.MissingBoardState, "board state is missing");
        if (objectives == null)
            return Fail(ForwardTurnRealizationStatus.MissingObjectives, "forward objectives are missing");
        if (futureTurns == null)
            return Fail(ForwardTurnRealizationStatus.MissingFutureTurns, "future turns are missing");
        if (remainingFeatureCapacity == null)
            return Fail(ForwardTurnRealizationStatus.MissingFeatureCapacity, "remaining feature capacity is missing");
        if (symbolLedger == null || extraSpinLedger == null || prizeUpgradeLedger == null)
            return Fail(ForwardTurnRealizationStatus.MissingLedgers, "one or more ledgers are missing");

        var preview = boardState.PreviewAfterPushRotate(frame.Shape);
        if (!preview.IsValid)
            return Fail(ForwardTurnRealizationStatus.BoardPreviewInvalid, preview.Detail, collected: preview.Collected);

        var trialSymbolLedger = symbolLedger.Clone();
        var trialExtraLedger = extraSpinLedger.Clone();
        var trialPrizeLedger = prizeUpgradeLedger.Clone();

        var begin = trialExtraLedger.BeginTurn(frame.Turn);
        if (!begin.IsValid)
        {
            return Fail(
                ForwardTurnRealizationStatus.ExtraSpinTimelineInvalid,
                begin.Detail,
                collected: preview.Collected);
        }

        var featurePlacement = new ForwardFeaturePlacementAdapter(_settings, objectives.MaxSymbol, _rng.Next()).Plan(
            frame.Turn,
            plannedTotalTurns,
            objectives,
            frame.FeatureIntents,
            preview.EmptyPositions,
            Array.Empty<(int r, int c)>(),
            futureTurns,
            remainingFeatureCapacity,
            trialSymbolLedger,
            trialExtraLedger,
            trialPrizeLedger,
            preview.BoardAfterPushRotate);
        if (!featurePlacement.IsValid)
        {
            return Fail(
                ForwardTurnRealizationStatus.FeaturePlacementInvalid,
                featurePlacement.Detail,
                collected: preview.Collected);
        }

        var blockedCollectSymbols = featurePlacement.WheelImpacts
            .Select(wheel => wheel.Symbol)
            .Where(symbol => !objectives.WinTargets.ContainsKey(symbol))
            .Distinct()
            .ToHashSet();
        var normalPositions = preview.EmptyPositions
            .Except(featurePlacement.UsedPositions)
            .ToArray();
        var normalIntentResult = new ForwardNormalIntentPlanner(_settings).Plan(
            frame.Turn,
            objectives,
            normalPositions,
            futureTurns,
            trialSymbolLedger,
            featurePlacement.WheelImpacts);
        if (!normalIntentResult.IsValid)
        {
            return Fail(
                ForwardTurnRealizationStatus.NormalIntentPlanningFailed,
                normalIntentResult.Detail,
                collected: preview.Collected,
                normalIntentResult: normalIntentResult);
        }

        var normalRequests = BuildNormalRequests(
            normalIntentResult.Intents,
            normalPositions,
            futureTurns);
        if (!normalRequests.IsValid)
        {
            return Fail(
                ForwardTurnRealizationStatus.NormalIntentCannotBeSatisfied,
                normalRequests.Detail,
                collected: preview.Collected,
                normalIntentResult: normalIntentResult);
        }

        var normalSpawns = new ForwardSpawnPlanner(
            new ForwardSymbolSelector(
                trialSymbolLedger,
                objectives.WinTargets,
                objectives.NearMissTargets,
                objectives.FillSymbols,
                objectives.MaxSymbol,
                _settings,
                new Random(_rng.Next()),
                blockedCollectSymbols,
                objectives.TopPrizeSymbol,
                plannedTotalTurns),
            _fateAnalyzer,
            _settings).Plan(
                normalRequests.Requests,
                futureTurns,
                frame.Turn,
                featurePlacement.WheelImpacts);
        if (!normalSpawns.IsValid)
        {
            return Fail(
                ForwardTurnRealizationStatus.NormalSpawnInvalid,
                normalSpawns.Detail,
                collected: preview.Collected,
                normalIntentResult: normalIntentResult);
        }

        var spawns = featurePlacement.Spawns
            .Concat(normalSpawns.Spawns)
            .OrderBy(spawn => spawn.Row)
            .ThenBy(spawn => spawn.Col)
            .ToArray();
        var tempBoard = new ForwardBoardState(boardState.Snapshot(), _settings);
        var tempAdvance = tempBoard.Advance(frame.Shape, spawns);
        if (!tempAdvance.IsValid)
        {
            return Fail(
                ForwardTurnRealizationStatus.BoardAdvanceInvalid,
                tempAdvance.Detail,
                collected: preview.Collected,
                normalIntentResult: normalIntentResult);
        }

        var realAdvance = boardState.Advance(frame.Shape, spawns);
        if (!realAdvance.IsValid)
        {
            return Fail(
                ForwardTurnRealizationStatus.BoardAdvanceInvalid,
                realAdvance.Detail,
                collected: preview.Collected,
                normalIntentResult: normalIntentResult);
        }

        symbolLedger.ReplaceWith(trialSymbolLedger);
        extraSpinLedger.ReplaceWith(trialExtraLedger);
        prizeUpgradeLedger.ReplaceWith(trialPrizeLedger);

        return new ForwardTurnRealizationResult(
            ForwardTurnRealizationStatus.Valid,
            "ok",
            spawns,
            realAdvance.Collected,
            featurePlacement.WheelImpacts,
            normalIntentResult,
            featurePlacement.FlushCount);
    }

    private NormalRequestBuildResult BuildNormalRequests(
        IReadOnlyList<ForwardNormalSpawnIntent> intents,
        IReadOnlyList<(int r, int c)> positions,
        IReadOnlyList<ForwardFutureTurn> futureTurns)
    {
        if (intents.Count != positions.Count)
            return NormalRequestBuildResult.Fail($"normal intents={intents.Count}, remaining positions={positions.Count}");

        var fates = positions
            .Select(position => (Position: position, Fate: _fateAnalyzer.Analyze(position.r, position.c, futureTurns)))
            .ToArray();
        var invalid = fates.FirstOrDefault(item => !item.Fate.IsValid);
        if (invalid.Fate.Status != ForwardCellFateStatus.Valid)
            return NormalRequestBuildResult.Fail(invalid.Fate.Detail);

        var collected = fates
            .Where(item => item.Fate.IsCollected)
            .OrderBy(item => item.Fate.CollectedTurn)
            .ThenBy(_ => _rng.Next())
            .Select(item => item.Position)
            .ToList();
        var residue = fates
            .Where(item => !item.Fate.IsCollected)
            .OrderBy(_ => _rng.Next())
            .Select(item => item.Position)
            .ToList();

        var requests = new List<ForwardSpawnCellRequest>(intents.Count);
        foreach (var intent in Shuffled(intents.Where(intent => intent.RequiresCollected)))
        {
            if (collected.Count == 0)
                return NormalRequestBuildResult.Fail($"{intent.CollectIntent} requires a future-collected cell");
            var index = _rng.Next(collected.Count);
            AddRequest(requests, collected[index], intent);
            collected.RemoveAt(index);
        }

        foreach (var intent in Shuffled(intents.Where(intent => intent.RequiresResidue)))
        {
            if (residue.Count == 0)
                return NormalRequestBuildResult.Fail($"{intent.CollectIntent} requires a residue cell");
            var index = _rng.Next(residue.Count);
            AddRequest(requests, residue[index], intent);
            residue.RemoveAt(index);
        }

        foreach (var intent in Shuffled(intents.Where(intent => !intent.RequiresCollected && !intent.RequiresResidue)))
        {
            var targetList = collected.Count > 0 ? collected : residue;
            if (targetList.Count == 0)
                return NormalRequestBuildResult.Fail($"{intent.CollectIntent} has no remaining cell");
            var index = _rng.Next(targetList.Count);
            AddRequest(requests, targetList[index], intent);
            targetList.RemoveAt(index);
        }

        return NormalRequestBuildResult.Ok(requests);
    }

    private IReadOnlyList<ForwardNormalSpawnIntent> Shuffled(IEnumerable<ForwardNormalSpawnIntent> intents) =>
        intents.OrderBy(_ => _rng.Next()).ToArray();

    private static void AddRequest(
        List<ForwardSpawnCellRequest> requests,
        (int r, int c) position,
        ForwardNormalSpawnIntent intent) =>
        requests.Add(new ForwardSpawnCellRequest(
            position.r,
            position.c,
            intent.CollectIntent,
            intent.LedgerCollectionValue,
            intent.SpawnStack));

    private static ForwardTurnRealizationResult Fail(
        ForwardTurnRealizationStatus status,
        string detail,
        IReadOnlyDictionary<int, int>? collected = null,
        ForwardNormalIntentResult? normalIntentResult = null) =>
        new(
            status,
            detail,
            Array.Empty<ForwardSpawn>(),
            collected ?? new Dictionary<int, int>(),
            Array.Empty<ForwardWheelImpact>(),
            normalIntentResult,
            0);

    private sealed class NormalRequestBuildResult
    {
        private NormalRequestBuildResult(
            string detail,
            IReadOnlyList<ForwardSpawnCellRequest> requests)
        {
            Detail = detail;
            Requests = requests;
        }

        internal string Detail { get; }
        internal IReadOnlyList<ForwardSpawnCellRequest> Requests { get; }
        internal bool IsValid => Detail == "ok";

        internal static NormalRequestBuildResult Ok(IReadOnlyList<ForwardSpawnCellRequest> requests) =>
            new("ok", requests);

        internal static NormalRequestBuildResult Fail(string detail) =>
            new(detail, Array.Empty<ForwardSpawnCellRequest>());
    }
}
