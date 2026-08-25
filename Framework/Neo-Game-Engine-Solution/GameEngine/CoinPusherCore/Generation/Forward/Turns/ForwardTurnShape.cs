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
    internal bool IsFlush(ICustomProfileSettings settings) => FeatureId == settings.F_FLUSH_ID;
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
    private readonly ICustomProfileSettings _settings;

    private ForwardTurnShape(IReadOnlyList<ForwardPusher> pushers, ICustomProfileSettings settings)
    {
        Pushers = pushers.ToArray();
        _settings = settings;
    }

    internal IReadOnlyList<ForwardPusher> Pushers { get; }

    internal int PoppedCellCount => Pushers.Sum(p => p.IsFlush(_settings)
        ? _settings.ROWS
        : p.PushValue);

    internal IEnumerable<(int r, int c)> CollectionCells()
    {
        for (var col = 0; col < _settings.COLS; col++)
        {
            var pusher = Pushers[col];
            if (pusher.IsFlush(_settings))
            {
                for (var row = 0; row < _settings.ROWS; row++)
                    yield return (row, col);
                continue;
            }

            for (var row = _settings.ROWS - pusher.PushValue; row < _settings.ROWS; row++)
                yield return (row, col);
        }
    }

    internal static (ForwardTurnShape? Shape, ForwardTurnShapeCheck Check) TryCreate(
        IReadOnlyList<ForwardPusher> pushers,
        ICustomProfileSettings settings)
    {
        var check = Validate(pushers, settings);
        return check.IsValid
            ? (new ForwardTurnShape(pushers, settings), check)
            : (null, check);
    }

    internal static ForwardTurnShapeCheck Validate(
        IReadOnlyList<ForwardPusher>? pushers,
        ICustomProfileSettings settings)
    {
        if (pushers == null || pushers.Count != settings.COLS)
        {
            return new ForwardTurnShapeCheck(
                ForwardTurnShapeStatus.WrongColumnCount,
                $"expected {settings.COLS} pushers, got {pushers?.Count ?? 0}");
        }

        for (var col = 0; col < pushers.Count; col++)
        {
            var pusher = pushers[col];
            if (pusher.FeatureId.HasValue)
            {
                if (pusher.FeatureId.Value != settings.F_FLUSH_ID || pusher.PushValue != settings.ROWS)
                {
                    return new ForwardTurnShapeCheck(
                        ForwardTurnShapeStatus.InvalidFeaturePush,
                        $"column {col} has PushValue={pusher.PushValue}, FeatureId={pusher.FeatureId}; expected flush {settings.ROWS}/{settings.F_FLUSH_ID}");
                }

                continue;
            }

            if (pusher.PushValue < settings.MIN_PUSH || pusher.PushValue > settings.MAX_PUSH)
            {
                return new ForwardTurnShapeCheck(
                    ForwardTurnShapeStatus.InvalidNormalPush,
                    $"column {col} has normal PushValue={pusher.PushValue}; expected {settings.MIN_PUSH}..{settings.MAX_PUSH}");
            }
        }

        return new ForwardTurnShapeCheck(ForwardTurnShapeStatus.Valid, "ok");
    }
}
