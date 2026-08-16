namespace CoinPusherEngine;

using GameEngine;

public static class ProfileSettingsExtensions
{
    private static readonly string[] FeatureIds =
    {
        "WHEEL",
        "FLUSH",
        "EXTRA_SPIN",
        "PRIZE_UPGRADE",
    };

    public static int MixedPushCapacity(this ICustomProfileSettings settings, int freeCols)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        if (freeCols <= 0) return 0;
        if (freeCols >= 3)
            return (settings.MAX_PUSH * (freeCols - 2)) + settings.MIN_PUSH + (settings.MIN_PUSH + 1);
        if (freeCols == 2)
            return settings.MAX_PUSH + settings.MIN_PUSH;
        return settings.MAX_PUSH;
    }

    public static int SymbolFillCap(this ICustomProfileSettings settings, int sym)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        if (sym >= 1 && sym <= settings.PrizeLadderRows.Count)
            return settings.PrizeLadderRows[sym - 1].Target;

        return settings.FILL_CAP;
    }

    public static bool IsFeat(this ICustomProfileSettings settings, int id)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        return id == settings.F_WHEEL || id == settings.F_XSPIN || id == settings.F_PRUP;
    }

    public static IReadOnlyDictionary<string, (double P, int Max, int MinS, int MaxS, int Ord)> FeatureConfigs(
        this ICustomProfileSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        return FeatureIds.ToDictionary(id => id, settings.FeatureConfig);
    }

    public static IEnumerable<string> OrderedFeatureIds(this ICustomProfileSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        return FeatureIds.OrderBy(id => settings.FeatureConfig(id).Ord);
    }

    public static (double P, int Max, int MinS, int MaxS, int Ord) FeatureConfig(
        this ICustomProfileSettings settings,
        string id)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        return id switch
        {
            "WHEEL" => settings.WheelFeatureConfig,
            "FLUSH" => settings.FlushFeatureConfig,
            "EXTRA_SPIN" => settings.ExtraSpinFeatureConfig,
            "PRIZE_UPGRADE" => settings.PrizeUpgradeFeatureConfig,
            _ => throw new ArgumentException($"Unknown feature config '{id}'", nameof(id)),
        };
    }
}
