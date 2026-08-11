namespace CoinPusherEngine;

internal enum ForwardExtraSpinStatus
{
    Valid,
    PlannedTotalBelowBase,
    PlannedTotalAboveMax,
    TurnBeforeEarned,
    ExtraGoOnFinalTurn,
    ExtraGoOverAwardsPlannedTurns,
    EarnedTurnMismatch,
    InvalidExtraGoCount,
}

internal readonly struct ForwardExtraSpinCheck
{
    internal ForwardExtraSpinCheck(
        ForwardExtraSpinStatus status,
        int turn,
        int earnedTurns,
        int plannedTotalTurns,
        string detail)
    {
        Status = status;
        Turn = turn;
        EarnedTurns = earnedTurns;
        PlannedTotalTurns = plannedTotalTurns;
        Detail = detail;
    }

    internal ForwardExtraSpinStatus Status { get; }
    internal int Turn { get; }
    internal int EarnedTurns { get; }
    internal int PlannedTotalTurns { get; }
    internal string Detail { get; }
    internal bool IsValid => Status == ForwardExtraSpinStatus.Valid;
}

internal sealed class ForwardExtraSpinLedger
{
    private readonly Settings _settings;
    private readonly int _plannedTotalTurns;
    private int _earnedTurns;
    private int _logicalExtraGoAwards;

    internal ForwardExtraSpinLedger(int plannedTotalTurns, Settings settings)
    {
        _settings = settings;
        _plannedTotalTurns = plannedTotalTurns;
        _earnedTurns = settings.BASE_SPINS;
    }

    private ForwardExtraSpinLedger(
        int plannedTotalTurns,
        Settings settings,
        int earnedTurns,
        int logicalExtraGoAwards)
    {
        _settings = settings;
        _plannedTotalTurns = plannedTotalTurns;
        _earnedTurns = earnedTurns;
        _logicalExtraGoAwards = logicalExtraGoAwards;
    }

    internal int EarnedTurns => _earnedTurns;
    internal int LogicalExtraGoAwards => _logicalExtraGoAwards;

    internal ForwardExtraSpinLedger Clone() =>
        new(_plannedTotalTurns, _settings, _earnedTurns, _logicalExtraGoAwards);

    internal void ReplaceWith(ForwardExtraSpinLedger other)
    {
        _earnedTurns = other._earnedTurns;
        _logicalExtraGoAwards = other._logicalExtraGoAwards;
    }

    internal ForwardExtraSpinCheck ValidatePlanBounds()
    {
        if (_plannedTotalTurns < _settings.BASE_SPINS)
        {
            return Fail(
                ForwardExtraSpinStatus.PlannedTotalBelowBase,
                0,
                $"planned total turns {_plannedTotalTurns} is below base {_settings.BASE_SPINS}");
        }

        if (_plannedTotalTurns > _settings.MAX_SPINS)
        {
            return Fail(
                ForwardExtraSpinStatus.PlannedTotalAboveMax,
                0,
                $"planned total turns {_plannedTotalTurns} is above max {_settings.MAX_SPINS}");
        }

        return Ok(0);
    }

    internal ForwardExtraSpinCheck BeginTurn(int turn)
    {
        var bounds = ValidatePlanBounds();
        if (!bounds.IsValid) return bounds;

        if (turn > _earnedTurns)
        {
            return Fail(
                ForwardExtraSpinStatus.TurnBeforeEarned,
                turn,
                $"turn {turn} starts before it is earned; earned turns={_earnedTurns}");
        }

        return Ok(turn);
    }

    internal ForwardExtraSpinCheck AwardExtraGo(int turn, int count)
    {
        if (count < 0)
        {
            return Fail(
                ForwardExtraSpinStatus.InvalidExtraGoCount,
                turn,
                $"Extra Go count {count} cannot be negative");
        }

        if (count == 0) return Ok(turn);

        var begin = BeginTurn(turn);
        if (!begin.IsValid) return begin;

        if (turn >= _plannedTotalTurns)
        {
            return Fail(
                ForwardExtraSpinStatus.ExtraGoOnFinalTurn,
                turn,
                $"turn {turn} is final planned turn; Extra Go cannot award a future turn");
        }

        var remainingFutureTurns = _plannedTotalTurns - turn;
        if (count > remainingFutureTurns)
        {
            return Fail(
                ForwardExtraSpinStatus.ExtraGoOverAwardsPlannedTurns,
                turn,
                $"turn {turn} awards {count} Extra Go but only {remainingFutureTurns} future turn(s) remain");
        }

        if (_earnedTurns + count > _plannedTotalTurns)
        {
            return Fail(
                ForwardExtraSpinStatus.ExtraGoOverAwardsPlannedTurns,
                turn,
                $"earned turns would become {_earnedTurns + count}, above planned total {_plannedTotalTurns}");
        }

        _earnedTurns += count;
        _logicalExtraGoAwards += count;
        return Ok(turn);
    }

    internal ForwardExtraSpinCheck ValidateFinal()
    {
        var bounds = ValidatePlanBounds();
        if (!bounds.IsValid) return bounds;

        if (_earnedTurns != _plannedTotalTurns)
        {
            return Fail(
                ForwardExtraSpinStatus.EarnedTurnMismatch,
                _plannedTotalTurns,
                $"earned turns={_earnedTurns}, planned total turns={_plannedTotalTurns}, logical Extra Go awards={_logicalExtraGoAwards}");
        }

        return Ok(_plannedTotalTurns);
    }

    private ForwardExtraSpinCheck Ok(int turn) =>
        new(ForwardExtraSpinStatus.Valid, turn, _earnedTurns, _plannedTotalTurns, "ok");

    private ForwardExtraSpinCheck Fail(ForwardExtraSpinStatus status, int turn, string detail) =>
        new(status, turn, _earnedTurns, _plannedTotalTurns, detail);
}
