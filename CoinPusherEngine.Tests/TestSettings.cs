namespace CoinPusherEngine;

using GameEngine;

internal static class TestSettings
{
    internal static ICustomProfileSettings Default { get; } = CreateDefault();

    internal static void EnsureConfigured()
    {
        GameEngine.Engine.Settings = Default;
    }

    private static ICustomProfileSettings CreateDefault()
    {
        var settings = new DefaultProfileSettings();
        GameEngine.Engine.Settings = settings;
        return settings;
    }
}
