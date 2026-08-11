namespace CoinPusherEngine;

internal enum ForwardTurnCycleStatus
{
    Valid,
    MissingFrame,
    MissingBoardState,
    MissingLedgers,
    BoardHasUnfiredFeatureCells,
    TrialRealizationFailed,
    TrialFeatureFireFailed,
    TrialFeatureFireAuditFailed,
    RealizationFailed,
    FeatureFireFailed,
    FeatureFireAuditFailed,
}

internal sealed class ForwardTurnCycleResult
{
    internal ForwardTurnCycleResult(
        ForwardTurnCycleStatus status,
        string detail,
        ForwardTurnRealizationResult? realization,
        ForwardFeatureFireIntegrationResult? featureFire)
    {
        Status = status;
        Detail = detail;
        Realization = realization;
        FeatureFire = featureFire;
    }

    internal ForwardTurnCycleStatus Status { get; }
    internal string Detail { get; }
    internal ForwardTurnRealizationResult? Realization { get; }
    internal ForwardFeatureFireIntegrationResult? FeatureFire { get; }
    internal bool IsValid => Status == ForwardTurnCycleStatus.Valid;
}

internal sealed class ForwardTurnCycleExecutor
{
    private readonly Settings _settings;
    private readonly int _seed;

    internal ForwardTurnCycleExecutor(Settings settings, int seed)
    {
        _settings = settings;
        _seed = seed;
    }

    internal ForwardTurnCycleResult ExecuteAndAdvance(
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
            return Fail(ForwardTurnCycleStatus.MissingFrame, "turn frame is missing");
        if (boardState == null)
            return Fail(ForwardTurnCycleStatus.MissingBoardState, "board state is missing");
        if (symbolLedger == null || extraSpinLedger == null || prizeUpgradeLedger == null)
            return Fail(ForwardTurnCycleStatus.MissingLedgers, "one or more ledgers are missing");

        var startBoardCheck = ValidateNoFeatureCells(boardState.Snapshot());
        if (startBoardCheck != null)
            return Fail(ForwardTurnCycleStatus.BoardHasUnfiredFeatureCells, startBoardCheck);

        var trialBoard = new ForwardBoardState(boardState.Snapshot(), _settings);
        var trialSymbolLedger = symbolLedger.Clone();
        var trialExtraLedger = extraSpinLedger.Clone();
        var trialPrizeLedger = prizeUpgradeLedger.Clone();

        var trialRealization = CreateRealizer(frame.Turn).RealizeAndAdvance(
            frame,
            plannedTotalTurns,
            trialBoard,
            objectives,
            futureTurns,
            remainingFeatureCapacity,
            trialSymbolLedger,
            trialExtraLedger,
            trialPrizeLedger);
        if (!trialRealization.IsValid)
        {
            return Fail(
                ForwardTurnCycleStatus.TrialRealizationFailed,
                $"{trialRealization.Status}: {trialRealization.Detail}",
                trialRealization);
        }

        var trialFire = new ForwardFeatureFireIntegrator(_settings).FireCurrentBoard(trialBoard);
        if (!trialFire.IsValid)
        {
            return Fail(
                ForwardTurnCycleStatus.TrialFeatureFireFailed,
                $"{trialFire.Status}: {trialFire.Detail}",
                trialRealization,
                trialFire);
        }

        var trialAudit = AuditFeatureFire(trialRealization.Spawns, trialFire.Events);
        if (trialAudit != null)
        {
            return Fail(
                ForwardTurnCycleStatus.TrialFeatureFireAuditFailed,
                trialAudit,
                trialRealization,
                trialFire);
        }

        var realization = CreateRealizer(frame.Turn).RealizeAndAdvance(
            frame,
            plannedTotalTurns,
            boardState,
            objectives,
            futureTurns,
            remainingFeatureCapacity,
            symbolLedger,
            extraSpinLedger,
            prizeUpgradeLedger);
        if (!realization.IsValid)
        {
            return Fail(
                ForwardTurnCycleStatus.RealizationFailed,
                $"{realization.Status}: {realization.Detail}",
                realization);
        }

        var featureFire = new ForwardFeatureFireIntegrator(_settings).FireCurrentBoard(boardState);
        if (!featureFire.IsValid)
        {
            return Fail(
                ForwardTurnCycleStatus.FeatureFireFailed,
                $"{featureFire.Status}: {featureFire.Detail}",
                realization,
                featureFire);
        }

        var audit = AuditFeatureFire(realization.Spawns, featureFire.Events);
        if (audit != null)
        {
            return Fail(
                ForwardTurnCycleStatus.FeatureFireAuditFailed,
                audit,
                realization,
                featureFire);
        }

        return new ForwardTurnCycleResult(
            ForwardTurnCycleStatus.Valid,
            "ok",
            realization,
            featureFire);
    }

    internal string? AuditFeatureFire(
        IReadOnlyList<ForwardSpawn> spawns,
        IReadOnlyList<ForwardFeatureFireEvent> events)
    {
        var featureSpawns = spawns
            .Where(spawn => spawn.Cell.IsFeat)
            .ToArray();
        var spawnByPosition = new Dictionary<(int r, int c), ForwardSpawn>();
        foreach (var spawn in featureSpawns)
        {
            if (!spawnByPosition.TryAdd((spawn.Row, spawn.Col), spawn))
                return $"feature spawn ({spawn.Row},{spawn.Col}) appears more than once";
        }

        var eventByPosition = new Dictionary<(int r, int c), ForwardFeatureFireEvent>();
        var wheelSeen = false;
        for (var index = 0; index < events.Count; index++)
        {
            var fireEvent = events[index];
            if (!eventByPosition.TryAdd((fireEvent.Row, fireEvent.Col), fireEvent))
                return $"feature fire event ({fireEvent.Row},{fireEvent.Col}) appears more than once";

            if (fireEvent.FeatureSymbol == _settings.F_WHEEL)
            {
                wheelSeen = true;
                continue;
            }

            if (wheelSeen)
                return "WHEEL fire event appeared before a non-WHEEL feature event";
        }

        if (featureSpawns.Length != events.Count)
            return $"feature spawns={featureSpawns.Length}, fire events={events.Count}";

        foreach (var spawn in featureSpawns)
        {
            var position = (spawn.Row, spawn.Col);
            if (!eventByPosition.TryGetValue(position, out var fireEvent))
                return $"feature spawn ({spawn.Row},{spawn.Col}) did not fire";

            var payloadMismatch = PayloadMismatch(spawn, fireEvent);
            if (payloadMismatch != null)
                return payloadMismatch;
        }

        foreach (var fireEvent in events)
        {
            if (!spawnByPosition.ContainsKey((fireEvent.Row, fireEvent.Col)))
                return $"feature fire event ({fireEvent.Row},{fireEvent.Col}) has no matching spawn";
        }

        return null;
    }

    private string? PayloadMismatch(ForwardSpawn spawn, ForwardFeatureFireEvent fireEvent)
    {
        var cell = spawn.Cell;
        if (cell.Sym != fireEvent.FeatureSymbol)
        {
            return $"feature ({spawn.Row},{spawn.Col}) fired symbol {fireEvent.FeatureSymbol}, expected {cell.Sym}";
        }

        if (cell.CvtSym != fireEvent.ConvertToSymbol)
        {
            return $"feature ({spawn.Row},{spawn.Col}) ConvertToId fired {fireEvent.ConvertToSymbol}, expected {cell.CvtSym}";
        }

        if (cell.Sym == _settings.F_WHEEL)
        {
            if (fireEvent.WheelSymbol != cell.Fp?.WheelSym || fireEvent.WheelStack != cell.Fp?.WheelStack)
            {
                return $"WHEEL ({spawn.Row},{spawn.Col}) payload fired symbol={fireEvent.WheelSymbol}, stack={fireEvent.WheelStack}, " +
                       $"expected symbol={cell.Fp?.WheelSym}, stack={cell.Fp?.WheelStack}";
            }

            if (fireEvent.UpgradeSymbol.HasValue || fireEvent.UpgradeTier.HasValue || fireEvent.ExtraGoAward != 0)
                return $"WHEEL ({spawn.Row},{spawn.Col}) carried non-WHEEL fire payload";

            return null;
        }

        if (cell.Sym == _settings.F_PRUP)
        {
            if (fireEvent.UpgradeSymbol != cell.Fp?.PrupSym || fireEvent.UpgradeTier != cell.Fp?.PrupTier)
            {
                return $"PRIZE_UPGRADE ({spawn.Row},{spawn.Col}) payload fired symbol={fireEvent.UpgradeSymbol}, tier={fireEvent.UpgradeTier}, " +
                       $"expected symbol={cell.Fp?.PrupSym}, tier={cell.Fp?.PrupTier}";
            }

            if (fireEvent.WheelSymbol.HasValue || fireEvent.WheelStack.HasValue || fireEvent.ExtraGoAward != 0)
                return $"PRIZE_UPGRADE ({spawn.Row},{spawn.Col}) carried non-PRIZE_UPGRADE fire payload";

            return null;
        }

        if (cell.Sym == _settings.F_XSPIN)
        {
            if (fireEvent.ExtraGoAward != 1)
                return $"EXTRA_GO ({spawn.Row},{spawn.Col}) fired ExtraGoAward={fireEvent.ExtraGoAward}, expected 1";

            if (fireEvent.WheelSymbol.HasValue || fireEvent.WheelStack.HasValue || fireEvent.UpgradeSymbol.HasValue || fireEvent.UpgradeTier.HasValue)
                return $"EXTRA_GO ({spawn.Row},{spawn.Col}) carried non-EXTRA_GO fire payload";

            return null;
        }

        return $"feature ({spawn.Row},{spawn.Col}) has unknown feature symbol {cell.Sym}";
    }

    private string? ValidateNoFeatureCells(Cell?[,] board)
    {
        for (var row = 0; row < _settings.ROWS; row++)
        {
            for (var col = 0; col < _settings.COLS; col++)
            {
                var cell = board[row, col];
                if (cell?.IsFeat == true)
                    return $"board already has unfired feature symbol {cell.Sym} at ({row},{col})";
            }
        }

        return null;
    }

    private ForwardTurnRealizer CreateRealizer(int turn) =>
        new(_settings, SeedForTurn(turn));

    private int SeedForTurn(int turn)
    {
        unchecked
        {
            var seed = _seed;
            seed = (seed * 397) ^ turn;
            seed ^= 0x5bd1e995;
            return seed == int.MinValue ? 0 : Math.Abs(seed);
        }
    }

    private static ForwardTurnCycleResult Fail(
        ForwardTurnCycleStatus status,
        string detail,
        ForwardTurnRealizationResult? realization = null,
        ForwardFeatureFireIntegrationResult? featureFire = null) =>
        new(status, detail, realization, featureFire);
}
