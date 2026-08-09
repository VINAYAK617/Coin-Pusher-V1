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
        IReadOnlyList<ForwardWheelImpact> wheelImpacts,
        IReadOnlyList<(int r, int c)> usedPositions,
        IReadOnlyList<ForwardFeatureSpawnRequest> requests,
        int flushCount)
    {
        Status = status;
        Detail = detail;
        Spawns = spawns;
        WheelImpacts = wheelImpacts;
        UsedPositions = usedPositions;
        Requests = requests;
        FlushCount = flushCount;
    }

    internal ForwardFeaturePlacementStatus Status { get; }
    internal string Detail { get; }
    internal IReadOnlyList<ForwardSpawn> Spawns { get; }
    internal IReadOnlyList<ForwardWheelImpact> WheelImpacts { get; }
    internal IReadOnlyList<(int r, int c)> UsedPositions { get; }
    internal IReadOnlyList<ForwardFeatureSpawnRequest> Requests { get; }
    internal int FlushCount { get; }
    internal bool IsValid => Status == ForwardFeaturePlacementStatus.Valid;
}

internal sealed class ForwardFeaturePlacementAdapter
{
    private readonly Settings _settings;
    private readonly ForwardCellFateAnalyzer _fateAnalyzer;
    private readonly ForwardFeatureSpawnPlanner _featureSpawnPlanner;
    private readonly Random _rng;

    internal ForwardFeaturePlacementAdapter(Settings settings, int maxSymbol, int seed)
    {
        _settings = settings;
        _fateAnalyzer = new ForwardCellFateAnalyzer(settings);
        _featureSpawnPlanner = new ForwardFeatureSpawnPlanner(settings, maxSymbol);
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
        ForwardPrizeUpgradeLedger? prizeUpgradeLedger)
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
            .OrderBy(intent => intent.Kind)
            .ToArray();
        if (boardIntents.Length == 0)
        {
            return new ForwardFeaturePlacementResult(
                ForwardFeaturePlacementStatus.Valid,
                "no board feature intents",
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

        var trialSymbolLedger = symbolLedger.Clone();
        var trialRequests = new List<ForwardFeatureSpawnRequest>();
        var reservations = new List<ConvertReservation>();
        foreach (var intent in boardIntents)
        {
            var placed = TryPlaceIntent(
                intent,
                available,
                futureTurns,
                objectives,
                trialSymbolLedger);
            if (!placed.IsValid)
                return placed.Result!;

            trialSymbolLedger = placed.SymbolLedger!;
            trialRequests.Add(placed.Request!.Value);
            reservations.Add(placed.Reservation!.Value);
            available.Remove((placed.Request.Value.Row, placed.Request.Value.Col));
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

        foreach (var reservation in reservations.Where(reservation => reservation.IsCollected))
        {
            var commit = symbolLedger.Collect(reservation.ConvertSymbol);
            if (!commit.IsValid)
            {
                return Fail(
                    ForwardFeaturePlacementStatus.CommitFailed,
                    $"symbol ledger commit failed for ConvertToId={reservation.ConvertSymbol}: {commit.Detail}",
                    flushCount: flushCount);
            }
        }

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
            committedSpawn.WheelImpacts,
            trialRequests.Select(request => (request.Row, request.Col)).ToArray(),
            trialRequests,
            flushCount);
    }

    private FeatureSlotAttempt TryPlaceIntent(
        ForwardFeatureIntent intent,
        IReadOnlyList<(int r, int c)> available,
        IReadOnlyList<ForwardFutureTurn> futureTurns,
        ForwardObjectives objectives,
        SymbolLedger currentTrialLedger)
    {
        foreach (var position in OrderedCandidatePositions(available, intent, futureTurns))
        {
            var candidateLedger = currentTrialLedger.Clone();
            var convert = SelectConvertSymbol(
                intent,
                position,
                futureTurns,
                objectives,
                CreateSelector(objectives, candidateLedger));
            if (!convert.IsValid)
                continue;

            var request = BuildRequest(intent, position, convert.ConvertSymbol);
            if (!request.HasValue)
            {
                return FeatureSlotAttempt.Fail(Fail(
                    ForwardFeaturePlacementStatus.FeatureSpawnInvalid,
                    $"cannot build feature spawn request for {intent.Kind}"));
            }

            return FeatureSlotAttempt.Ok(
                request.Value,
                new ConvertReservation(
                    position.r,
                    position.c,
                    convert.ConvertSymbol,
                    convert.IsCollected),
                candidateLedger);
        }

        return FeatureSlotAttempt.Fail(Fail(
            ForwardFeaturePlacementStatus.NoSafeConvertSymbol,
            $"no safe ConvertToId found for {intent.Kind} on turn {intent.Turn}"));
    }

    private IEnumerable<(int r, int c)> OrderedCandidatePositions(
        IReadOnlyList<(int r, int c)> available,
        ForwardFeatureIntent intent,
        IReadOnlyList<ForwardFutureTurn> futureTurns)
    {
        return available
            .Select(position => (Position: position, Fate: _fateAnalyzer.Analyze(position.r, position.c, futureTurns)))
            .Where(item => item.Fate.IsValid)
            .OrderBy(item => PositionScore(intent, item.Fate))
            .ThenBy(_ => _rng.Next())
            .Select(item => item.Position);
    }

    private static int PositionScore(ForwardFeatureIntent intent, ForwardCellFate fate)
    {
        if (intent.Kind == ForwardTimedFeatureKind.PrizeUpgrade)
            return fate.IsCollected ? 1 : 0;
        if (intent.Kind == ForwardTimedFeatureKind.Wheel)
            return fate.IsCollected ? 1 : 0;
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
            var selected = selector.TryCollectSpecific(preferred);
            if (selected.IsValid)
                return ConvertSelection.Ok(selected.Symbol, isCollected: true);
        }

        foreach (var fallbackIntent in ConvertFallbackOrder())
        {
            var selected = selector.ChooseAndCollect(fallbackIntent);
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
        yield return ForwardSymbolIntent.SafeFiller;
        yield return ForwardSymbolIntent.MustProgressWin;
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
            ForwardTimedFeatureKind.Wheel => ForwardFeatureSpawnRequest.Wheel(
                position.r,
                position.c,
                convertSymbol,
                intent.WheelSymbol!.Value,
                intent.ResultingWheelStack!.Value),
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

    private ForwardSymbolSelector CreateSelector(
        ForwardObjectives objectives,
        SymbolLedger ledger) =>
        new(
            ledger,
            objectives.WinTargets,
            objectives.NearMissTargets,
            objectives.FillSymbols,
            objectives.MaxSymbol,
            _settings,
            new Random(_rng.Next()));

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
            Array.Empty<ForwardWheelImpact>(),
            Array.Empty<(int r, int c)>(),
            Array.Empty<ForwardFeatureSpawnRequest>(),
            flushCount);

    private readonly struct ConvertReservation
    {
        internal ConvertReservation(
            int row,
            int col,
            int convertSymbol,
            bool isCollected)
        {
            Row = row;
            Col = col;
            ConvertSymbol = convertSymbol;
            IsCollected = isCollected;
        }

        internal int Row { get; }
        internal int Col { get; }
        internal int ConvertSymbol { get; }
        internal bool IsCollected { get; }
    }

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
            ConvertReservation? reservation,
            SymbolLedger? symbolLedger,
            ForwardFeaturePlacementResult? result)
        {
            Request = request;
            Reservation = reservation;
            SymbolLedger = symbolLedger;
            Result = result;
        }

        internal ForwardFeatureSpawnRequest? Request { get; }
        internal ConvertReservation? Reservation { get; }
        internal SymbolLedger? SymbolLedger { get; }
        internal ForwardFeaturePlacementResult? Result { get; }
        internal bool IsValid => Result == null;

        internal static FeatureSlotAttempt Ok(
            ForwardFeatureSpawnRequest request,
            ConvertReservation reservation,
            SymbolLedger symbolLedger) =>
            new(request, reservation, symbolLedger, null);

        internal static FeatureSlotAttempt Fail(ForwardFeaturePlacementResult result) =>
            new(null, null, null, result);
    }
}
