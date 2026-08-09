namespace CoinPusherEngine;

internal enum CoinPusherTicketGenerationValidationStatus
{
    Valid,
    MissingGenerationResult,
    GenerationNotValid,
    MissingJson,
    MissingTicket,
    MissingAdaptedPlan,
    MissingBuildResult,
    MissingCheckerReport,
    PlanNotVerified,
    SpinCountMismatch,
    PrizeCoverageMismatch,
    TicketCheckerFailed,
}

internal sealed class CoinPusherTicketGenerationValidationResult
{
    internal CoinPusherTicketGenerationValidationResult(
        CoinPusherTicketGenerationValidationStatus status,
        string detail)
    {
        Status = status;
        Detail = detail;
    }

    internal CoinPusherTicketGenerationValidationStatus Status { get; }
    internal string Detail { get; }
    internal bool IsValid => Status == CoinPusherTicketGenerationValidationStatus.Valid;
}

internal sealed class CoinPusherTicketGenerationValidator
{
    internal CoinPusherTicketGenerationValidationResult Validate(
        IReadOnlyList<decimal>? requestedPrizeAmounts,
        ForwardTicketGenerationResult? generated)
    {
        if (generated == null)
            return Fail(CoinPusherTicketGenerationValidationStatus.MissingGenerationResult, "generation result is missing");
        if (!generated.IsValid)
            return Fail(CoinPusherTicketGenerationValidationStatus.GenerationNotValid, $"{generated.Status}: {generated.Detail}");
        if (string.IsNullOrWhiteSpace(generated.Json))
            return Fail(CoinPusherTicketGenerationValidationStatus.MissingJson, "generated JSON is missing");
        if (generated.Ticket == null)
            return Fail(CoinPusherTicketGenerationValidationStatus.MissingTicket, "ticket DTO is missing");
        if (generated.AdaptedPlan?.Plan == null)
            return Fail(CoinPusherTicketGenerationValidationStatus.MissingAdaptedPlan, "adapted GamePlan is missing");
        if (generated.Build == null)
            return Fail(CoinPusherTicketGenerationValidationStatus.MissingBuildResult, "build result is missing");
        if (generated.CheckerReport == null)
            return Fail(CoinPusherTicketGenerationValidationStatus.MissingCheckerReport, "ticket checker report is missing");

        if (!generated.AdaptedPlan.Plan.Verified)
            return Fail(CoinPusherTicketGenerationValidationStatus.PlanNotVerified, "adapted GamePlan is not verified");

        var spinMismatch = ValidateSpinCounts(generated);
        if (spinMismatch != null)
            return spinMismatch;

        var coverage = ValidatePrizeCoverage(requestedPrizeAmounts, generated.Build);
        if (coverage != null)
            return coverage;

        if (!generated.CheckerReport.IsValid)
        {
            var first = generated.CheckerReport.Checks
                .FirstOrDefault(check => check.Result == TicketChecker.Status.Fail);
            return Fail(
                CoinPusherTicketGenerationValidationStatus.TicketCheckerFailed,
                first == null ? "ticket checker failed" : $"{first.Category}/{first.Name}: {first.Detail}");
        }

        return new CoinPusherTicketGenerationValidationResult(
            CoinPusherTicketGenerationValidationStatus.Valid,
            "ok");
    }

    private static CoinPusherTicketGenerationValidationResult? ValidateSpinCounts(
        ForwardTicketGenerationResult generated)
    {
        var ticketSpins = generated.Ticket!.WinInfo.TotalSpins;
        var ticketTurns = generated.Ticket.Turns.Length;
        var planSpins = generated.AdaptedPlan!.Plan!.TotalSpins;
        var planTurns = generated.AdaptedPlan.Plan.Spins.Count;

        if (ticketSpins != ticketTurns || ticketSpins != planSpins || ticketSpins != planTurns)
        {
            return Fail(
                CoinPusherTicketGenerationValidationStatus.SpinCountMismatch,
                $"ticket TotalSpins={ticketSpins}, ticket turns={ticketTurns}, plan TotalSpins={planSpins}, plan turns={planTurns}");
        }

        return null;
    }

    private static CoinPusherTicketGenerationValidationResult? ValidatePrizeCoverage(
        IReadOnlyList<decimal>? requestedPrizeAmounts,
        ForwardTicketBuildResult build)
    {
        var requested = requestedPrizeAmounts ?? Array.Empty<decimal>();
        var bundle = build.MathInput?.Bundle;
        if (bundle == null)
        {
            return Fail(
                CoinPusherTicketGenerationValidationStatus.PrizeCoverageMismatch,
                "bundle result is missing");
        }

        var requestedSorted = requested.OrderBy(amount => amount).ToArray();
        var coveredSorted = bundle.Covered.OrderBy(amount => amount).ToArray();
        if (!requestedSorted.SequenceEqual(coveredSorted))
        {
            return Fail(
                CoinPusherTicketGenerationValidationStatus.PrizeCoverageMismatch,
                $"requested=[{string.Join(",", requestedSorted)}], covered=[{string.Join(",", coveredSorted)}]");
        }

        if (bundle.Skipped.Count > 0)
        {
            return Fail(
                CoinPusherTicketGenerationValidationStatus.PrizeCoverageMismatch,
                $"bundle skipped prize amount(s): [{string.Join(",", bundle.Skipped.OrderBy(amount => amount))}]");
        }

        return null;
    }

    private static CoinPusherTicketGenerationValidationResult Fail(
        CoinPusherTicketGenerationValidationStatus status,
        string detail) =>
        new(status, detail);
}
