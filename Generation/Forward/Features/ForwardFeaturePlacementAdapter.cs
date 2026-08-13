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
    private readonly ForwardCellFateAnalyzer _fateAnalyzer;
    private readonly ForwardFeatureSpawnPlanner _featureSpawnPlanner;
    private readonly Random _rng;

    internal ForwardFeaturePlacementAdapter(int maxSymbol, int seed)
    {
        _fateAnalyzer = new ForwardCellFateAnalyzer();
        _featureSpawnPlanner = new ForwardFeatureSpawnPlanner(maxSymbol);
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
        var sameTurnWheelSymbols = new HashSet<int>();
        var sameTurnCollectedConvertSymbols = new HashSet<int>();
        foreach (var intent in boardIntents)
        {
            var placed = TryPlaceIntent(
                intent,
                available,
                futureTurns,
                objectives,
                trialSymbolLedger,
                boardAfterPushRotate,
                sameTurnWheelSymbols,
                sameTurnCollectedConvertSymbols);
            if (!placed.IsValid)
                return placed.Result!;

            trialSymbolLedger = placed.SymbolLedger!;
            trialRequests.Add(placed.Request!.Value);
            if (placed.Request.Value.Kind == ForwardFeatureKind.Wheel
                && placed.Request.Value.WheelSymbol.HasValue)
            {
                sameTurnWheelSymbols.Add(placed.Request.Value.WheelSymbol.Value);
            }
            reservations.Add(placed.Reservation!.Value);
            if (placed.Reservation.Value.IsCollected)
                sameTurnCollectedConvertSymbols.Add(placed.Reservation.Value.ConvertSymbol);
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

        var conversionBonuses = CommitSameTurnFeatureConversionWheelBonuses(
            reservations,
            trialRequests,
            trialSymbolLedger);
        if (!conversionBonuses.IsValid)
        {
            return Fail(
                ForwardFeaturePlacementStatus.CommitFailed,
                conversionBonuses.Detail,
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
        SymbolLedger currentTrialLedger,
        Cell?[,]? boardAfterPushRotate,
        IReadOnlySet<int> sameTurnWheelSymbols,
        IReadOnlySet<int> sameTurnCollectedConvertSymbols)
    {
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
                    sameTurnWheelSymbols,
                    futureTurns));
            if (!convert.IsValid)
                continue;

            var request = BuildRequest(
                intent,
                position,
                convert.ConvertSymbol,
                boardAfterPushRotate,
                futureTurns,
                objectives,
                candidateLedger,
                sameTurnCollectedConvertSymbols);
            if (!request.HasValue)
                continue;

            return FeatureSlotAttempt.Ok(
                request.Value,
                new ConvertReservation(
                    position.r,
                    position.c,
                    convert.ConvertSymbol,
                    convert.IsCollected,
                    convert.CollectionTurn,
                    request.Value.Kind),
                candidateLedger);
        }

        return FeatureSlotAttempt.Fail(Fail(
            ForwardFeaturePlacementStatus.NoSafeConvertSymbol,
            $"no safe ConvertToId found for {intent.Kind} on turn {intent.Turn}; " +
            $"preferred={PreferredConvertSymbol(intent)}, " +
            $"remainingWins=[{RemainingWins(objectives, currentTrialLedger)}], " +
            $"collected=[{Collected(currentTrialLedger)}]"));
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
            var selected = selector.TryCollectSpecific(preferred, collectionTurn: fate.CollectedTurn);
            if (selected.IsValid)
                return ConvertSelection.Ok(selected.Symbol, isCollected: true, fate.CollectedTurn);
        }

        foreach (var fallbackIntent in ConvertFallbackOrder())
        {
            var selected = selector.ChooseAndCollect(fallbackIntent, collectionTurn: fate.CollectedTurn);
            if (selected.IsValid)
                return ConvertSelection.Ok(selected.Symbol, isCollected: true, fate.CollectedTurn);
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

    private static string RemainingWins(
        ForwardObjectives objectives,
        SymbolLedger ledger) =>
        string.Join(",", objectives.WinTargets
            .OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key}:{Math.Max(0, kv.Value - ledger.CollectedCount(kv.Key))}"));

    private static string Collected(SymbolLedger ledger) =>
        string.Join(",", ledger.Collected
            .OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key}:{kv.Value}"));

    private ForwardFeatureSpawnRequest? BuildRequest(
        ForwardFeatureIntent intent,
        (int r, int c) position,
        int convertSymbol,
        Cell?[,]? boardAfterPushRotate,
        IReadOnlyList<ForwardFutureTurn> futureTurns,
        ForwardObjectives objectives,
        SymbolLedger ledger,
        IReadOnlySet<int> sameTurnCollectedConvertSymbols) =>
        intent.Kind switch
        {
            ForwardTimedFeatureKind.Wheel => BuildWheelRequest(
                intent,
                position,
                convertSymbol,
                boardAfterPushRotate,
                futureTurns,
                objectives,
                ledger,
                sameTurnCollectedConvertSymbols),
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
        IReadOnlySet<int> sameTurnCollectedConvertSymbols)
    {
        var wheelStack = intent.ResultingWheelStack!.Value;
        foreach (var wheelSymbol in OrderedWheelSymbols(intent, objectives, sameTurnCollectedConvertSymbols))
        {
            var trialLedger = ledger.Clone();
            var requiredBonus = RequiresBoundedWheelBonus(objectives)
                ? intent.WheelStackValue
                : null;
            var impact = CommitCurrentBoardWheelImpact(
                wheelSymbol,
                wheelStack,
                boardAfterPushRotate,
                futureTurns,
                trialLedger,
                requiredBonus);
            if (!impact.IsValid)
                continue;

            var selfConversion = CommitWheelSelfConversionBonus(
                position,
                convertSymbol,
                wheelSymbol,
                wheelStack,
                futureTurns,
                trialLedger);
            if (!selfConversion.IsValid)
                continue;

            ledger.ReplaceWith(trialLedger);
            return ForwardFeatureSpawnRequest.Wheel(
                position.r,
                position.c,
                convertSymbol,
                wheelSymbol,
                wheelStack);
        }

        return null;
    }

    private IReadOnlyList<int> OrderedWheelSymbols(
        ForwardFeatureIntent intent,
        ForwardObjectives objectives,
        IReadOnlySet<int> blockedSameTurnSymbols)
    {
        if (RequiresBoundedWheelBonus(objectives))
        {
            return objectives.WinSymbols
                .Where(symbol => symbol >= 1 && symbol <= objectives.MaxSymbol && !Settings.IsFeat(symbol))
                .Where(symbol => !blockedSameTurnSymbols.Contains(symbol))
                .Distinct()
                .OrderBy(symbol => symbol == intent.WheelSymbol ? 0 : 1)
                .ThenBy(symbol => symbol)
                .ToArray();
        }

        return objectives.FillSymbols
            .Where(symbol => !objectives.WinTargets.ContainsKey(symbol))
            .Where(symbol => !objectives.NearMissTargets.ContainsKey(symbol))
            .Concat(objectives.NearMissTargets.Keys)
            .Concat(objectives.WinSymbols)
            .Where(symbol => symbol >= 1 && symbol <= objectives.MaxSymbol && !Settings.IsFeat(symbol))
            .Where(symbol => !blockedSameTurnSymbols.Contains(symbol))
            .Distinct()
            .OrderBy(symbol => symbol == intent.WheelSymbol ? 0 : 1)
            .ThenBy(symbol => objectives.WinTargets.ContainsKey(symbol) ? 2 : objectives.NearMissTargets.ContainsKey(symbol) ? 1 : 0)
            .ThenBy(symbol => symbol)
            .ToArray();
    }

    private WheelImpactCommitResult CommitCurrentBoardWheelImpact(
        int symbol,
        int wheelStack,
        Cell?[,]? boardAfterPushRotate,
        IReadOnlyList<ForwardFutureTurn> futureTurns,
        SymbolLedger ledger,
        int? requiredBonus = null)
    {
        if (boardAfterPushRotate == null)
            return WheelImpactCommitResult.Ok();

        var stackAdd = Math.Max(0, wheelStack - 1);
        if (stackAdd == 0)
            return WheelImpactCommitResult.Ok();

        var committedBonus = 0;
        for (var row = 0; row < Settings.ROWS; row++)
        {
            for (var col = 0; col < Settings.COLS; col++)
            {
                var cell = boardAfterPushRotate[row, col];
                if (cell == null || cell.IsFeat || cell.Sym != symbol)
                    continue;

                var fate = _fateAnalyzer.Analyze(row, col, futureTurns);
                if (!fate.IsValid)
                    return WheelImpactCommitResult.Fail(fate.Detail);
                if (!fate.IsCollected)
                    continue;

                var currentStack = Math.Max(1, cell.Stack);
                var stacked = Math.Min(Settings.MAX_COIN_STACK, currentStack + stackAdd);
                var bonus = stacked - currentStack;
                if (bonus <= 0)
                    continue;
                if (requiredBonus.HasValue && committedBonus + bonus > requiredBonus.Value)
                    return WheelImpactCommitResult.Fail("bounded WHEEL would add too much stack bonus");

                var collect = ledger.Collect(symbol, bonus, fate.CollectedTurn);
                if (!collect.IsValid)
                    return WheelImpactCommitResult.Fail(collect.Detail);
                committedBonus += bonus;
            }
        }

        return WheelImpactCommitResult.Ok();
    }

    private WheelImpactCommitResult CommitWheelSelfConversionBonus(
        (int r, int c) position,
        int convertSymbol,
        int wheelSymbol,
        int wheelStack,
        IReadOnlyList<ForwardFutureTurn> futureTurns,
        SymbolLedger ledger)
    {
        if (convertSymbol != wheelSymbol)
            return WheelImpactCommitResult.Ok();

        var stackBonus = Math.Max(0, wheelStack - 1);
        if (stackBonus == 0)
            return WheelImpactCommitResult.Ok();

        var fate = _fateAnalyzer.Analyze(position.r, position.c, futureTurns);
        if (!fate.IsValid)
            return WheelImpactCommitResult.Fail(fate.Detail);
        if (!fate.IsCollected)
            return WheelImpactCommitResult.Ok();

        var collect = ledger.Collect(convertSymbol, stackBonus, fate.CollectedTurn);
        return collect.IsValid
            ? WheelImpactCommitResult.Ok()
            : WheelImpactCommitResult.Fail(collect.Detail);
    }

    private static bool RequiresBoundedWheelBonus(ForwardObjectives objectives) =>
        !HasGuaranteedSafeFiller(objectives);

    private static bool HasGuaranteedSafeFiller(ForwardObjectives objectives) =>
        objectives.FillSymbols.Any(symbol =>
            !objectives.WinTargets.ContainsKey(symbol)
            && !objectives.NearMissTargets.ContainsKey(symbol));

    private WheelImpactCommitResult CommitSameTurnFeatureConversionWheelBonuses(
        IReadOnlyList<ConvertReservation> reservations,
        IReadOnlyList<ForwardFeatureSpawnRequest> requests,
        SymbolLedger ledger)
    {
        foreach (var reservation in reservations.Where(reservation => reservation.IsCollected))
        {
            var bonus = SameTurnFeatureConversionWheelBonus(reservation, requests);
            if (bonus <= 0)
                continue;

            var collect = ledger.Collect(
                reservation.ConvertSymbol,
                bonus,
                reservation.CollectionTurn);
            if (!collect.IsValid)
            {
                return WheelImpactCommitResult.Fail(
                    $"same-turn WHEEL bonus for feature ConvertToId={reservation.ConvertSymbol} " +
                    $"at ({reservation.Row},{reservation.Col}) failed: {collect.Detail}");
            }
        }

        return WheelImpactCommitResult.Ok();
    }

    private static int SameTurnFeatureConversionWheelBonus(
        ConvertReservation reservation,
        IReadOnlyList<ForwardFeatureSpawnRequest> requests)
    {
        var baseStack = BaseConvertedStack(reservation, requests);
        var stack = baseStack;
        foreach (var wheel in requests
                     .Where(request => request.Kind == ForwardFeatureKind.Wheel)
                     .Where(request => request.WheelSymbol == reservation.ConvertSymbol)
                     .Where(request => WheelCanApplyAfterConversion(reservation, request))
                     .OrderBy(PositionOrder))
        {
            stack = Math.Min(
                Settings.MAX_COIN_STACK,
                stack + Math.Max(0, wheel.WheelStack!.Value - 1));
        }

        return Math.Max(0, stack - baseStack);
    }

    private static int BaseConvertedStack(
        ConvertReservation reservation,
        IReadOnlyList<ForwardFeatureSpawnRequest> requests)
    {
        if (reservation.FeatureKind != ForwardFeatureKind.Wheel)
            return 1;

        var ownWheel = requests.FirstOrDefault(request =>
            request.Kind == ForwardFeatureKind.Wheel
            && request.Row == reservation.Row
            && request.Col == reservation.Col
            && request.WheelSymbol == reservation.ConvertSymbol);

        return ownWheel.WheelStack.HasValue
            ? Math.Min(Settings.MAX_COIN_STACK, Math.Max(1, ownWheel.WheelStack.Value))
            : 1;
    }

    private static bool WheelCanApplyAfterConversion(
        ConvertReservation reservation,
        ForwardFeatureSpawnRequest wheel)
    {
        if (reservation.FeatureKind != ForwardFeatureKind.Wheel)
            return true;

        return PositionOrder(wheel) > PositionOrder(reservation);
    }

    private static int PositionOrder(ForwardFeatureSpawnRequest request) =>
        request.Row * Settings.COLS + request.Col;

    private static int PositionOrder(ConvertReservation reservation) =>
        reservation.Row * Settings.COLS + reservation.Col;

    private ForwardSymbolSelector CreateSelector(
        ForwardObjectives objectives,
        SymbolLedger ledger,
        IReadOnlySet<int>? blockedCollectSymbols,
        IReadOnlyList<ForwardFutureTurn> futureTurns) =>
        new(
            ledger,
            objectives.WinTargets,
            objectives.NearMissTargets,
            objectives.FillSymbols,
            objectives.MaxSymbol,
            new Random(_rng.Next()),
            blockedCollectSymbols,
            FutureWheelReservations(objectives, futureTurns));

    private static IReadOnlyDictionary<int, int> FutureWheelReservations(
        ForwardObjectives objectives,
        IReadOnlyList<ForwardFutureTurn> futureTurns) =>
        futureTurns
            .SelectMany(turn => turn.FeatureIntents)
            .Where(intent => intent.Kind == ForwardTimedFeatureKind.Wheel)
            .Where(intent => intent.WheelSymbol.HasValue)
            .Select(intent => intent.WheelSymbol!.Value)
            .Where(symbol => objectives.WinTargets.ContainsKey(symbol))
            .GroupBy(symbol => symbol)
            .ToDictionary(group => group.Key, group => group.Count());

    private bool ValidSymbol(int symbol, ForwardObjectives objectives) =>
        symbol >= 1 && symbol <= objectives.MaxSymbol && !Settings.IsFeat(symbol);

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
            bool isCollected,
            int? collectionTurn,
            ForwardFeatureKind featureKind)
        {
            Row = row;
            Col = col;
            ConvertSymbol = convertSymbol;
            IsCollected = isCollected;
            CollectionTurn = collectionTurn;
            FeatureKind = featureKind;
        }

        internal int Row { get; }
        internal int Col { get; }
        internal int ConvertSymbol { get; }
        internal bool IsCollected { get; }
        internal int? CollectionTurn { get; }
        internal ForwardFeatureKind FeatureKind { get; }
    }

    private readonly struct ConvertSelection
    {
        private ConvertSelection(
            ForwardFeaturePlacementStatus status,
            int convertSymbol,
            bool isCollected,
            int? collectionTurn,
            string detail)
        {
            Status = status;
            ConvertSymbol = convertSymbol;
            IsCollected = isCollected;
            CollectionTurn = collectionTurn;
            Detail = detail;
        }

        internal ForwardFeaturePlacementStatus Status { get; }
        internal int ConvertSymbol { get; }
        internal bool IsCollected { get; }
        internal int? CollectionTurn { get; }
        internal string Detail { get; }
        internal bool IsValid => Status == ForwardFeaturePlacementStatus.Valid;

        internal static ConvertSelection Ok(int convertSymbol, bool isCollected, int? collectionTurn = null) =>
            new(ForwardFeaturePlacementStatus.Valid, convertSymbol, isCollected, collectionTurn, "ok");

        internal static ConvertSelection Fail(ForwardFeaturePlacementStatus status, string detail) =>
            new(status, 0, false, null, detail);
    }

    private readonly struct WheelImpactCommitResult
    {
        private WheelImpactCommitResult(bool isValid, string detail)
        {
            IsValid = isValid;
            Detail = detail;
        }

        internal bool IsValid { get; }
        internal string Detail { get; }

        internal static WheelImpactCommitResult Ok() =>
            new(true, "ok");

        internal static WheelImpactCommitResult Fail(string detail) =>
            new(false, detail);
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
