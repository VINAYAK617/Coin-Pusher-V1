namespace CoinPusherEngine;

internal enum ForwardTicketEnvelopeStatus
{
    Valid,
    MissingObjectives,
    MissingFramePlan,
    InvalidFrameCount,
    InvalidTurnSequence,
    InvalidTurnShape,
    InvalidTotalTurns,
    FeatureOnFinalTurn,
    ExtraGoTurnBeforeEarned,
    ExtraGoOverAward,
    ExtraGoFinalMismatch,
    CellFateInvalid,
    InsufficientCollectionCapacity,
}

internal sealed class ForwardTicketEnvelopeResult
{
    internal ForwardTicketEnvelopeResult(
        ForwardTicketEnvelopeStatus status,
        string detail,
        int requiredCollections,
        int availableCollectionSlots,
        int startingBoardCollectionSlots,
        int spawnCollectionSlots,
        int extraGoCount)
    {
        Status = status;
        Detail = detail;
        RequiredCollections = requiredCollections;
        AvailableCollectionSlots = availableCollectionSlots;
        StartingBoardCollectionSlots = startingBoardCollectionSlots;
        SpawnCollectionSlots = spawnCollectionSlots;
        ExtraGoCount = extraGoCount;
    }

    internal ForwardTicketEnvelopeStatus Status { get; }
    internal string Detail { get; }
    internal int RequiredCollections { get; }
    internal int AvailableCollectionSlots { get; }
    internal int StartingBoardCollectionSlots { get; }
    internal int SpawnCollectionSlots { get; }
    internal int ExtraGoCount { get; }
    internal bool IsValid => Status == ForwardTicketEnvelopeStatus.Valid;
}

internal sealed class ForwardTicketEnvelopeValidator
{
    private readonly ICustomProfileSettings _settings;
    private readonly ForwardCellFateAnalyzer _fateAnalyzer;

    internal ForwardTicketEnvelopeValidator(ICustomProfileSettings settings)
    {
        _settings = settings;
        _fateAnalyzer = new ForwardCellFateAnalyzer(settings);
    }

    internal ForwardTicketEnvelopeResult Validate(
        ForwardObjectives? objectives,
        ForwardTurnFramePlan? framePlan)
    {
        if (objectives == null)
            return Fail(ForwardTicketEnvelopeStatus.MissingObjectives, "forward objectives are missing");
        if (framePlan == null)
            return Fail(ForwardTicketEnvelopeStatus.MissingFramePlan, "turn frame plan is missing");

        var shapeCheck = ValidateFrameSequenceAndShapes(framePlan);
        if (shapeCheck != null) return shapeCheck;

        var extraGoCount = framePlan.Frames.Sum(frame => frame.FeatureIntents.Count(intent => intent.Kind == ForwardTimedFeatureKind.ExtraGo));
        if (framePlan.TotalTurns != _settings.BASE_SPINS + extraGoCount)
        {
            return Fail(
                ForwardTicketEnvelopeStatus.InvalidTotalTurns,
                $"TotalTurns={framePlan.TotalTurns}, BASE_SPINS={_settings.BASE_SPINS}, EXTRA_GO count={extraGoCount}",
                extraGoCount: extraGoCount);
        }

        var timeline = ValidateExtraGoTimeline(framePlan, extraGoCount);
        if (timeline != null) return timeline;

        var capacity = CountCollectionCapacity(framePlan);
        if (!capacity.IsValid) return capacity.Result!;

        var required = objectives.WinTargets.Values.Sum()
            + objectives.NearMissTargets.Values.Sum();
        var noSafeFillerAllowance = NoSafeFillerCapacityAllowance(objectives);
        var available = capacity.StartingBoardSlots
            + capacity.SpawnSlots
            + noSafeFillerAllowance
            + WheelWinBonus(objectives, framePlan);
        if (required > available)
        {
            return new ForwardTicketEnvelopeResult(
                ForwardTicketEnvelopeStatus.InsufficientCollectionCapacity,
                $"required collections={required}, physical collectible slots={available}",
                required,
                available,
                capacity.StartingBoardSlots,
                capacity.SpawnSlots,
                extraGoCount);
        }

        return new ForwardTicketEnvelopeResult(
            ForwardTicketEnvelopeStatus.Valid,
            "ok",
            required,
            available,
            capacity.StartingBoardSlots,
            capacity.SpawnSlots,
            extraGoCount);
    }

    private int NoSafeFillerCapacityAllowance(ForwardObjectives objectives) =>
        HasGuaranteedSafeFiller(objectives) || objectives.NearMissTargets.Count > 0
            ? 0
            : _settings.COLS;

    private int WheelWinBonus(
        ForwardObjectives objectives,
        ForwardTurnFramePlan framePlan)
    {
        var bonus = 0;
        foreach (var intent in framePlan.Frames
                     .SelectMany(frame => frame.FeatureIntents)
                     .Where(intent => intent.Kind == ForwardTimedFeatureKind.Wheel)
                     .Where(intent => intent.WheelSymbol.HasValue && intent.ResultingWheelStack.HasValue))
        {
            var symbol = intent.WheelSymbol!.Value;
            if (!objectives.WinTargets.TryGetValue(symbol, out var target))
                continue;

            var stack = Math.Min(_settings.MAX_COIN_STACK, intent.ResultingWheelStack!.Value);
            if (stack <= 1)
                continue;

            var zone = Math.Max(0, Math.Min(target / stack, _settings.COLS - 1) - 1);
            bonus += zone * (stack - 1);
        }

        return bonus;
    }

    private static bool HasGuaranteedSafeFiller(ForwardObjectives objectives) =>
        objectives.FillSymbols.Any(symbol =>
            !objectives.WinTargets.ContainsKey(symbol)
            && !objectives.NearMissTargets.ContainsKey(symbol));

    private ForwardTicketEnvelopeResult? ValidateFrameSequenceAndShapes(ForwardTurnFramePlan framePlan)
    {
        if (framePlan.TotalTurns <= 0)
            return Fail(ForwardTicketEnvelopeStatus.InvalidTotalTurns, $"TotalTurns={framePlan.TotalTurns} must be positive");
        if (framePlan.TotalTurns < _settings.BASE_SPINS || framePlan.TotalTurns > _settings.MAX_SPINS)
        {
            return Fail(
                ForwardTicketEnvelopeStatus.InvalidTotalTurns,
                $"TotalTurns={framePlan.TotalTurns} outside {_settings.BASE_SPINS}..{_settings.MAX_SPINS}");
        }
        if (framePlan.Frames.Count != framePlan.TotalTurns)
        {
            return Fail(
                ForwardTicketEnvelopeStatus.InvalidFrameCount,
                $"frame count={framePlan.Frames.Count}, TotalTurns={framePlan.TotalTurns}");
        }

        var seen = new HashSet<int>();
        foreach (var frame in framePlan.Frames)
        {
            if (frame.Turn <= 0 || frame.Turn > framePlan.TotalTurns)
            {
                return Fail(
                    ForwardTicketEnvelopeStatus.InvalidTurnSequence,
                    $"frame turn {frame.Turn} outside 1..{framePlan.TotalTurns}");
            }

            if (!seen.Add(frame.Turn))
                return Fail(ForwardTicketEnvelopeStatus.InvalidTurnSequence, $"frame turn {frame.Turn} appears more than once");

            var shape = ForwardTurnShape.Validate(frame.Shape?.Pushers, _settings);
            if (!shape.IsValid)
            {
                return Fail(
                    ForwardTicketEnvelopeStatus.InvalidTurnShape,
                    $"turn {frame.Turn}: {shape.Detail}");
            }

            if (frame.Turn == framePlan.TotalTurns && frame.FeatureIntents.Count > 0)
            {
                return Fail(
                    ForwardTicketEnvelopeStatus.FeatureOnFinalTurn,
                    $"final turn {frame.Turn} contains {frame.FeatureIntents.Count} feature intent(s)");
            }
        }

        for (var turn = 1; turn <= framePlan.TotalTurns; turn++)
        {
            if (!seen.Contains(turn))
                return Fail(ForwardTicketEnvelopeStatus.InvalidTurnSequence, $"frame turn {turn} is missing");
        }

        return null;
    }

    private ForwardTicketEnvelopeResult? ValidateExtraGoTimeline(
        ForwardTurnFramePlan framePlan,
        int extraGoCount)
    {
        var earnedTurns = _settings.BASE_SPINS;
        foreach (var frame in framePlan.Frames.OrderBy(frame => frame.Turn))
        {
            if (frame.Turn > earnedTurns)
            {
                return Fail(
                    ForwardTicketEnvelopeStatus.ExtraGoTurnBeforeEarned,
                    $"turn {frame.Turn} starts before it is earned; earned turns before turn={earnedTurns}",
                    extraGoCount: extraGoCount);
            }

            var extrasThisTurn = frame.FeatureIntents.Count(intent => intent.Kind == ForwardTimedFeatureKind.ExtraGo);
            if (extrasThisTurn == 0) continue;

            var remainingFutureTurns = framePlan.TotalTurns - frame.Turn;
            if (extrasThisTurn > remainingFutureTurns)
            {
                return Fail(
                    ForwardTicketEnvelopeStatus.ExtraGoOverAward,
                    $"turn {frame.Turn} awards {extrasThisTurn} EXTRA_GO but only {remainingFutureTurns} future turn(s) remain",
                    extraGoCount: extraGoCount);
            }

            earnedTurns += extrasThisTurn;
            if (earnedTurns > framePlan.TotalTurns)
            {
                return Fail(
                    ForwardTicketEnvelopeStatus.ExtraGoOverAward,
                    $"earned turns would become {earnedTurns}, above planned total {framePlan.TotalTurns}",
                    extraGoCount: extraGoCount);
            }
        }

        if (earnedTurns != framePlan.TotalTurns)
        {
            return Fail(
                ForwardTicketEnvelopeStatus.ExtraGoFinalMismatch,
                $"earned turns={earnedTurns}, TotalTurns={framePlan.TotalTurns}, EXTRA_GO count={extraGoCount}",
                extraGoCount: extraGoCount);
        }

        return null;
    }

    private CapacityResult CountCollectionCapacity(ForwardTurnFramePlan framePlan)
    {
        var starting = 0;
        foreach (var position in AllPositions())
        {
            var fate = _fateAnalyzer.Analyze(position.r, position.c, framePlan.FutureTurnsAfter(0));
            if (!fate.IsValid)
            {
                return CapacityResult.Fail(Fail(
                    ForwardTicketEnvelopeStatus.CellFateInvalid,
                    $"starting cell ({position.r},{position.c}): {fate.Detail}"));
            }

            if (fate.IsCollected)
                starting++;
        }

        var spawned = 0;
        foreach (var frame in framePlan.Frames.OrderBy(frame => frame.Turn))
        {
            var emptyPositions = EmptyPositionsAfterPushRotate(frame.Shape);
            foreach (var position in emptyPositions)
            {
                var fate = _fateAnalyzer.Analyze(position.r, position.c, framePlan.FutureTurnsAfter(frame.Turn));
                if (!fate.IsValid)
                {
                    return CapacityResult.Fail(Fail(
                        ForwardTicketEnvelopeStatus.CellFateInvalid,
                        $"turn {frame.Turn} spawn cell ({position.r},{position.c}): {fate.Detail}"));
                }

                if (fate.IsCollected)
                    spawned++;
            }
        }

        return CapacityResult.Ok(starting, spawned);
    }

    private IReadOnlyList<(int r, int c)> EmptyPositionsAfterPushRotate(ForwardTurnShape shape)
    {
        var board = new Cell?[_settings.ROWS, _settings.COLS];
        for (var row = 0; row < _settings.ROWS; row++)
        {
            for (var col = 0; col < _settings.COLS; col++)
                board[row, col] = Grid.Norm(1);
        }

        return new ForwardBoardState(board, _settings)
            .PreviewAfterPushRotate(shape)
            .EmptyPositions;
    }

    private IEnumerable<(int r, int c)> AllPositions()
    {
        for (var row = 0; row < _settings.ROWS; row++)
        {
            for (var col = 0; col < _settings.COLS; col++)
                yield return (row, col);
        }
    }

    private static ForwardTicketEnvelopeResult Fail(
        ForwardTicketEnvelopeStatus status,
        string detail,
        int requiredCollections = 0,
        int availableCollectionSlots = 0,
        int startingBoardCollectionSlots = 0,
        int spawnCollectionSlots = 0,
        int extraGoCount = 0) =>
        new(
            status,
            detail,
            requiredCollections,
            availableCollectionSlots,
            startingBoardCollectionSlots,
            spawnCollectionSlots,
            extraGoCount);

    private sealed class CapacityResult
    {
        private CapacityResult(
            int startingBoardSlots,
            int spawnSlots,
            ForwardTicketEnvelopeResult? result)
        {
            StartingBoardSlots = startingBoardSlots;
            SpawnSlots = spawnSlots;
            Result = result;
        }

        internal int StartingBoardSlots { get; }
        internal int SpawnSlots { get; }
        internal ForwardTicketEnvelopeResult? Result { get; }
        internal bool IsValid => Result == null;

        internal static CapacityResult Ok(int startingBoardSlots, int spawnSlots) =>
            new(startingBoardSlots, spawnSlots, null);

        internal static CapacityResult Fail(ForwardTicketEnvelopeResult result) =>
            new(0, 0, result);
    }
}
