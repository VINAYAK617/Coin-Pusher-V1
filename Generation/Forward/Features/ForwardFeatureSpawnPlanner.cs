namespace CoinPusherEngine;

internal enum ForwardFeatureKind
{
    Unknown = 0,
    Wheel,
    ExtraGo,
    PrizeUpgrade,
}

internal enum ForwardFeatureSpawnStatus
{
    Valid,
    InvalidTurn,
    MissingEmptyPositions,
    MissingFeatureRequests,
    MissingFeatureCapacity,
    InvalidEmptyPosition,
    DuplicateEmptyPosition,
    InvalidReservedPosition,
    DuplicateReservedPosition,
    InvalidFeaturePosition,
    FeaturePositionNotEmpty,
    FeaturePositionAlreadyReserved,
    DuplicateFeaturePosition,
    FeatureOnFinalTurn,
    FeatureCapacityExceeded,
    UnknownFeatureKind,
    InvalidConvertSymbol,
    InvalidWheelPayload,
    InvalidPrizeUpgradePayload,
    MultiplePrizeUpgradesSameSymbolSameTurn,
    ExtraGoTimelineInvalid,
    PrizeUpgradeInvalid,
}

internal readonly struct ForwardFeatureSpawnRequest
{
    private ForwardFeatureSpawnRequest(
        int row,
        int col,
        ForwardFeatureKind kind,
        int convertToSymbol,
        int? wheelSymbol,
        int? wheelStack,
        int? upgradeSymbol,
        int? upgradeTier)
    {
        Row = row;
        Col = col;
        Kind = kind;
        ConvertToSymbol = convertToSymbol;
        WheelSymbol = wheelSymbol;
        WheelStack = wheelStack;
        UpgradeSymbol = upgradeSymbol;
        UpgradeTier = upgradeTier;
    }

    internal int Row { get; }
    internal int Col { get; }
    internal ForwardFeatureKind Kind { get; }
    internal int ConvertToSymbol { get; }
    internal int? WheelSymbol { get; }
    internal int? WheelStack { get; }
    internal int? UpgradeSymbol { get; }
    internal int? UpgradeTier { get; }

    internal static ForwardFeatureSpawnRequest Wheel(
        int row,
        int col,
        int convertToSymbol,
        int wheelSymbol,
        int wheelStack) =>
        new(row, col, ForwardFeatureKind.Wheel, convertToSymbol, wheelSymbol, wheelStack, null, null);

    internal static ForwardFeatureSpawnRequest ExtraGo(
        int row,
        int col,
        int convertToSymbol) =>
        new(row, col, ForwardFeatureKind.ExtraGo, convertToSymbol, null, null, null, null);

    internal static ForwardFeatureSpawnRequest PrizeUpgrade(
        int row,
        int col,
        int convertToSymbol,
        int upgradeSymbol,
        int upgradeTier) =>
        new(row, col, ForwardFeatureKind.PrizeUpgrade, convertToSymbol, null, null, upgradeSymbol, upgradeTier);
}

internal sealed class ForwardFeatureSpawnPlanResult
{
    internal ForwardFeatureSpawnPlanResult(
        ForwardFeatureSpawnStatus status,
        string detail,
        IReadOnlyList<ForwardSpawn> spawns,
        IReadOnlyList<ForwardWheelImpact> wheelImpacts)
    {
        Status = status;
        Detail = detail;
        Spawns = spawns;
        WheelImpacts = wheelImpacts;
    }

    internal ForwardFeatureSpawnStatus Status { get; }
    internal string Detail { get; }
    internal IReadOnlyList<ForwardSpawn> Spawns { get; }
    internal IReadOnlyList<ForwardWheelImpact> WheelImpacts { get; }
    internal bool IsValid => Status == ForwardFeatureSpawnStatus.Valid;
}

internal sealed class ForwardFeatureSpawnPlanner
{
    private const string WheelId = "WHEEL";
    private const string ExtraGoId = "EXTRA_SPIN";
    private const string PrizeUpgradeId = "PRIZE_UPGRADE";

    private readonly ICustomProfileSettings _settings;
    private readonly int _maxSymbol;

    internal ForwardFeatureSpawnPlanner(ICustomProfileSettings settings, int maxSymbol)
    {
        _settings = settings;
        _maxSymbol = maxSymbol;
    }

    internal ForwardFeatureSpawnPlanResult Plan(
        int turn,
        int plannedTotalTurns,
        IReadOnlyCollection<(int r, int c)>? emptyPositions,
        IReadOnlyCollection<(int r, int c)>? reservedPositions,
        IReadOnlyList<ForwardFeatureSpawnRequest>? featureRequests,
        IReadOnlyDictionary<ForwardFeatureKind, int>? remainingFeatureCapacity,
        ForwardExtraSpinLedger extraSpinLedger,
        ForwardPrizeUpgradeLedger prizeUpgradeLedger)
    {
        if (turn <= 0 || plannedTotalTurns <= 0 || turn > plannedTotalTurns)
            return Fail(ForwardFeatureSpawnStatus.InvalidTurn, $"turn={turn}, plannedTotalTurns={plannedTotalTurns}");
        if (emptyPositions == null)
            return Fail(ForwardFeatureSpawnStatus.MissingEmptyPositions, "empty positions are missing");
        if (featureRequests == null)
            return Fail(ForwardFeatureSpawnStatus.MissingFeatureRequests, "feature requests are missing");
        if (remainingFeatureCapacity == null)
            return Fail(ForwardFeatureSpawnStatus.MissingFeatureCapacity, "remaining feature capacity is missing");

        var emptyCheck = BuildPositionSet(
            emptyPositions,
            ForwardFeatureSpawnStatus.InvalidEmptyPosition,
            ForwardFeatureSpawnStatus.DuplicateEmptyPosition);
        if (emptyCheck.Status != ForwardFeatureSpawnStatus.Valid) return emptyCheck;
        var reservedCheck = BuildPositionSet(
            reservedPositions ?? Array.Empty<(int r, int c)>(),
            ForwardFeatureSpawnStatus.InvalidReservedPosition,
            ForwardFeatureSpawnStatus.DuplicateReservedPosition);
        if (reservedCheck.Status != ForwardFeatureSpawnStatus.Valid) return reservedCheck;
        var empty = emptyPositions.ToHashSet();
        var reserved = reservedPositions?.ToHashSet() ?? new HashSet<(int r, int c)>();

        var basic = ValidateRequests(turn, plannedTotalTurns, featureRequests, empty, reserved, remainingFeatureCapacity);
        if (basic.Status != ForwardFeatureSpawnStatus.Valid) return basic;

        var extraClone = extraSpinLedger.Clone();
        var prizeClone = prizeUpgradeLedger.Clone();
        var timeline = ValidateLedgers(turn, featureRequests, extraClone, prizeClone);
        if (timeline.Status != ForwardFeatureSpawnStatus.Valid) return timeline;

        var spawns = new List<ForwardSpawn>(featureRequests.Count);
        var wheelImpacts = new List<ForwardWheelImpact>();
        foreach (var request in featureRequests)
        {
            var cell = ToCell(request);
            spawns.Add(new ForwardSpawn(request.Row, request.Col, cell));
            if (request.Kind == ForwardFeatureKind.Wheel)
                wheelImpacts.Add(new ForwardWheelImpact(turn, request.WheelSymbol!.Value, request.WheelStack!.Value));
        }

        var commit = CommitLedgers(turn, featureRequests, extraSpinLedger, prizeUpgradeLedger);
        if (commit.Status != ForwardFeatureSpawnStatus.Valid) return commit;

        return new ForwardFeatureSpawnPlanResult(
            ForwardFeatureSpawnStatus.Valid,
            $"planned {spawns.Count} feature spawn(s)",
            spawns,
            wheelImpacts);
    }

    private ForwardFeatureSpawnPlanResult ValidateRequests(
        int turn,
        int plannedTotalTurns,
        IReadOnlyList<ForwardFeatureSpawnRequest> requests,
        HashSet<(int r, int c)> empty,
        HashSet<(int r, int c)> reserved,
        IReadOnlyDictionary<ForwardFeatureKind, int> remainingFeatureCapacity)
    {
        var seen = new HashSet<(int r, int c)>();
        foreach (var request in requests)
        {
            var position = (request.Row, request.Col);
            if (!PositionInRange(position))
                return Fail(ForwardFeatureSpawnStatus.InvalidFeaturePosition, $"feature position ({request.Row},{request.Col}) outside board");
            if (!empty.Contains(position))
                return Fail(ForwardFeatureSpawnStatus.FeaturePositionNotEmpty, $"feature position ({request.Row},{request.Col}) is not an empty spawn slot");
            if (reserved.Contains(position))
                return Fail(ForwardFeatureSpawnStatus.FeaturePositionAlreadyReserved, $"feature position ({request.Row},{request.Col}) is already reserved");
            if (!seen.Add(position))
                return Fail(ForwardFeatureSpawnStatus.DuplicateFeaturePosition, $"feature position ({request.Row},{request.Col}) appears more than once");
            if (turn >= plannedTotalTurns)
                return Fail(ForwardFeatureSpawnStatus.FeatureOnFinalTurn, $"feature {request.Kind} requested on final turn {turn}");
            if (request.Kind == ForwardFeatureKind.Unknown)
                return Fail(ForwardFeatureSpawnStatus.UnknownFeatureKind, "feature kind is Unknown");
            if (!ValidConvert(request.ConvertToSymbol))
                return Fail(ForwardFeatureSpawnStatus.InvalidConvertSymbol, $"feature {request.Kind} ConvertToSymbol={request.ConvertToSymbol} is invalid");

            var payload = ValidatePayload(request);
            if (payload.Status != ForwardFeatureSpawnStatus.Valid) return payload;
        }

        foreach (var group in requests.GroupBy(request => request.Kind))
        {
            if (!remainingFeatureCapacity.TryGetValue(group.Key, out var remaining))
                return Fail(ForwardFeatureSpawnStatus.MissingFeatureCapacity, $"capacity missing for feature {group.Key}");
            if (group.Count() > remaining)
                return Fail(ForwardFeatureSpawnStatus.FeatureCapacityExceeded, $"feature {group.Key} requested {group.Count()}, remaining capacity {remaining}");
        }

        var sameTurnDuplicateUpgrades = requests
            .Where(request => request.Kind == ForwardFeatureKind.PrizeUpgrade)
            .GroupBy(request => request.UpgradeSymbol!.Value)
            .FirstOrDefault(group => group.Count() > 1);
        if (sameTurnDuplicateUpgrades != null)
        {
            return Fail(
                ForwardFeatureSpawnStatus.MultiplePrizeUpgradesSameSymbolSameTurn,
                $"symbol {sameTurnDuplicateUpgrades.Key} has {sameTurnDuplicateUpgrades.Count()} PRIZE_UPGRADE requests in the same turn");
        }

        return Ok();
    }

    private ForwardFeatureSpawnPlanResult ValidatePayload(ForwardFeatureSpawnRequest request)
    {
        if (request.Kind == ForwardFeatureKind.Wheel)
        {
            var minStack = _settings.MIN_WHEEL_STACK_VALUE + 1;
            var maxStack = _settings.MAX_WHEEL_STACK_VALUE + 1;
            if (!request.WheelSymbol.HasValue
                || !request.WheelStack.HasValue
                || !ValidSymbol(request.WheelSymbol.Value)
                || request.WheelStack.Value < minStack
                || request.WheelStack.Value > maxStack
                || request.UpgradeSymbol.HasValue
                || request.UpgradeTier.HasValue)
            {
                return Fail(
                    ForwardFeatureSpawnStatus.InvalidWheelPayload,
                    $"invalid WHEEL payload symbol={request.WheelSymbol}, stack={request.WheelStack}");
            }
        }
        else if (request.Kind == ForwardFeatureKind.PrizeUpgrade)
        {
            if (!request.UpgradeSymbol.HasValue
                || !request.UpgradeTier.HasValue
                || !ValidSymbol(request.UpgradeSymbol.Value)
                || request.UpgradeTier.Value <= 0
                || request.WheelSymbol.HasValue
                || request.WheelStack.HasValue)
            {
                return Fail(
                    ForwardFeatureSpawnStatus.InvalidPrizeUpgradePayload,
                    $"invalid PRIZE_UPGRADE payload symbol={request.UpgradeSymbol}, tier={request.UpgradeTier}");
            }
        }
        else if (request.Kind == ForwardFeatureKind.ExtraGo)
        {
            if (request.WheelSymbol.HasValue
                || request.WheelStack.HasValue
                || request.UpgradeSymbol.HasValue
                || request.UpgradeTier.HasValue)
            {
                return Fail(
                    ForwardFeatureSpawnStatus.UnknownFeatureKind,
                    "EXTRA_GO request must not carry WHEEL or PRIZE_UPGRADE payload");
            }
        }

        return Ok();
    }

    private ForwardFeatureSpawnPlanResult ValidateLedgers(
        int turn,
        IReadOnlyList<ForwardFeatureSpawnRequest> requests,
        ForwardExtraSpinLedger extraSpinLedger,
        ForwardPrizeUpgradeLedger prizeUpgradeLedger)
    {
        var extraCount = requests.Count(request => request.Kind == ForwardFeatureKind.ExtraGo);
        var extra = extraSpinLedger.AwardExtraGo(turn, extraCount);
        if (!extra.IsValid)
            return Fail(ForwardFeatureSpawnStatus.ExtraGoTimelineInvalid, extra.Detail);

        foreach (var request in requests.Where(request => request.Kind == ForwardFeatureKind.PrizeUpgrade))
        {
            var prize = prizeUpgradeLedger.ApplyUpgrade(request.UpgradeSymbol!.Value, request.UpgradeTier!.Value);
            if (!prize.IsValid)
                return Fail(ForwardFeatureSpawnStatus.PrizeUpgradeInvalid, prize.Detail);
        }

        return Ok();
    }

    private ForwardFeatureSpawnPlanResult CommitLedgers(
        int turn,
        IReadOnlyList<ForwardFeatureSpawnRequest> requests,
        ForwardExtraSpinLedger extraSpinLedger,
        ForwardPrizeUpgradeLedger prizeUpgradeLedger) =>
        ValidateLedgers(turn, requests, extraSpinLedger, prizeUpgradeLedger);

    private Cell ToCell(ForwardFeatureSpawnRequest request)
    {
        return request.Kind switch
        {
            ForwardFeatureKind.Wheel => Grid.Feat(
                _settings.F_WHEEL,
                request.ConvertToSymbol,
                new FP { FeatId = WheelId, WheelSym = request.WheelSymbol!.Value, WheelStack = request.WheelStack!.Value }),
            ForwardFeatureKind.ExtraGo => Grid.Feat(
                _settings.F_XSPIN,
                request.ConvertToSymbol,
                new FP { FeatId = ExtraGoId }),
            ForwardFeatureKind.PrizeUpgrade => Grid.Feat(
                _settings.F_PRUP,
                request.ConvertToSymbol,
                new FP { FeatId = PrizeUpgradeId, PrupSym = request.UpgradeSymbol!.Value, PrupTier = request.UpgradeTier!.Value }),
            _ => Grid.Norm(_settings.F_COIN),
        };
    }

    private ForwardFeatureSpawnPlanResult BuildPositionSet(
        IReadOnlyCollection<(int r, int c)> positions,
        ForwardFeatureSpawnStatus invalidStatus,
        ForwardFeatureSpawnStatus duplicateStatus)
    {
        var seen = new HashSet<(int r, int c)>();
        foreach (var position in positions)
        {
            if (!PositionInRange(position))
                return Fail(invalidStatus, $"position ({position.r},{position.c}) outside board");
            if (!seen.Add(position))
                return Fail(duplicateStatus, $"position ({position.r},{position.c}) appears more than once");
        }

        return Ok();
    }

    private bool PositionInRange((int r, int c) position) =>
        position.r >= 0 && position.r < _settings.ROWS && position.c >= 0 && position.c < _settings.COLS;

    private bool ValidSymbol(int symbol) =>
        symbol >= 1 && symbol <= _maxSymbol && !_settings.IsFeat(symbol);

    private bool ValidConvert(int symbol) => ValidSymbol(symbol);

    private static ForwardFeatureSpawnPlanResult Ok() =>
        new(
            ForwardFeatureSpawnStatus.Valid,
            "ok",
            Array.Empty<ForwardSpawn>(),
            Array.Empty<ForwardWheelImpact>());

    private static ForwardFeatureSpawnPlanResult Fail(ForwardFeatureSpawnStatus status, string detail) =>
        new(status, detail, Array.Empty<ForwardSpawn>(), Array.Empty<ForwardWheelImpact>());
}
