namespace CoinPusherEngine;

internal enum ForwardNormalIntentStatus
{
    Valid,
    MissingObjectives,
    MissingPositions,
    MissingFutureTurns,
    MissingLedger,
    InvalidTurn,
    CellFateInvalid,
    InsufficientCollectionCapacity,
}

internal sealed class ForwardNormalIntentResult
{
    internal ForwardNormalIntentResult(
        ForwardNormalIntentStatus status,
        string detail,
        IReadOnlyList<ForwardNormalSpawnIntent> intents,
        int collectingSlots,
        int residueSlots,
        int winProgressCount,
        int nearMissProgressCount,
        int safeFillerCount,
        int residueCount)
    {
        Status = status;
        Detail = detail;
        Intents = intents;
        CollectingSlots = collectingSlots;
        ResidueSlots = residueSlots;
        WinProgressCount = winProgressCount;
        NearMissProgressCount = nearMissProgressCount;
        SafeFillerCount = safeFillerCount;
        ResidueCount = residueCount;
    }

    internal ForwardNormalIntentStatus Status { get; }
    internal string Detail { get; }
    internal IReadOnlyList<ForwardNormalSpawnIntent> Intents { get; }
    internal int CollectingSlots { get; }
    internal int ResidueSlots { get; }
    internal int WinProgressCount { get; }
    internal int NearMissProgressCount { get; }
    internal int SafeFillerCount { get; }
    internal int ResidueCount { get; }
    internal bool IsValid => Status == ForwardNormalIntentStatus.Valid;
}

internal sealed class ForwardNormalIntentPlanner
{
    private readonly ICustomProfileSettings _settings;
    private readonly ForwardCellFateAnalyzer _fateAnalyzer;
    private readonly Random _rng;

    internal ForwardNormalIntentPlanner(ICustomProfileSettings settings, int seed = 0)
    {
        _settings = settings;
        _fateAnalyzer = new ForwardCellFateAnalyzer(settings);
        _rng = new Random(seed);
    }

    internal ForwardNormalIntentResult Plan(
        int turn,
        ForwardObjectives? objectives,
        IReadOnlyList<(int r, int c)>? availablePositions,
        IReadOnlyList<ForwardFutureTurn>? futureTurns,
        SymbolLedger? symbolLedger,
        IReadOnlyList<ForwardWheelImpact>? wheelImpacts = null)
    {
        if (turn <= 0)
            return Fail(ForwardNormalIntentStatus.InvalidTurn, $"turn={turn} must be positive");
        if (objectives == null)
            return Fail(ForwardNormalIntentStatus.MissingObjectives, "forward objectives are missing");
        if (availablePositions == null)
            return Fail(ForwardNormalIntentStatus.MissingPositions, "available positions are missing");
        if (futureTurns == null)
            return Fail(ForwardNormalIntentStatus.MissingFutureTurns, "future turns are missing");
        if (symbolLedger == null)
            return Fail(ForwardNormalIntentStatus.MissingLedger, "symbol ledger is missing");

        var fates = AnalyzePositions(availablePositions, futureTurns);
        if (!fates.IsValid) return fates.Result!;

        var collectingSlots = fates.Fates.Count(fate => fate.IsCollected);
        var residueSlots = availablePositions.Count - collectingSlots;
        var winRemaining = Remaining(objectives.WinTargets, symbolLedger);
        var nearRemaining = Remaining(objectives.NearMissTargets, symbolLedger);
        var totalRequired = winRemaining + nearRemaining;
        var futureCapacities = FutureCollectionCapacities(futureTurns);
        var futureCapacity = futureCapacities.Sum();
        var winningCompletionTurn = objectives.WinningRoundPlan?.WinningCompletionTurn;
        var currentWinSlots = fates.Fates.Count(fate =>
            fate.IsCollected
            && (!winningCompletionTurn.HasValue || fate.CollectedTurn <= winningCompletionTurn));
        var futureWinCapacity = FutureCollectionCapacities(futureTurns, winningCompletionTurn).Sum();
        var futureAnchorReserve = futureTurns.Sum(turn =>
            turn.FeatureIntents.Count(intent => intent.Kind == ForwardTimedFeatureKind.Wheel));

        var wheelBonus = WheelBonus(objectives, wheelImpacts, collectingSlots);
        var futureWheelBonus = FutureWheelBonus(objectives, futureTurns);
        var futureWinWheelBonus = FutureWinWheelBonus(objectives, futureTurns, winningCompletionTurn);
        if (winRemaining > currentWinSlots + wheelBonus + futureWinCapacity + futureWinWheelBonus)
        {
            return Fail(
                ForwardNormalIntentStatus.InsufficientCollectionCapacity,
                $"remaining winning collections={winRemaining}, current deadline-eligible slots={currentWinSlots}, " +
                $"current WHEEL bonus={wheelBonus}, future deadline-eligible capacity={futureWinCapacity}, " +
                $"future guaranteed WHEEL bonus={futureWinWheelBonus}, completion turn={winningCompletionTurn?.ToString() ?? "none"}",
                collectingSlots,
                residueSlots);
        }
        if (totalRequired > collectingSlots + wheelBonus + futureCapacity + futureWheelBonus)
        {
            return Fail(
                ForwardNormalIntentStatus.InsufficientCollectionCapacity,
                $"remaining required collections={totalRequired}, current collecting slots={collectingSlots}, " +
                $"current WHEEL bonus={wheelBonus}, future capacity={futureCapacity}, future WHEEL bonus={futureWheelBonus}",
                collectingSlots,
                residueSlots);
        }

        var guaranteedFutureCapacity = Math.Max(0, futureCapacity - futureAnchorReserve);
        var pacingReserve = futureTurns.Count > 0
            ? Math.Max(0, _settings.WinLateMinTail)
            : 0;
        var requiredNow = Math.Max(
            0,
            totalRequired - guaranteedFutureCapacity - futureWheelBonus - wheelBonus + pacingReserve);
        var requiredWinNow = Math.Max(
            0,
            winRemaining - futureWinCapacity - futureWinWheelBonus - wheelBonus);
        var hasPureFiller = objectives.FillSymbols.Any(symbol =>
            !objectives.WinTargets.ContainsKey(symbol)
            && !objectives.NearMissTargets.ContainsKey(symbol));
        var hasBoardFeatureDependency = (wheelImpacts?.Count ?? 0) > 0
            || futureTurns.Any(future => future.FeatureIntents.Any(intent => intent.IsBoardFeature));
        var physicalCapacity = collectingSlots + futureCapacity + wheelBonus + futureWheelBonus;
        var allowPacing = hasPureFiller
            && !hasBoardFeatureDependency
            && physicalCapacity >= totalRequired * 2;
        var desiredNow = DesiredCurrentCollections(
            totalRequired,
            collectingSlots,
            requiredNow,
            futureCapacities,
            allowPacing);
        desiredNow = Math.Max(desiredNow, Math.Min(currentWinSlots, requiredWinNow));
        var (winProgress, nearProgress) = AllocateDemand(
            desiredNow,
            winRemaining,
            nearRemaining,
            requiredWinNow);
        if (winProgress > currentWinSlots)
        {
            var displaced = winProgress - currentWinSlots;
            winProgress = currentWinSlots;
            nearProgress += Math.Min(displaced, Math.Max(0, nearRemaining - nearProgress));
        }
        var safeFillers = collectingSlots - winProgress - nearProgress;
        var intents = BuildIntents(
            winProgress,
            nearProgress,
            safeFillers,
            residueSlots,
            winningCompletionTurn);

        return new ForwardNormalIntentResult(
            ForwardNormalIntentStatus.Valid,
            "ok",
            intents,
            collectingSlots,
            residueSlots,
            winProgress,
            nearProgress,
            safeFillers,
            residueSlots);
    }

    private FateAnalysisResult AnalyzePositions(
        IReadOnlyList<(int r, int c)> positions,
        IReadOnlyList<ForwardFutureTurn> futureTurns)
    {
        var fates = new List<ForwardCellFate>(positions.Count);
        foreach (var position in positions)
        {
            var fate = _fateAnalyzer.Analyze(position.r, position.c, futureTurns);
            if (!fate.IsValid)
            {
                return FateAnalysisResult.Fail(Fail(
                    ForwardNormalIntentStatus.CellFateInvalid,
                    $"cell ({position.r},{position.c}) fate invalid: {fate.Detail}"));
            }

            fates.Add(fate);
        }

        return FateAnalysisResult.Ok(fates);
    }

    private static int Remaining(
        IReadOnlyDictionary<int, int> targets,
        SymbolLedger ledger) =>
        targets.Sum(kv => Math.Max(0, kv.Value - ledger.CollectedCount(kv.Key)));

    private IReadOnlyList<int> FutureCollectionCapacities(
        IReadOnlyList<ForwardFutureTurn> futureTurns,
        int? latestCollectionTurn = null)
    {
        var capacities = new List<int>(futureTurns.Count);
        for (var index = 0; index < futureTurns.Count; index++)
        {
            var turn = futureTurns[index];
            var laterTurns = futureTurns
                .Skip(index + 1)
                .ToArray();
            if (laterTurns.Length == 0)
            {
                capacities.Add(0);
                continue;
            }

            var collectibleSpawns = EmptyPositionsAfterPushRotate(turn.Shape)
                .Select(position => _fateAnalyzer.Analyze(position.r, position.c, laterTurns))
                .Count(fate => fate.IsCollected
                    && (!latestCollectionTurn.HasValue || fate.CollectedTurn <= latestCollectionTurn));
            capacities.Add(collectibleSpawns);
        }

        return capacities;
    }

    private IReadOnlyList<(int r, int c)> EmptyPositionsAfterPushRotate(ForwardTurnShape shape)
    {
        var board = new Cell?[_settings.ROWS, _settings.COLS];
        for (var row = 0; row < _settings.ROWS; row++)
        {
            for (var col = 0; col < _settings.COLS; col++)
                board[row, col] = Grid.Norm(_settings.F_COIN);
        }

        return new ForwardBoardState(board, _settings)
            .PreviewAfterPushRotate(shape)
            .EmptyPositions;
    }

    private int WheelBonus(
        ForwardObjectives objectives,
        IReadOnlyList<ForwardWheelImpact>? wheelImpacts,
        int collectingSlots)
    {
        if (wheelImpacts == null || wheelImpacts.Count == 0 || collectingSlots <= 0)
            return 0;

        var remainingSlots = collectingSlots;
        var bonus = 0;
        foreach (var wheel in wheelImpacts
                     .Where(wheel => objectives.WinTargets.ContainsKey(wheel.Symbol))
                     .OrderByDescending(wheel => wheel.StackAdd))
        {
            if (remainingSlots <= 0)
                break;

            var usableCells = Math.Min(remainingSlots, _settings.COLS - 2);
            if (usableCells <= 0)
                continue;

            bonus += usableCells * wheel.StackAdd;
            remainingSlots -= usableCells;
        }

        return bonus;
    }

    private int FutureWheelBonus(
        ForwardObjectives objectives,
        IReadOnlyList<ForwardFutureTurn> futureTurns)
    {
        _ = objectives;
        return futureTurns
            .SelectMany(turn => turn.FeatureIntents)
            .Where(intent => intent.Kind == ForwardTimedFeatureKind.Wheel)
            .Where(intent => intent.IsCapacityRequiredWheel)
            .Sum(intent => Math.Max(0, intent.PlannedWheelCollectionBonus));
    }

    private static int FutureWinWheelBonus(
        ForwardObjectives objectives,
        IReadOnlyList<ForwardFutureTurn> futureTurns,
        int? winningCompletionTurn)
    {
        return futureTurns
            .SelectMany(turn => turn.FeatureIntents)
            .Where(intent => intent.Kind == ForwardTimedFeatureKind.Wheel)
            .Where(intent => intent.IsCapacityRequiredWheel)
            .Where(intent => intent.WheelSymbol.HasValue
                && objectives.WinTargets.ContainsKey(intent.WheelSymbol.Value))
            .Where(intent => !winningCompletionTurn.HasValue || intent.Turn < winningCompletionTurn.Value)
            .Sum(intent => Math.Max(0, intent.PlannedWheelCollectionBonus));
    }

    private int DesiredCurrentCollections(
        int totalRequired,
        int collectingCapacity,
        int requiredNow,
        IReadOnlyList<int> futureCapacities,
        bool allowPacing)
    {
        if (totalRequired <= 0 || collectingCapacity <= 0)
            return 0;

        var maximum = Math.Min(collectingCapacity, totalRequired);
        var minimum = Math.Min(maximum, Math.Max(0, requiredNow));
        if (!allowPacing)
            return maximum;
        if (minimum >= maximum || futureCapacities.All(capacity => capacity <= 0))
            return maximum;
        if (futureCapacities.Count <= Math.Max(0, _settings.WinLateTailSpins))
            return maximum;

        var progressFloor = Math.Min(maximum, Math.Max(minimum, 1));
        if (_rng.NextDouble() >= _settings.PWinLateCompletion)
            return _rng.Next(progressFloor, maximum + 1);

        var lateFraction = Math.Max(0.0, Math.Min(1.0, _settings.WinLateTailFraction));
        var reserved = Math.Min(
            Math.Max(0, _settings.MaxDeferredCollectionsPerTurn),
            (int)Math.Ceiling((maximum - minimum) * lateFraction));
        var lateLower = Math.Max(progressFloor, maximum - reserved);
        return _rng.Next(lateLower, maximum + 1);
    }

    private static (int Win, int NearMiss) AllocateDemand(
        int desired,
        int winRemaining,
        int nearRemaining,
        int minimumWin)
    {
        if (desired <= 0 || winRemaining + nearRemaining <= 0)
            return (0, 0);
        if (winRemaining <= 0)
            return (0, Math.Min(desired, nearRemaining));
        if (nearRemaining <= 0)
            return (Math.Min(desired, winRemaining), 0);

        var total = winRemaining + nearRemaining;
        var near = Math.Min(
            nearRemaining,
            Math.Max(1, (int)Math.Round(desired * (nearRemaining / (double)total), MidpointRounding.AwayFromZero)));
        var win = Math.Min(winRemaining, Math.Max(0, desired - near));
        var spare = desired - win - near;
        if (spare > 0)
        {
            var winAdd = Math.Min(spare, winRemaining - win);
            win += winAdd;
            spare -= winAdd;
        }

        if (spare > 0)
            near += Math.Min(spare, nearRemaining - near);

        var requiredWin = Math.Min(Math.Min(desired, winRemaining), Math.Max(0, minimumWin));
        if (win < requiredWin)
        {
            var transfer = Math.Min(requiredWin - win, near);
            win += transfer;
            near -= transfer;
        }

        return (win, near);
    }

    private static IReadOnlyList<ForwardNormalSpawnIntent> BuildIntents(
        int winProgress,
        int nearProgress,
        int safeFillers,
        int residue,
        int? winningCompletionTurn)
    {
        var intents = new List<ForwardNormalSpawnIntent>(winProgress + nearProgress + safeFillers + residue);
        for (var i = 0; i < winProgress; i++)
            intents.Add(new ForwardNormalSpawnIntent(
                ForwardSymbolIntent.MustProgressWin,
                latestCollectionTurn: winningCompletionTurn));
        for (var i = 0; i < nearProgress; i++)
            intents.Add(new ForwardNormalSpawnIntent(ForwardSymbolIntent.PreferNearMiss));
        for (var i = 0; i < safeFillers; i++)
            intents.Add(ForwardNormalSpawnIntent.SafeFiller());
        for (var i = 0; i < residue; i++)
            intents.Add(new ForwardNormalSpawnIntent(ForwardSymbolIntent.ResidueOnly));

        return intents;
    }

    private IEnumerable<(int r, int c)> AllPositions()
    {
        for (var row = 0; row < _settings.ROWS; row++)
        {
            for (var col = 0; col < _settings.COLS; col++)
                yield return (row, col);
        }
    }

    private static ForwardNormalIntentResult Fail(
        ForwardNormalIntentStatus status,
        string detail,
        int collectingSlots = 0,
        int residueSlots = 0) =>
        new(
            status,
            detail,
            Array.Empty<ForwardNormalSpawnIntent>(),
            collectingSlots,
            residueSlots,
            0,
            0,
            0,
            0);

    private sealed class FateAnalysisResult
    {
        private FateAnalysisResult(
            IReadOnlyList<ForwardCellFate> fates,
            ForwardNormalIntentResult? result)
        {
            Fates = fates;
            Result = result;
        }

        internal IReadOnlyList<ForwardCellFate> Fates { get; }
        internal ForwardNormalIntentResult? Result { get; }
        internal bool IsValid => Result == null;

        internal static FateAnalysisResult Ok(IReadOnlyList<ForwardCellFate> fates) =>
            new(fates, null);

        internal static FateAnalysisResult Fail(ForwardNormalIntentResult result) =>
            new(Array.Empty<ForwardCellFate>(), result);
    }
}
