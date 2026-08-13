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
    private readonly ForwardCellFateAnalyzer _fateAnalyzer;

    internal ForwardNormalIntentPlanner()
    {
        _fateAnalyzer = new ForwardCellFateAnalyzer();
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
        var futureWheelBonus = 0;
        var futureCapacity = futureCapacities.Sum() + futureWheelBonus;
        var safeCollectionCapacity = SafeCollectionCapacity(objectives, symbolLedger);
        if (collectingSlots > totalRequired + safeCollectionCapacity)
        {
            return Fail(
                ForwardNormalIntentStatus.InsufficientCollectionCapacity,
                $"current collecting slots={collectingSlots}, remaining required collections={totalRequired}, safe filler capacity={safeCollectionCapacity}",
                collectingSlots,
                residueSlots);
        }

        // WHEEL stack is symbol-specific. Capacity planning must count physical
        // cells only; ForwardSpawnPlanner applies stack bonus once the symbol is
        // known and the ledger can reject any over-collection.
        var wheelBonus = 0;
        if (totalRequired > collectingSlots + wheelBonus + futureCapacity)
        {
            return Fail(
                ForwardNormalIntentStatus.InsufficientCollectionCapacity,
                $"remaining required collections={totalRequired}, current collecting slots={collectingSlots}, WHEEL bonus={wheelBonus}, future capacity={futureCapacity}",
                collectingSlots,
                residueSlots);
        }

        var requiredNow = Math.Max(0, totalRequired - futureCapacity);
        var desiredUnitsNow = DesiredCurrentCollections(
            totalRequired,
            collectingSlots,
            requiredNow,
            futureCapacities,
            futureWheelBonus,
            safeCollectionCapacity);
        var futureWheelReserved = FutureWheelReservations(objectives, futureTurns).Values.Sum();
        if (futureWheelReserved > 0)
            desiredUnitsNow = Math.Min(desiredUnitsNow, Math.Max(0, totalRequired - futureWheelReserved));
        var desiredNow = Math.Max(0, desiredUnitsNow - wheelBonus);
        var (winProgress, nearProgress) = AllocateDemand(desiredNow, winRemaining, nearRemaining);
        if (futureWheelReserved > 0)
            winProgress = Math.Min(winProgress, Math.Max(0, winRemaining - futureWheelReserved));
        var timing = ConstrainWinProgress(objectives, fates.Fates, winProgress, winRemaining);
        winProgress = timing.WinProgress;
        var safeFillers = collectingSlots - winProgress - nearProgress;
        var intents = BuildIntents(
            winProgress,
            nearProgress,
            safeFillers,
            residueSlots,
            objectives.WinCompletionTurn,
            timing.ExactTargetTurnWinCount);

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

    private IReadOnlyList<int> FutureCollectionCapacities(IReadOnlyList<ForwardFutureTurn> futureTurns)
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

            var collectibleCells = AllPositions()
                .Count(position => _fateAnalyzer.Analyze(position.r, position.c, laterTurns).IsCollected);
            capacities.Add(Math.Min(turn.Shape.PoppedCellCount, collectibleCells));
        }

        return capacities;
    }

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

    private int DesiredCurrentCollections(
        int totalRequired,
        int collectingCapacity,
        int requiredNow,
        IReadOnlyList<int> futureCapacities,
        int futureWheelBonus,
        int safeCollectionCapacity)
    {
        if (totalRequired <= 0 || collectingCapacity <= 0)
            return 0;

        var futureCapacity = futureCapacities.Sum() + futureWheelBonus;
        var futureRequiredReserve = Math.Max(0, futureCapacity - safeCollectionCapacity);
        var maxRequiredNow = Math.Max(0, totalRequired - futureRequiredReserve);
        var minRequiredNow = Math.Max(requiredNow, collectingCapacity - safeCollectionCapacity);

        if (maxRequiredNow < minRequiredNow)
            return Math.Min(collectingCapacity, totalRequired);

        return Math.Clamp(
            Math.Min(collectingCapacity, maxRequiredNow),
            Math.Min(collectingCapacity, minRequiredNow),
            Math.Min(collectingCapacity, totalRequired));
    }

    private int SafeCollectionCapacity(
        ForwardObjectives objectives,
        SymbolLedger ledger)
    {
        var candidates = objectives.FillSymbols
            .Concat(objectives.NearMissTargets.Keys)
            .Where(symbol => !objectives.WinTargets.ContainsKey(symbol))
            .Where(symbol => symbol >= 1 && symbol <= objectives.MaxSymbol)
            .Where(symbol => !Settings.IsFeat(symbol))
            .Distinct();

        var capacity = 0;
        foreach (var symbol in candidates)
        {
            var current = ledger.CollectedCount(symbol);
            var cap = Settings.SymbolFillCap(symbol);
            capacity += Math.Max(0, (cap - 1) - current);
        }

        return capacity;
    }

    private static (int Win, int NearMiss) AllocateDemand(
        int desired,
        int winRemaining,
        int nearRemaining)
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

        return (win, near);
    }

    private static WinProgressTiming ConstrainWinProgress(
        ForwardObjectives objectives,
        IReadOnlyList<ForwardCellFate> fates,
        int requestedWinProgress,
        int winRemaining)
    {
        if (!objectives.WinCompletionTurn.HasValue
            || requestedWinProgress <= 0
            || winRemaining <= 0)
        {
            return new WinProgressTiming(requestedWinProgress, 0);
        }

        var targetTurn = objectives.WinCompletionTurn.Value;
        var legalWinSlots = fates.Count(fate =>
            fate.IsCollected
            && fate.CollectedTurn.HasValue
            && fate.CollectedTurn.Value <= targetTurn);
        var targetTurnSlots = fates.Count(fate =>
            fate.IsCollected
            && fate.CollectedTurn == targetTurn);
        if (legalWinSlots <= 0)
            return new WinProgressTiming(0, 0);

        if (targetTurnSlots > 0
            && requestedWinProgress >= winRemaining
            && legalWinSlots >= winRemaining)
        {
            return new WinProgressTiming(winRemaining, 1);
        }

        var nonFinalProgress = Math.Min(
            requestedWinProgress,
            Math.Min(Math.Max(0, winRemaining - 1), legalWinSlots));

        return new WinProgressTiming(nonFinalProgress, 0);
    }

    private static IReadOnlyList<ForwardNormalSpawnIntent> BuildIntents(
        int winProgress,
        int nearProgress,
        int safeFillers,
        int residue,
        int? winCompletionTurn,
        int exactTargetTurnWinCount)
    {
        var intents = new List<ForwardNormalSpawnIntent>(winProgress + nearProgress + safeFillers + residue);
        var normalWinProgress = Math.Max(0, winProgress - exactTargetTurnWinCount);
        for (var i = 0; i < normalWinProgress; i++)
        {
            intents.Add(new ForwardNormalSpawnIntent(
                ForwardSymbolIntent.MustProgressWin,
                maxCollectionTurn: winCompletionTurn));
        }

        for (var i = 0; i < exactTargetTurnWinCount; i++)
        {
            intents.Add(new ForwardNormalSpawnIntent(
                ForwardSymbolIntent.MustProgressWin,
                minCollectionTurn: winCompletionTurn,
                maxCollectionTurn: winCompletionTurn));
        }

        for (var i = 0; i < nearProgress; i++)
            intents.Add(new ForwardNormalSpawnIntent(ForwardSymbolIntent.PreferNearMiss));
        for (var i = 0; i < safeFillers; i++)
            intents.Add(ForwardNormalSpawnIntent.SafeFiller());
        for (var i = 0; i < residue; i++)
            intents.Add(new ForwardNormalSpawnIntent(ForwardSymbolIntent.ResidueOnly));

        return intents;
    }

    private readonly struct WinProgressTiming
    {
        internal WinProgressTiming(int winProgress, int exactTargetTurnWinCount)
        {
            WinProgress = winProgress;
            ExactTargetTurnWinCount = exactTargetTurnWinCount;
        }

        internal int WinProgress { get; }
        internal int ExactTargetTurnWinCount { get; }
    }

    private IEnumerable<(int r, int c)> AllPositions()
    {
        for (var row = 0; row < Settings.ROWS; row++)
        {
            for (var col = 0; col < Settings.COLS; col++)
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
