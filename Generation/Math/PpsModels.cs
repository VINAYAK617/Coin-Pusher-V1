namespace CoinPusherEngine;

/// <summary>
/// One PPS-defined prize component. Symbol ids follow the configured ladder order;
/// tier 0 is base, tier 1 is PRIZE UPGRADE1, tier 2 is PRIZE UPGRADE2.
/// </summary>
public sealed class PpsPrizeComponent
{
    public required int SymbolId { get; init; }
    public required int Tier { get; init; }
}

/// <summary>
/// One exact PPS combination row. The visible label from the sheet is intentionally
/// not used for generation because duplicate amounts can map to different symbols.
/// </summary>
public sealed class PpsPrizeCombination
{
    public required int Id { get; init; }
    public required decimal TotalPrize { get; init; }
    public required IReadOnlyList<PpsPrizeComponent> Components { get; init; }
}

/// <summary>
/// PPS pacing rule for a total win band: exact extra-go count is selected inside
/// the allowed range, then final win completion is selected inside the compatible
/// winning-round range.
/// </summary>
public sealed class PpsSpinRule
{
    public required decimal MinWinInclusive { get; init; }
    public decimal? MaxWinExclusive { get; init; }
    public required int MinExtraGo { get; init; }
    public required int MaxExtraGo { get; init; }
    public int? MinWinningTurn { get; init; }
    public int? MaxWinningTurn { get; init; }

    public bool Matches(decimal totalWin) =>
        totalWin >= MinWinInclusive
        && (!MaxWinExclusive.HasValue || totalWin < MaxWinExclusive.Value);
}
