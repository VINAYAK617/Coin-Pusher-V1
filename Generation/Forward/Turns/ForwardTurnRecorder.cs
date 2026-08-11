namespace CoinPusherEngine;

internal enum ForwardTurnRecordStatus
{
    Valid,
    MissingFrame,
    MissingRealization,
    InvalidRealization,
    SpawnOutOfRange,
    DuplicateSpawnPosition,
    InvalidPusherCount,
}

internal readonly struct ForwardRecordedPusher
{
    internal ForwardRecordedPusher(int pushValue, int? featureId)
    {
        PushValue = pushValue;
        FeatureId = featureId;
    }

    internal int PushValue { get; }
    internal int? FeatureId { get; }
}

internal sealed class ForwardRecordedTurn
{
    internal ForwardRecordedTurn(
        int turn,
        IReadOnlyList<ForwardRecordedPusher> pushers,
        IReadOnlyList<ForwardSpawn> spawns,
        IReadOnlyDictionary<int, int> collected,
        IReadOnlyList<ForwardWheelImpact> wheelImpacts,
        int flushCount)
    {
        Turn = turn;
        Pushers = pushers;
        Spawns = spawns;
        Collected = collected;
        WheelImpacts = wheelImpacts;
        FlushCount = flushCount;
    }

    internal int Turn { get; }
    internal IReadOnlyList<ForwardRecordedPusher> Pushers { get; }
    internal IReadOnlyList<ForwardSpawn> Spawns { get; }
    internal IReadOnlyDictionary<int, int> Collected { get; }
    internal IReadOnlyList<ForwardWheelImpact> WheelImpacts { get; }
    internal int FlushCount { get; }
}

internal sealed class ForwardTurnRecordResult
{
    internal ForwardTurnRecordResult(
        ForwardTurnRecordStatus status,
        string detail,
        ForwardRecordedTurn? turn)
    {
        Status = status;
        Detail = detail;
        Turn = turn;
    }

    internal ForwardTurnRecordStatus Status { get; }
    internal string Detail { get; }
    internal ForwardRecordedTurn? Turn { get; }
    internal bool IsValid => Status == ForwardTurnRecordStatus.Valid;
}

internal sealed class ForwardTurnRecorder
{
    private readonly Settings _settings;

    internal ForwardTurnRecorder(Settings settings)
    {
        _settings = settings;
    }

    internal ForwardTurnRecordResult Record(
        ForwardTurnFrame? frame,
        ForwardTurnRealizationResult? realization)
    {
        if (frame == null)
            return Fail(ForwardTurnRecordStatus.MissingFrame, "turn frame is missing");
        if (realization == null)
            return Fail(ForwardTurnRecordStatus.MissingRealization, "turn realization is missing");
        if (!realization.IsValid)
        {
            return Fail(
                ForwardTurnRecordStatus.InvalidRealization,
                $"cannot record invalid realization {realization.Status}: {realization.Detail}");
        }

        if (frame.Shape.Pushers.Count != _settings.COLS)
        {
            return Fail(
                ForwardTurnRecordStatus.InvalidPusherCount,
                $"expected {_settings.COLS} pushers, got {frame.Shape.Pushers.Count}");
        }

        var spawnCheck = ValidateSpawns(realization.Spawns);
        if (spawnCheck.Status != ForwardTurnRecordStatus.Valid)
            return spawnCheck;

        var recorded = new ForwardRecordedTurn(
            frame.Turn,
            frame.Shape.Pushers
                .Select(pusher => new ForwardRecordedPusher(pusher.PushValue, pusher.FeatureId))
                .ToArray(),
            realization.Spawns
                .OrderBy(spawn => spawn.Row)
                .ThenBy(spawn => spawn.Col)
                .Select(spawn => new ForwardSpawn(spawn.Row, spawn.Col, spawn.Cell.Clone()))
                .ToArray(),
            realization.Collected.ToDictionary(kv => kv.Key, kv => kv.Value),
            realization.WheelImpacts.ToArray(),
            realization.FlushCount);

        return new ForwardTurnRecordResult(
            ForwardTurnRecordStatus.Valid,
            "ok",
            recorded);
    }

    private ForwardTurnRecordResult ValidateSpawns(IReadOnlyList<ForwardSpawn> spawns)
    {
        var seen = new HashSet<(int r, int c)>();
        foreach (var spawn in spawns)
        {
            if (spawn.Row < 0 || spawn.Row >= _settings.ROWS || spawn.Col < 0 || spawn.Col >= _settings.COLS)
            {
                return Fail(
                    ForwardTurnRecordStatus.SpawnOutOfRange,
                    $"spawn ({spawn.Row},{spawn.Col}) outside board");
            }

            if (!seen.Add((spawn.Row, spawn.Col)))
            {
                return Fail(
                    ForwardTurnRecordStatus.DuplicateSpawnPosition,
                    $"spawn ({spawn.Row},{spawn.Col}) appears more than once");
            }
        }

        return new ForwardTurnRecordResult(ForwardTurnRecordStatus.Valid, "ok", null);
    }

    private static ForwardTurnRecordResult Fail(
        ForwardTurnRecordStatus status,
        string detail) =>
        new(status, detail, null);
}
