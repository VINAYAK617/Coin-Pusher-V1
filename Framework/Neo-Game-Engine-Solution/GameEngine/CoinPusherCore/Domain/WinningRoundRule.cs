namespace CoinPusherEngine;

public sealed class WinningRoundRule
{
    public decimal MinWinInclusive { get; init; }
    public decimal? MaxWinExclusive { get; init; }
    public IReadOnlyList<int> ExtraGoCounts { get; init; } = Array.Empty<int>();
    public int? MinWinningTurn { get; init; }
    public int? MaxWinningTurn { get; init; }

    public bool Matches(decimal totalWin) =>
        totalWin >= MinWinInclusive
        && (!MaxWinExclusive.HasValue || totalWin < MaxWinExclusive.Value);
}
