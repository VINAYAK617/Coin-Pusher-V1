namespace CoinPusherEngine;

internal enum ForwardStackImpactStatus
{
    Valid,
    InvalidSymbol,
    InvalidSpawnStack,
    InvalidCollectionTurn,
    InvalidWheel,
}

internal readonly struct ForwardWheelImpact
{
    internal ForwardWheelImpact(int fireTurn, int symbol, int stackValue)
    {
        FireTurn = fireTurn;
        Symbol = symbol;
        StackValue = stackValue;
    }

    internal int FireTurn { get; }
    internal int Symbol { get; }
    internal int StackValue { get; }
    internal int StackAdd => Math.Max(0, StackValue - 1);
}

internal readonly struct ForwardStackImpact
{
    internal ForwardStackImpact(
        ForwardStackImpactStatus status,
        int symbol,
        int spawnStack,
        int collectionTurn,
        int finalStack,
        int appliedWheelCount,
        string detail)
    {
        Status = status;
        Symbol = symbol;
        SpawnStack = spawnStack;
        CollectionTurn = collectionTurn;
        FinalStack = finalStack;
        AppliedWheelCount = appliedWheelCount;
        Detail = detail;
    }

    internal ForwardStackImpactStatus Status { get; }
    internal int Symbol { get; }
    internal int SpawnStack { get; }
    internal int CollectionTurn { get; }
    internal int FinalStack { get; }
    internal int AppliedWheelCount { get; }
    internal string Detail { get; }
    internal bool IsValid => Status == ForwardStackImpactStatus.Valid;
}

internal sealed class ForwardStackImpactAnalyzer
{
    private readonly IReadOnlyList<ForwardWheelImpact> _wheels;
    private readonly int _maxSymbol;
    private readonly ICustomProfileSettings _settings;

    internal ForwardStackImpactAnalyzer(
        IReadOnlyList<ForwardWheelImpact> wheels,
        int maxSymbol,
        ICustomProfileSettings settings)
    {
        _wheels = wheels.OrderBy(wheel => wheel.FireTurn).ToArray();
        _maxSymbol = maxSymbol;
        _settings = settings;
    }

    internal ForwardStackImpact Analyze(
        int symbol,
        int spawnTurn,
        int? collectionTurn,
        int spawnStack = 1)
    {
        if (symbol < 1 || symbol > _maxSymbol || _settings.IsFeat(symbol))
        {
            return Fail(
                ForwardStackImpactStatus.InvalidSymbol,
                symbol,
                spawnStack,
                collectionTurn ?? 0,
                0,
                $"symbol {symbol} is outside 1..{_maxSymbol} or is a feature symbol");
        }

        if (spawnStack <= 0 || spawnStack > _settings.MAX_COIN_STACK)
        {
            return Fail(
                ForwardStackImpactStatus.InvalidSpawnStack,
                symbol,
                spawnStack,
                collectionTurn ?? 0,
                0,
                $"spawn stack {spawnStack} must be in 1..{_settings.MAX_COIN_STACK}");
        }

        if (!collectionTurn.HasValue)
        {
            return new ForwardStackImpact(
                ForwardStackImpactStatus.Valid,
                symbol,
                spawnStack,
                0,
                spawnStack,
                0,
                "not collected in planned future turns");
        }

        if (collectionTurn.Value <= spawnTurn)
        {
            return Fail(
                ForwardStackImpactStatus.InvalidCollectionTurn,
                symbol,
                spawnStack,
                collectionTurn.Value,
                0,
                $"collection turn {collectionTurn.Value} must be after spawn turn {spawnTurn}");
        }

        var stack = spawnStack;
        var applied = 0;
        foreach (var wheel in _wheels)
        {
            var wheelCheck = ValidateWheel(wheel);
            if (!wheelCheck.IsValid)
                return wheelCheck;

            if (wheel.Symbol != symbol) continue;
            if (wheel.FireTurn < spawnTurn) continue;
            if (wheel.FireTurn >= collectionTurn.Value) continue;

            var next = Math.Min(_settings.MAX_COIN_STACK, stack + wheel.StackAdd);
            if (next != stack)
                applied++;
            stack = next;
        }

        return new ForwardStackImpact(
            ForwardStackImpactStatus.Valid,
            symbol,
            spawnStack,
            collectionTurn.Value,
            stack,
            applied,
            $"collects as stack {stack}");
    }

    private ForwardStackImpact ValidateWheel(ForwardWheelImpact wheel)
    {
        if (wheel.FireTurn <= 0
            || wheel.Symbol < 1
            || wheel.Symbol > _maxSymbol
            || _settings.IsFeat(wheel.Symbol)
            || wheel.StackValue <= 0
            || wheel.StackValue > _settings.MAX_COIN_STACK)
        {
            return Fail(
                ForwardStackImpactStatus.InvalidWheel,
                wheel.Symbol,
                wheel.StackValue,
                wheel.FireTurn,
                0,
                $"invalid wheel fireTurn={wheel.FireTurn}, symbol={wheel.Symbol}, stackValue={wheel.StackValue}");
        }

        return new ForwardStackImpact(
            ForwardStackImpactStatus.Valid,
            wheel.Symbol,
            wheel.StackValue,
            wheel.FireTurn,
            wheel.StackValue,
            0,
            "ok");
    }

    private static ForwardStackImpact Fail(
        ForwardStackImpactStatus status,
        int symbol,
        int spawnStack,
        int collectionTurn,
        int finalStack,
        string detail) =>
        new(status, symbol, spawnStack, collectionTurn, finalStack, 0, detail);
}
