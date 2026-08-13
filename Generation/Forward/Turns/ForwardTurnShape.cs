namespace CoinPusherEngine;

internal enum ForwardTurnShapeStatus
{
    Valid,
    WrongColumnCount,
    InvalidNormalPush,
    InvalidFeaturePush,
}

internal readonly struct ForwardPusher
{
    internal ForwardPusher(int pushValue, int? featureId = null)
    {
        PushValue = pushValue;
        FeatureId = featureId;
    }

    internal int PushValue { get; }
    internal int? FeatureId { get; }
    internal bool IsFlush() => FeatureId == Settings.F_FLUSH_ID;
}

internal readonly struct ForwardTurnShapeCheck
{
    internal ForwardTurnShapeCheck(ForwardTurnShapeStatus status, string detail)
    {
        Status = status;
        Detail = detail;
    }

    internal ForwardTurnShapeStatus Status { get; }
    internal string Detail { get; }
    internal bool IsValid => Status == ForwardTurnShapeStatus.Valid;
}

internal sealed class ForwardTurnShape
{

    private ForwardTurnShape(IReadOnlyList<ForwardPusher> pushers)
    {
        Pushers = pushers.ToArray();
    }

    internal IReadOnlyList<ForwardPusher> Pushers { get; }

    internal int PoppedCellCount => Pushers.Sum(p => p.IsFlush()
        ? Settings.ROWS
        : p.PushValue);

    internal IEnumerable<(int r, int c)> CollectionCells()
    {
        for (var col = 0; col < Settings.COLS; col++)
        {
            var pusher = Pushers[col];
            if (pusher.IsFlush())
            {
                for (var row = 0; row < Settings.ROWS; row++)
                    yield return (row, col);
                continue;
            }

            for (var row = Settings.ROWS - pusher.PushValue; row < Settings.ROWS; row++)
                yield return (row, col);
        }
    }

    internal static (ForwardTurnShape? Shape, ForwardTurnShapeCheck Check) TryCreate(
        IReadOnlyList<ForwardPusher> pushers)
    {
        var check = Validate(pushers);
        return check.IsValid
            ? (new ForwardTurnShape(pushers), check)
            : (null, check);
    }

    internal static ForwardTurnShapeCheck Validate(
        IReadOnlyList<ForwardPusher>? pushers)
    {
        if (pushers == null || pushers.Count != Settings.COLS)
        {
            return new ForwardTurnShapeCheck(
                ForwardTurnShapeStatus.WrongColumnCount,
                $"expected {Settings.COLS} pushers, got {pushers?.Count ?? 0}");
        }

        for (var col = 0; col < pushers.Count; col++)
        {
            var pusher = pushers[col];
            if (pusher.FeatureId.HasValue)
            {
                if (pusher.FeatureId.Value != Settings.F_FLUSH_ID || pusher.PushValue != Settings.ROWS)
                {
                    return new ForwardTurnShapeCheck(
                        ForwardTurnShapeStatus.InvalidFeaturePush,
                        $"column {col} has PushValue={pusher.PushValue}, FeatureId={pusher.FeatureId}; expected flush {Settings.ROWS}/{Settings.F_FLUSH_ID}");
                }

                continue;
            }

            if (pusher.PushValue < Settings.MIN_PUSH || pusher.PushValue > Settings.MAX_PUSH)
            {
                return new ForwardTurnShapeCheck(
                    ForwardTurnShapeStatus.InvalidNormalPush,
                    $"column {col} has normal PushValue={pusher.PushValue}; expected {Settings.MIN_PUSH}..{Settings.MAX_PUSH}");
            }
        }

        return new ForwardTurnShapeCheck(ForwardTurnShapeStatus.Valid, "ok");
    }
}
