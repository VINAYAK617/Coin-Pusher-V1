namespace CoinPusherEngine;

internal enum ForwardMathInputStatus
{
    Valid,
    MissingPrizeAmounts,
    NegativePrizeAmount,
    MissingPrizeLadder,
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
    private readonly Settings _settings;

    internal ForwardMathInputResolver(Settings settings)
    {
        _settings = settings;
    }

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

        if (_settings.PrizeLadderRows == null || _settings.PrizeLadderRows.Count == 0)
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

        try
        {
            var bundle = new LadderCombinator(_settings.PrizeLadderRows, seed, _settings)
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
