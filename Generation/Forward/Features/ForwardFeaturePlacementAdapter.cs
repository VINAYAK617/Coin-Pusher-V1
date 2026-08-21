namespace CoinPusherEngine;

internal enum ForwardFeaturePlacementStatus
{
    Valid,
    MissingObjectives,
    MissingIntents,
    MissingEmptyPositions,
    MissingReservedPositions,
    MissingFutureTurns,
    MissingFeatureCapacity,
    MissingLedgers,
    InvalidTurn,
    NotEnoughFeatureSlots,
    CellFateInvalid,
    NoSafeConvertSymbol,
    FeatureSpawnInvalid,
    CommitFailed,
}

internal sealed class ForwardFeaturePlacementResult
{
    internal ForwardFeaturePlacementResult(
        ForwardFeaturePlacementStatus status,
        string detail,
        IReadOnlyList<ForwardSpawn> spawns,
        IReadOnlyList<ForwardSpawn> anchorSpawns,
        IReadOnlyList<ForwardWheelImpact> wheelImpacts,
        IReadOnlyList<(int r, int c)> usedPositions,
        IReadOnlyList<ForwardFeatureSpawnRequest> requests,
        int flushCount)
    {
        Status = status;
        Detail = detail;
        Spawns = spawns;
        AnchorSpawns = anchorSpawns;
        WheelImpacts = wheelImpacts;
        UsedPositions = usedPositions;
        Requests = requests;
        FlushCount = flushCount;
    }

    internal ForwardFeaturePlacementStatus Status { get; }
    internal string Detail { get; }
    internal IReadOnlyList<ForwardSpawn> Spawns { get; }
    internal IReadOnlyList<ForwardSpawn> AnchorSpawns { get; }
    internal IReadOnlyList<ForwardWheelImpact> WheelImpacts { get; }
    internal IReadOnlyList<(int r, int c)> UsedPositions { get; }
    internal IReadOnlyList<ForwardFeatureSpawnRequest> Requests { get; }
    internal int FlushCount { get; }
    internal bool IsValid => Status == ForwardFeaturePlacementStatus.Valid;
}

internal sealed class ForwardFeaturePlacementAdapter
{
    private readonly ICustomProfileSettings _settings;
    private readonly ForwardCellFateAnalyzer _fateAnalyzer;
    private readonly ForwardFeatureSpawnPlanner _featureSpawnPlanner;
    private readonly ForwardWheelSequenceLedger _wheelSequenceLedger;
    private readonly Random _rng;

    internal ForwardFeaturePlacementAdapter(ICustomProfileSettings settings, int maxSymbol, int seed)
    {
        _settings = settings;
        _fateAnalyzer = new ForwardCellFateAnalyzer(settings);
        _featureSpawnPlanner = new ForwardFeatureSpawnPlanner(settings, maxSymbol);
        _wheelSequenceLedger = new ForwardWheelSequenceLedger(settings);
        _rng = new Random(seed);
    }

    internal ForwardFeaturePlacementResult Plan(
        int turn,
        int plannedTotalTurns,
        ForwardObjectives? objectives,
        IReadOnlyList<ForwardFeatureIntent>? intents,
        IReadOnlyCollection<(int r, int c)>? emptyPositions,
        IReadOnlyCollection<(int r, int c)>? reservedPositions,
        IReadOnlyList<ForwardFutureTurn>? futureTurns,
        IReadOnlyDictionary<ForwardFeatureKind, int>? remainingFeatureCapacity,
        SymbolLedger? symbolLedger,
        ForwardExtraSpinLedger? extraSpinLedger,
        ForwardPrizeUpgradeLedger? prizeUpgradeLedger,
        Cell?[,]? boardAfterPushRotate = null)
    {
        if (turn <= 0 || plannedTotalTurns <= 0 || turn > plannedTotalTurns)
            return Fail(ForwardFeaturePlacementStatus.InvalidTurn, $"turn={turn}, plannedTotalTurns={plannedTotalTurns}");
        if (objectives == null)
            return Fail(ForwardFeaturePlacementStatus.MissingObjectives, "forward objectives are missing");
        if (intents == null)
            return Fail(ForwardFeaturePlacementStatus.MissingIntents, "feature intents are missing");
        if (emptyPositions == null)
            return Fail(ForwardFeaturePlacementStatus.MissingEmptyPositions, "empty positions are missing");
        if (reservedPositions == null)
            return Fail(ForwardFeaturePlacementStatus.MissingReservedPositions, "reserved positions are missing");
        if (futureTurns == null)
            return Fail(ForwardFeaturePlacementStatus.MissingFutureTurns, "future turns are missing");
        if (remainingFeatureCapacity == null)
            return Fail(ForwardFeaturePlacementStatus.MissingFeatureCapacity, "feature capacity is missing");
        if (symbolLedger == null || extraSpinLedger == null || prizeUpgradeLedger == null)
            return Fail(ForwardFeaturePlacementStatus.MissingLedgers, "one or more ledgers are missing");

        var flushCount = intents.Count(intent => intent.Kind == ForwardTimedFeatureKind.Flush);
        var boardIntents = intents
            .Where(intent => intent.IsBoardFeature)
            .OrderBy(intent => intent.Kind == ForwardTimedFeatureKind.Wheel ? 1 : 0)
            .ThenBy(intent => intent.Kind)
            .ToArray();
        if (boardIntents.Length == 0)
        {
            return new ForwardFeaturePlacementResult(
                ForwardFeaturePlacementStatus.Valid,
                "no board feature intents",
                Array.Empty<ForwardSpawn>(),
                Array.Empty<ForwardSpawn>(),
                Array.Empty<ForwardWheelImpact>(),
                Array.Empty<(int r, int c)>(),
                Array.Empty<ForwardFeatureSpawnRequest>(),
                flushCount);
        }

        var available = emptyPositions
            .Except(reservedPositions)
            .Distinct()
            .OrderBy(position => position.r)
            .ThenBy(position => position.c)
            .ToList();
        if (available.Count < boardIntents.Length)
        {
            return Fail(
                ForwardFeaturePlacementStatus.NotEnoughFeatureSlots,
                $"need {boardIntents.Length} feature slot(s), have {available.Count}",
                flushCount: flushCount);
        }

        var turnStartSymbolLedger = symbolLedger.Clone();
        var trialSymbolLedger = turnStartSymbolLedger.Clone();
        var trialRequests = new List<ForwardFeatureSpawnRequest>();
        var trialAnchors = new List<ForwardSpawn>();
        var plannedWheelCollectionBonus = 0;
        foreach (var intent in boardIntents)
        {
            if (intent.Kind == ForwardTimedFeatureKind.Wheel && intent.IsCapacityRequiredWheel)
                plannedWheelCollectionBonus += intent.PlannedWheelCollectionBonus;
            var placed = TryPlaceIntent(
                intent,
                plannedTotalTurns,
                available,
                futureTurns,
                objectives,
                trialSymbolLedger,
                turnStartSymbolLedger,
                boardAfterPushRotate,
                trialRequests,
                trialAnchors,
                plannedWheelCollectionBonus);
            if (!placed.IsValid)
                return placed.Result!;

            trialSymbolLedger = placed.SymbolLedger!;
            trialRequests.Add(placed.Request!.Value);
            available.Remove((placed.Request.Value.Row, placed.Request.Value.Col));
            if (placed.Anchor.HasValue)
            {
                trialAnchors.Add(placed.Anchor.Value);
                available.Remove((placed.Anchor.Value.Row, placed.Anchor.Value.Col));
            }
        }

        var trialSpawn = _featureSpawnPlanner.Plan(
            turn,
            plannedTotalTurns,
            emptyPositions,
            reservedPositions,
            trialRequests,
            remainingFeatureCapacity,
            extraSpinLedger.Clone(),
            prizeUpgradeLedger.Clone());
        if (!trialSpawn.IsValid)
        {
            return Fail(
                ForwardFeaturePlacementStatus.FeatureSpawnInvalid,
                trialSpawn.Detail,
                flushCount: flushCount);
        }

        symbolLedger.ReplaceWith(trialSymbolLedger);

        var committedSpawn = _featureSpawnPlanner.Plan(
            turn,
            plannedTotalTurns,
            emptyPositions,
            reservedPositions,
            trialRequests,
            remainingFeatureCapacity,
            extraSpinLedger,
            prizeUpgradeLedger);
        if (!committedSpawn.IsValid)
        {
            return Fail(
                ForwardFeaturePlacementStatus.CommitFailed,
                committedSpawn.Detail,
                flushCount: flushCount);
        }

        return new ForwardFeaturePlacementResult(
            ForwardFeaturePlacementStatus.Valid,
            $"placed {committedSpawn.Spawns.Count} board feature(s)",
            committedSpawn.Spawns,
            trialAnchors,
            committedSpawn.WheelImpacts,
            trialRequests.Select(request => (request.Row, request.Col)).ToArray(),
            trialRequests,
            flushCount);
    }

    private FeatureSlotAttempt TryPlaceIntent(
        ForwardFeatureIntent intent,
        int plannedTotalTurns,
        IReadOnlyList<(int r, int c)> available,
        IReadOnlyList<ForwardFutureTurn> futureTurns,
        ForwardObjectives objectives,
        SymbolLedger currentTrialLedger,
        SymbolLedger turnStartSymbolLedger,
        Cell?[,]? boardAfterPushRotate,
        IReadOnlyList<ForwardFeatureSpawnRequest> sameTurnRequests,
        IReadOnlyList<ForwardSpawn> sameTurnAnchors,
        int plannedWheelCollectionBonus)
    {
        if (intent.Kind == ForwardTimedFeatureKind.Wheel)
        {
            return TryPlaceWheelIntent(
                intent,
                plannedTotalTurns,
                available,
                futureTurns,
                objectives,
                currentTrialLedger,
                turnStartSymbolLedger,
                boardAfterPushRotate,
                sameTurnRequests,
                sameTurnAnchors,
                plannedWheelCollectionBonus);
        }

        foreach (var position in OrderedCandidatePositions(available, intent, futureTurns))
        {
            var candidateLedger = currentTrialLedger.Clone();
            var convert = SelectConvertSymbol(
                intent,
                position,
                futureTurns,
                objectives,
                CreateSelector(
                    objectives,
                    candidateLedger,
                    blockedCollectSymbols: null,
                    plannedTotalTurns));
            if (!convert.IsValid)
                continue;

            var request = BuildRequest(
                intent,
                position,
                convert.ConvertSymbol);
            if (!request.HasValue)
                continue;

            return FeatureSlotAttempt.Ok(
                request.Value,
                candidateLedger);
        }

        return FeatureSlotAttempt.Fail(Fail(
            ForwardFeaturePlacementStatus.NoSafeConvertSymbol,
            $"no safe ConvertToId found for {intent.Kind} on turn {intent.Turn}"));
    }

    private FeatureSlotAttempt TryPlaceWheelIntent(
        ForwardFeatureIntent intent,
        int plannedTotalTurns,
        IReadOnlyList<(int r, int c)> available,
        IReadOnlyList<ForwardFutureTurn> futureTurns,
        ForwardObjectives objectives,
        SymbolLedger currentTrialLedger,
        SymbolLedger turnStartSymbolLedger,
        Cell?[,]? boardAfterPushRotate,
        IReadOnlyList<ForwardFeatureSpawnRequest> sameTurnRequests,
        IReadOnlyList<ForwardSpawn> sameTurnAnchors,
        int plannedWheelCollectionBonus)
    {
        var preferCollection = _rng.NextDouble() < _settings.PWheelStackCollection;
        var preferDenseTarget = !intent.IsCapacityRequiredWheel
            && _rng.NextDouble() < _settings.PWheelPreferDenseTarget;
        foreach (var position in OrderedCandidatePositions(
                     available,
                     intent,
                     futureTurns,
                     preferWheelCollection: preferCollection))
        {
            foreach (var convertSymbol in OrderedWheelConvertSymbols(intent, objectives))
            {
                var candidateLedger = currentTrialLedger.Clone();
                var request = BuildWheelRequest(
                    intent,
                    position,
                    convertSymbol,
                    boardAfterPushRotate,
                    futureTurns,
                    objectives,
                    candidateLedger,
                    turnStartSymbolLedger,
                    sameTurnRequests,
                    sameTurnAnchors,
                    plannedTotalTurns,
                    preferDenseTarget,
                    plannedWheelCollectionBonus);
                if (request.HasValue)
                    return FeatureSlotAttempt.Ok(request.Value, candidateLedger);

                var anchored = BuildAnchoredWheelRequest(
                    intent,
                    position,
                    convertSymbol,
                    available,
                    boardAfterPushRotate,
                    futureTurns,
                    objectives,
                    candidateLedger,
                    turnStartSymbolLedger,
                    sameTurnRequests,
                    sameTurnAnchors,
                    plannedTotalTurns,
                    preferCollection,
                    preferDenseTarget,
                    plannedWheelCollectionBonus);
                if (anchored.HasValue)
                {
                    return FeatureSlotAttempt.Ok(
                        anchored.Value.Request,
                        candidateLedger,
                        anchored.Value.Anchor);
                }
            }
        }

        return FeatureSlotAttempt.Fail(Fail(
            ForwardFeaturePlacementStatus.NoSafeConvertSymbol,
            $"no complete WHEEL position/ConvertToId/target assignment is safe on turn {intent.Turn}"));
    }

    private IReadOnlyList<int> OrderedWheelConvertSymbols(
        ForwardFeatureIntent intent,
        ForwardObjectives objectives) =>
        new[] { PreferredConvertSymbol(intent) }
            .Concat(objectives.NearMissTargets.Keys)
            .Concat(objectives.FillSymbols)
            .Concat(objectives.WinSymbols)
            .Where(symbol => ValidSymbol(symbol, objectives))
            .Distinct()
            .OrderBy(symbol => symbol == PreferredConvertSymbol(intent) ? 0 : 1)
            .ThenBy(symbol => objectives.NearMissTargets.ContainsKey(symbol) ? 0 :
                objectives.WinTargets.ContainsKey(symbol) ? 2 : 1)
            .ThenBy(symbol => symbol)
            .ToArray();

    private IEnumerable<(int r, int c)> OrderedCandidatePositions(
        IReadOnlyList<(int r, int c)> available,
        ForwardFeatureIntent intent,
        IReadOnlyList<ForwardFutureTurn> futureTurns,
        bool? preferWheelCollection = null)
    {
        return available
            .Select(position => (Position: position, Fate: _fateAnalyzer.Analyze(position.r, position.c, futureTurns)))
            .Where(item => item.Fate.IsValid)
            .OrderBy(item => PositionScore(intent, item.Fate, preferWheelCollection))
            .ThenBy(_ => _rng.Next())
            .Select(item => item.Position);
    }

    private static int PositionScore(
        ForwardFeatureIntent intent,
        ForwardCellFate fate,
        bool? preferWheelCollection)
    {
        if (intent.Kind == ForwardTimedFeatureKind.PrizeUpgrade)
            return fate.IsCollected ? 1 : 0;
        if (intent.Kind == ForwardTimedFeatureKind.Wheel)
            return fate.IsCollected == preferWheelCollection.GetValueOrDefault() ? 0 : 1;
        return fate.IsCollected ? 0 : 1;
    }

    private ConvertSelection SelectConvertSymbol(
        ForwardFeatureIntent intent,
        (int r, int c) position,
        IReadOnlyList<ForwardFutureTurn> futureTurns,
        ForwardObjectives objectives,
        ForwardSymbolSelector selector)
    {
        var fate = _fateAnalyzer.Analyze(position.r, position.c, futureTurns);
        if (!fate.IsValid)
        {
            return ConvertSelection.Fail(
                ForwardFeaturePlacementStatus.CellFateInvalid,
                fate.Detail);
        }

        var preferred = PreferredConvertSymbol(intent);
        if (!fate.IsCollected)
        {
            if (ValidSymbol(preferred, objectives))
                return ConvertSelection.Ok(preferred, isCollected: false);

            var residue = selector.ChooseResidue();
            return residue.IsValid
                ? ConvertSelection.Ok(residue.Symbol, isCollected: false)
                : ConvertSelection.Fail(ForwardFeaturePlacementStatus.NoSafeConvertSymbol, residue.Detail);
        }

        if (ValidSymbol(preferred, objectives))
        {
            var selected = selector.TryCollectSpecific(preferred, collectionTurn: fate.CollectedTurn);
            if (selected.IsValid)
                return ConvertSelection.Ok(selected.Symbol, isCollected: true);
        }

        foreach (var fallbackIntent in ConvertFallbackOrder())
        {
            var selected = selector.ChooseAndCollectExact(fallbackIntent, fate.CollectedTurn);
            if (selected.IsValid)
                return ConvertSelection.Ok(selected.Symbol, isCollected: true);
        }

        return ConvertSelection.Fail(
            ForwardFeaturePlacementStatus.NoSafeConvertSymbol,
            $"{fate.Detail}; no collectible ConvertToId is safe");
    }

    private IEnumerable<ForwardSymbolIntent> ConvertFallbackOrder()
    {
        yield return ForwardSymbolIntent.PreferNearMiss;
        yield return ForwardSymbolIntent.MustProgressWin;
        yield return ForwardSymbolIntent.SafeFiller;
    }

    private int PreferredConvertSymbol(ForwardFeatureIntent intent) =>
        intent.Kind switch
        {
            ForwardTimedFeatureKind.Wheel => intent.WheelSymbol ?? 0,
            ForwardTimedFeatureKind.PrizeUpgrade => intent.UpgradeSymbol ?? 0,
            _ => 0,
        };

    private ForwardFeatureSpawnRequest? BuildRequest(
        ForwardFeatureIntent intent,
        (int r, int c) position,
        int convertSymbol) =>
        intent.Kind switch
        {
            ForwardTimedFeatureKind.ExtraGo => ForwardFeatureSpawnRequest.ExtraGo(
                position.r,
                position.c,
                convertSymbol),
            ForwardTimedFeatureKind.PrizeUpgrade => ForwardFeatureSpawnRequest.PrizeUpgrade(
                position.r,
                position.c,
                convertSymbol,
                intent.UpgradeSymbol!.Value,
                intent.UpgradeTier!.Value),
            _ => null,
        };

    private ForwardFeatureSpawnRequest? BuildWheelRequest(
        ForwardFeatureIntent intent,
        (int r, int c) position,
        int convertSymbol,
        Cell?[,]? boardAfterPushRotate,
        IReadOnlyList<ForwardFutureTurn> futureTurns,
        ForwardObjectives objectives,
        SymbolLedger ledger,
        SymbolLedger turnStartSymbolLedger,
        IReadOnlyList<ForwardFeatureSpawnRequest> sameTurnRequests,
        IReadOnlyList<ForwardSpawn> sameTurnAnchors,
        int plannedTotalTurns,
        bool preferDenseTarget,
        int plannedWheelCollectionBonus)
    {
        var wheelStack = intent.ResultingWheelStack!.Value;
        foreach (var wheelSymbol in OrderedWheelSymbols(
                     intent,
                     objectives,
                     convertSymbol,
                     boardAfterPushRotate,
                     sameTurnRequests,
                     sameTurnAnchors,
                     preferDenseTarget))
        {
            var request = ForwardFeatureSpawnRequest.Wheel(
                position.r,
                position.c,
                convertSymbol,
                wheelSymbol,
                wheelStack);

            var sequence = _wheelSequenceLedger.Rebuild(
                boardAfterPushRotate,
                sameTurnRequests.Concat(new[] { request }).ToArray(),
                futureTurns,
                turnStartSymbolLedger,
                objectives.TopPrizeSymbol,
                plannedTotalTurns,
                sameTurnAnchors);
            if (!sequence.IsValid)
                continue;
            if (intent.IsCapacityRequiredWheel
                && sequence.CollectedWheelBonus != plannedWheelCollectionBonus)
                continue;

            ledger.ReplaceWith(sequence.Ledger!);
            return request;
        }

        return null;
    }

    private (ForwardFeatureSpawnRequest Request, ForwardSpawn Anchor)? BuildAnchoredWheelRequest(
        ForwardFeatureIntent intent,
        (int r, int c) featurePosition,
        int convertSymbol,
        IReadOnlyList<(int r, int c)> available,
        Cell?[,]? boardAfterPushRotate,
        IReadOnlyList<ForwardFutureTurn> futureTurns,
        ForwardObjectives objectives,
        SymbolLedger ledger,
        SymbolLedger turnStartSymbolLedger,
        IReadOnlyList<ForwardFeatureSpawnRequest> sameTurnRequests,
        IReadOnlyList<ForwardSpawn> sameTurnAnchors,
        int plannedTotalTurns,
        bool preferCollection,
        bool preferDenseTarget,
        int plannedWheelCollectionBonus)
    {
        if (available.Count < 2)
            return null;

        var wheelStack = intent.ResultingWheelStack!.Value;
        foreach (var wheelSymbol in OrderedWheelSymbols(
                     intent,
                     objectives,
                     convertSymbol,
                     boardAfterPushRotate,
                     sameTurnRequests,
                     sameTurnAnchors,
                     preferDenseTarget))
        {
            foreach (var anchorPosition in available
                         .Where(position => position != featurePosition)
                         .Select(position => (
                             Position: position,
                             Fate: _fateAnalyzer.Analyze(position.r, position.c, futureTurns)))
                         .Where(item => item.Fate.IsValid)
                         .OrderBy(item => item.Fate.IsCollected == preferCollection ? 0 : 1)
                         .ThenBy(_ => _rng.Next())
                         .Select(item => item.Position))
            {
                var request = ForwardFeatureSpawnRequest.Wheel(
                    featurePosition.r,
                    featurePosition.c,
                    convertSymbol,
                    wheelSymbol,
                    wheelStack);
                var anchor = new ForwardSpawn(
                    anchorPosition.r,
                    anchorPosition.c,
                    Grid.Norm(wheelSymbol));
                var sequence = _wheelSequenceLedger.Rebuild(
                    boardAfterPushRotate,
                    sameTurnRequests.Concat(new[] { request }).ToArray(),
                    futureTurns,
                    turnStartSymbolLedger,
                    objectives.TopPrizeSymbol,
                    plannedTotalTurns,
                    sameTurnAnchors.Concat(new[] { anchor }).ToArray());
                if (!sequence.IsValid)
                    continue;
                if (intent.IsCapacityRequiredWheel
                    && sequence.CollectedWheelBonus != plannedWheelCollectionBonus)
                    continue;

                ledger.ReplaceWith(sequence.Ledger!);
                return (request, anchor);
            }
        }

        return null;
    }

    private IReadOnlyList<int> OrderedWheelSymbols(
        ForwardFeatureIntent intent,
        ForwardObjectives objectives,
        int convertSymbol,
        Cell?[,]? boardAfterPushRotate,
        IReadOnlyList<ForwardFeatureSpawnRequest> sameTurnRequests,
        IReadOnlyList<ForwardSpawn> sameTurnAnchors,
        bool preferDenseTarget)
    {
        return new[] { convertSymbol }
            .Concat(objectives.FillSymbols
            .Where(symbol => !objectives.WinTargets.ContainsKey(symbol))
            .Where(symbol => !objectives.NearMissTargets.ContainsKey(symbol))
            .Concat(objectives.NearMissTargets.Keys))
            .Concat(objectives.WinSymbols)
            .Where(symbol => symbol >= 1 && symbol <= objectives.MaxSymbol && !_settings.IsFeat(symbol))
            .Distinct()
            .OrderByDescending(symbol => preferDenseTarget
                ? VisibleWheelTargetCount(symbol, boardAfterPushRotate, sameTurnRequests, sameTurnAnchors)
                : 0)
            .ThenBy(symbol => symbol == intent.WheelSymbol ? 0 : symbol == convertSymbol ? 1 : 2)
            .ThenBy(symbol => objectives.WinTargets.ContainsKey(symbol) ? 2 : objectives.NearMissTargets.ContainsKey(symbol) ? 1 : 0)
            .ThenBy(symbol => symbol)
            .ToArray();
    }

    private static int VisibleWheelTargetCount(
        int symbol,
        Cell?[,]? boardAfterPushRotate,
        IReadOnlyList<ForwardFeatureSpawnRequest> sameTurnRequests,
        IReadOnlyList<ForwardSpawn> sameTurnAnchors)
    {
        var count = 0;
        if (boardAfterPushRotate != null)
        {
            foreach (var cell in boardAfterPushRotate)
            {
                if (cell != null && !cell.IsFeat && cell.Sym == symbol)
                    count++;
            }
        }

        count += sameTurnAnchors.Count(spawn => spawn.Cell.Sym == symbol);
        count += sameTurnRequests.Count(request =>
            request.Kind != ForwardFeatureKind.Wheel
            && request.ConvertToSymbol == symbol);
        return count;
    }

    private ForwardSymbolSelector CreateSelector(
        ForwardObjectives objectives,
        SymbolLedger ledger,
        IReadOnlySet<int>? blockedCollectSymbols,
        int plannedTotalTurns) =>
        new(
            ledger,
            objectives.WinTargets,
            objectives.NearMissTargets,
            objectives.FillSymbols,
            objectives.MaxSymbol,
            _settings,
            new Random(_rng.Next()),
            blockedCollectSymbols,
            objectives.TopPrizeSymbol,
            plannedTotalTurns);

    private bool ValidSymbol(int symbol, ForwardObjectives objectives) =>
        symbol >= 1 && symbol <= objectives.MaxSymbol && !_settings.IsFeat(symbol);

    private static ForwardFeaturePlacementResult Fail(
        ForwardFeaturePlacementStatus status,
        string detail,
        int flushCount = 0) =>
        new(
            status,
            detail,
            Array.Empty<ForwardSpawn>(),
            Array.Empty<ForwardSpawn>(),
            Array.Empty<ForwardWheelImpact>(),
            Array.Empty<(int r, int c)>(),
            Array.Empty<ForwardFeatureSpawnRequest>(),
            flushCount);

    private readonly struct ConvertSelection
    {
        private ConvertSelection(
            ForwardFeaturePlacementStatus status,
            int convertSymbol,
            bool isCollected,
            string detail)
        {
            Status = status;
            ConvertSymbol = convertSymbol;
            IsCollected = isCollected;
            Detail = detail;
        }

        internal ForwardFeaturePlacementStatus Status { get; }
        internal int ConvertSymbol { get; }
        internal bool IsCollected { get; }
        internal string Detail { get; }
        internal bool IsValid => Status == ForwardFeaturePlacementStatus.Valid;

        internal static ConvertSelection Ok(int convertSymbol, bool isCollected) =>
            new(ForwardFeaturePlacementStatus.Valid, convertSymbol, isCollected, "ok");

        internal static ConvertSelection Fail(ForwardFeaturePlacementStatus status, string detail) =>
            new(status, 0, false, detail);
    }

    private sealed class FeatureSlotAttempt
    {
        private FeatureSlotAttempt(
            ForwardFeatureSpawnRequest? request,
            SymbolLedger? symbolLedger,
            ForwardSpawn? anchor,
            ForwardFeaturePlacementResult? result)
        {
            Request = request;
            SymbolLedger = symbolLedger;
            Anchor = anchor;
            Result = result;
        }

        internal ForwardFeatureSpawnRequest? Request { get; }
        internal SymbolLedger? SymbolLedger { get; }
        internal ForwardSpawn? Anchor { get; }
        internal ForwardFeaturePlacementResult? Result { get; }
        internal bool IsValid => Result == null;

        internal static FeatureSlotAttempt Ok(
            ForwardFeatureSpawnRequest request,
            SymbolLedger symbolLedger,
            ForwardSpawn? anchor = null) =>
            new(request, symbolLedger, anchor, null);

        internal static FeatureSlotAttempt Fail(ForwardFeaturePlacementResult result) =>
            new(null, null, null, result);
    }
}
