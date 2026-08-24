namespace CoinPusherEngine.Tests;

using CoinPusherEngine;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class TestAssemblySetup
{
    [AssemblyInitialize]
    public static void Initialize(TestContext context)
    {
        TestSettings.EnsureConfigured();
    }
}
