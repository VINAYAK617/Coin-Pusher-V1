namespace CoinPusherEngine;

internal enum ForwardTurnAssemblyStatus
{
    Valid,
    MissingBoardState,
    MissingShape,
    MissingObjectives,
    MissingFeatureIntents,
    MissingNormalIntents,
    MissingFutureTurns,
    MissingFeatureCapacity,
    MissingLedgers,
    InvalidTurn,
    BoardPreviewInvalid,
    FeaturePlacementInvalid,
    NormalIntentCountMismatch,
    NormalIntentCannotBeSatisfied,
    NormalSpawnInvalid,
    BoardAdvanceInvalid,
}

internal readonly struct ForwardNormalSpawnIntent
{
    internal ForwardNormalSpawnIntent(
        ForwardSymbolIntent collectIntent,
        int ledgerCollectionValue = 1,
        int spawnStack = 1)
    {
        CollectIntent = collectIntent;
        LedgerCollectionValue = ledgerCollectionValue;
        SpawnStack = spawnStack;
    }

    internal ForwardSymbolIntent CollectIntent { get; }
    internal int LedgerCollectionValue { get; }
    internal int SpawnStack { get; }
    internal bool RequiresCollected =>
        CollectIntent == ForwardSymbolIntent.MustProgressWin
        || CollectIntent == ForwardSymbolIntent.PreferNearMiss;
    internal bool RequiresResidue => CollectIntent == ForwardSymbolIntent.ResidueOnly;

    internal static ForwardNormalSpawnIntent SafeFiller() =>
        new(ForwardSymbolIntent.SafeFiller);
}

internal sealed class ForwardTurnAssemblyResult
{
    internal ForwardTurnAssemblyResult(
        ForwardTurnAssemblyStatus status,
        string detail,
        IReadOnlyList<ForwardSpawn> spawns,
        IReadOnlyDictionary<int, int> collected,
        IReadOnlyList<ForwardWheelImpact> wheelImpacts,
        int flushCount)
    {
        Status = status;
        Detail = detail;
        Spawns = spawns;
        Collected = collected;
        WheelImpacts = wheelImpacts;
        FlushCount = flushCount;
    }

    internal ForwardTurnAssemblyStatus Status { get; }
    internal string Detail { get; }
    internal IReadOnlyList<ForwardSpawn> Spawns { get; }
    internal IReadOnlyDictionary<int, int> Collected { get; }
    internal IReadOnlyList<ForwardWheelImpact> WheelImpacts { get; }
    internal int FlushCount { get; }
    internal bool IsValid => Status == ForwardTurnAssemblyStatus.Valid;
}

internal sealed class ForwardTurnAssembler
{
    private readonly Settings _settings;
    private readonly Random _rng;

    internal ForwardTurnAssembler(Settings settings, int seed)
    {
        _settings = settings;
        _rng = new Random(seed);
    }

    internal ForwardTurnAssemblyResult AssembleAndAdvance(
        int turn,
        int plannedTotalTurns,
        ForwardBoardState? boardState,
        ForwardTurnShape? shape,
        ForwardObjectives? objectives,
        IReadOnlyList<ForwardFeatureIntent>? featureIntents,
        IReadOnlyList<ForwardNormalSpawnIntent>? normalIntents,
        IReadOnlyList<ForwardFutureTurn>? futureTurns,
        IReadOnlyDictionary<ForwardFeatureKind, int>? remainingFeatureCapacity,
        SymbolLedger? symbolLedger,
        ForwardExtraSpinLedger? extraSpinLedger,
        ForwardPrizeUpgradeLedger? prizeUpgradeLedger)
    {
        if (turn <= 0 || plannedTotalTurns <= 0 || turn > plannedTotalTurns)
            return Fail(ForwardTurnAssemblyStatus.InvalidTurn, $"turn={turn}, plannedTotalTurns={plannedTotalTurns}");
        if (boardState == null)
            return Fail(ForwardTurnAssemblyStatus.MissingBoardState, "board state is missing");
        if (shape == null)
            return Fail(ForwardTurnAssemblyStatus.MissingShape, "turn shape is missing");
        if (objectives == null)
            return Fail(ForwardTurnAssemblyStatus.MissingObjectives, "forward objectives are missing");
        if (featureIntents == null)
            return Fail(ForwardTurnAssemblyStatus.MissingFeatureIntents, "feature intents are missing");
        if (normalIntents == null)
            return Fail(ForwardTurnAssemblyStatus.MissingNormalIntents, "normal spawn intents are missing");
        if (futureTurns == null)
            return Fail(ForwardTurnAssemblyStatus.MissingFutureTurns, "future turns are missing");
        if (remainingFeatureCapacity == null)
            return Fail(ForwardTurnAssemblyStatus.MissingFeatureCapacity, "feature capacity is missing");
        if (symbolLedger == null || extraSpinLedger == null || prizeUpgradeLedger == null)
            return Fail(ForwardTurnAssemblyStatus.MissingLedgers, "one or more ledgers are missing");

        var preview = boardState.PreviewAfterPushRotate(shape);
        if (!preview.IsValid)
            return Fail(ForwardTurnAssemblyStatus.BoardPreviewInvalid, preview.Detail, preview.Collected);

        var trialSymbolLedger = symbolLedger.Clone();
        var trialExtraLedger = extraSpinLedger.Clone();
        var trialPrizeLedger = prizeUpgradeLedger.Clone();

        var featurePlacement = new ForwardFeaturePlacementAdapter(_settings, objectives.MaxSymbol, _rng.Next()).Plan(
            turn,
            plannedTotalTurns,
            objectives,
            featureIntents,
            preview.EmptyPositions,
            Array.Empty<(int r, int c)>(),
            futureTurns,
            remainingFeatureCapacity,
            trialSymbolLedger,
            trialExtraLedger,
            trialPrizeLedger);
        if (!featurePlacement.IsValid)
        {
            return Fail(
                ForwardTurnAssemblyStatus.FeaturePlacementInvalid,
                featurePlacement.Detail,
                preview.Collected);
        }

        var remainingPositions = preview.EmptyPositions
            .Except(featurePlacement.UsedPositions)
            .ToArray();
        var normalRequests = BuildNormalRequests(normalIntents, remainingPositions, futureTurns);
        if (!normalRequests.IsValid)
        {
            return Fail(
                normalRequests.Status,
                normalRequests.Detail,
                preview.Collected);
        }

        var normalSpawns = new ForwardSpawnPlanner(
            new ForwardSymbolSelector(
                trialSymbolLedger,
                objectives.WinTargets,
                objectives.NearMissTargets,
                objectives.FillSymbols,
                objectives.MaxSymbol,
                _settings,
                new Random(_rng.Next())),
            new ForwardCellFateAnalyzer(_settings),
            _settings).Plan(
                normalRequests.Requests,
                futureTurns);
        if (!normalSpawns.IsValid)
        {
            return Fail(
                ForwardTurnAssemblyStatus.NormalSpawnInvalid,
                normalSpawns.Detail,
                preview.Collected);
        }

        var spawns = featurePlacement.Spawns
            .Concat(normalSpawns.Spawns)
            .OrderBy(spawn => spawn.Row)
            .ThenBy(spawn => spawn.Col)
            .ToArray();

        var tempBoard = new ForwardBoardState(boardState.Snapshot(), _settings);
        var tempAdvance = tempBoard.Advance(shape, spawns);
        if (!tempAdvance.IsValid)
        {
            return Fail(
                ForwardTurnAssemblyStatus.BoardAdvanceInvalid,
                tempAdvance.Detail,
                preview.Collected);
        }

        var realAdvance = boardState.Advance(shape, spawns);
        if (!realAdvance.IsValid)
        {
            return Fail(
                ForwardTurnAssemblyStatus.BoardAdvanceInvalid,
                realAdvance.Detail,
                preview.Collected);
        }

        symbolLedger.ReplaceWith(trialSymbolLedger);
        extraSpinLedger.ReplaceWith(trialExtraLedger);
        prizeUpgradeLedger.ReplaceWith(trialPrizeLedger);

        return new ForwardTurnAssemblyResult(
            ForwardTurnAssemblyStatus.Valid,
            "ok",
            spawns,
            realAdvance.Collected,
            featurePlacement.WheelImpacts,
            featurePlacement.FlushCount);
    }

    private NormalRequestBuildResult BuildNormalRequests(
        IReadOnlyList<ForwardNormalSpawnIntent> intents,
        IReadOnlyList<(int r, int c)> positions,
        IReadOnlyList<ForwardFutureTurn> futureTurns)
    {
        if (intents.Count != positions.Count)
        {
            return NormalRequestBuildResult.Fail(
                ForwardTurnAssemblyStatus.NormalIntentCountMismatch,
                $"normal intents={intents.Count}, remaining empty positions={positions.Count}");
        }

        var fates = positions
            .Select(position => (Position: position, Fate: new ForwardCellFateAnalyzer(_settings).Analyze(position.r, position.c, futureTurns)))
            .ToArray();
        var invalid = fates.FirstOrDefault(item => !item.Fate.IsValid);
        if (invalid.Fate.Status != ForwardCellFateStatus.Valid)
        {
            return NormalRequestBuildResult.Fail(
                ForwardTurnAssemblyStatus.NormalIntentCannotBeSatisfied,
                invalid.Fate.Detail);
        }

        var collected = fates
            .Where(item => item.Fate.IsCollected)
            .OrderBy(item => item.Fate.CollectedTurn)
            .ThenBy(item => item.Position.r)
            .ThenBy(item => item.Position.c)
            .Select(item => item.Position)
            .ToList();
        var residue = fates
            .Where(item => !item.Fate.IsCollected)
            .OrderBy(item => item.Position.r)
            .ThenBy(item => item.Position.c)
            .Select(item => item.Position)
            .ToList();

        var requests = new List<ForwardSpawnCellRequest>(intents.Count);
        foreach (var intent in intents.Where(intent => intent.RequiresCollected))
        {
            if (collected.Count == 0)
                return CannotPlace(intent, "requires a future-collected cell");
            AddRequest(requests, collected[0], intent);
            collected.RemoveAt(0);
        }

        foreach (var intent in intents.Where(intent => intent.RequiresResidue))
        {
            if (residue.Count == 0)
                return CannotPlace(intent, "requires a residue cell");
            AddRequest(requests, residue[0], intent);
            residue.RemoveAt(0);
        }

        foreach (var intent in intents.Where(intent => !intent.RequiresCollected && !intent.RequiresResidue))
        {
            var targetList = collected.Count > 0 ? collected : residue;
            if (targetList.Count == 0)
                return CannotPlace(intent, "has no remaining cell");
            AddRequest(requests, targetList[0], intent);
            targetList.RemoveAt(0);
        }

        return NormalRequestBuildResult.Ok(requests);
    }

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

    private static NormalRequestBuildResult CannotPlace(
        ForwardNormalSpawnIntent intent,
        string reason) =>
        NormalRequestBuildResult.Fail(
            ForwardTurnAssemblyStatus.NormalIntentCannotBeSatisfied,
            $"{intent.CollectIntent} {reason}");

    private static ForwardTurnAssemblyResult Fail(
        ForwardTurnAssemblyStatus status,
        string detail,
        IReadOnlyDictionary<int, int>? collected = null) =>
        new(
            status,
            detail,
            Array.Empty<ForwardSpawn>(),
            collected ?? new Dictionary<int, int>(),
            Array.Empty<ForwardWheelImpact>(),
            0);

    private sealed class NormalRequestBuildResult
    {
        private NormalRequestBuildResult(
            ForwardTurnAssemblyStatus status,
            string detail,
            IReadOnlyList<ForwardSpawnCellRequest> requests)
        {
            Status = status;
            Detail = detail;
            Requests = requests;
        }

        internal ForwardTurnAssemblyStatus Status { get; }
        internal string Detail { get; }
        internal IReadOnlyList<ForwardSpawnCellRequest> Requests { get; }
        internal bool IsValid => Status == ForwardTurnAssemblyStatus.Valid;

        internal static NormalRequestBuildResult Ok(IReadOnlyList<ForwardSpawnCellRequest> requests) =>
            new(ForwardTurnAssemblyStatus.Valid, "ok", requests);

        internal static NormalRequestBuildResult Fail(ForwardTurnAssemblyStatus status, string detail) =>
            new(status, detail, Array.Empty<ForwardSpawnCellRequest>());
    }
}
