namespace CoinPusherEngine;

internal enum ForwardMathInputStatus
{
    Valid,
    MissingPrizeAmounts,
    NegativePrizeAmount,
    MissingPrizeLadder,
    PpsFailed,
    BundleFailed,
}

internal sealed class ForwardMathInputResult
{
    internal ForwardMathInputResult(
        ForwardMathInputStatus status,
        string detail,
        BundleResult? bundle)
    {
        Status = status;
        Detail = detail;
        Bundle = bundle;
    }

    internal ForwardMathInputStatus Status { get; }
    internal string Detail { get; }
    internal BundleResult? Bundle { get; }
    internal bool IsValid => Status == ForwardMathInputStatus.Valid;
}

internal sealed class ForwardMathInputResolver
{
    internal ForwardMathInputResult Resolve(
        IReadOnlyList<decimal>? prizeAmounts,
        int seed)
    {
        if (prizeAmounts == null)
        {
            return new ForwardMathInputResult(
                ForwardMathInputStatus.MissingPrizeAmounts,
                "prize amount list is null",
                null);
        }

        if (Settings.PrizeLadderRows == null || Settings.PrizeLadderRows.Count == 0)
        {
            return new ForwardMathInputResult(
                ForwardMathInputStatus.MissingPrizeLadder,
                "settings.PrizeLadderRows is empty",
                null);
        }

        var negative = prizeAmounts.FirstOrDefault(amount => amount < 0);
        if (negative < 0)
        {
            return new ForwardMathInputResult(
                ForwardMathInputStatus.NegativePrizeAmount,
                $"prize amount {negative} cannot be negative",
                null);
        }

        if (Settings.PpsCombinations != null && Settings.PpsCombinations.Count > 0)
        {
            var pps = new PpsMathInputResolver().Resolve(prizeAmounts, seed);
            return pps.IsValid
                ? new ForwardMathInputResult(
                    ForwardMathInputStatus.Valid,
                    pps.Detail,
                    pps.Bundle)
                : new ForwardMathInputResult(
                    ForwardMathInputStatus.PpsFailed,
                    $"{pps.Status}: {pps.Detail}",
                    null);
        }

        try
        {
            var bundle = new LadderCombinator(Settings.PrizeLadderRows, seed)
                .Bundle(prizeAmounts);
            return new ForwardMathInputResult(
                ForwardMathInputStatus.Valid,
                $"resolved {bundle.Covered.Count} prize amount(s)",
                bundle);
        }
        catch (Exception ex)
        {
            return new ForwardMathInputResult(
                ForwardMathInputStatus.BundleFailed,
                ex.Message,
                null);
        }
    }
}
