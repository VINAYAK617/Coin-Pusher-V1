namespace CoinPusherEngine;

internal enum ForwardObjectiveStatus
{
    Valid,
    MissingInput,
    MissingPrizeLadder,
    InvalidMaxSymbol,
    InvalidWinSymbol,
    InvalidWinTarget,
    InvalidPrizeTier,
    InvalidPrizeValue,
    InvalidNonWinSymbol,
    NonWinOverlapsWin,
    InvalidNonWinTarget,
    InvalidNonWinPrizeTier,
}

internal sealed class ForwardObjectives
{
    internal ForwardObjectives(
        IReadOnlyDictionary<int, int> winTargets,
        IReadOnlyList<int> winSymbols,
        IReadOnlyList<int> fillSymbols,
        IReadOnlyDictionary<int, int> nearMissTargets,
        IReadOnlyDictionary<int, int> symbolCaps,
        IReadOnlyDictionary<int, int> prizeTiers,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, decimal>> prizeValues,
        IReadOnlyDictionary<int, int> nonWinPrizeTiers,
        int maxSymbol,
        bool isNoWin,
        int topPrizeSymbol,
        int? winCompletionTurn)
    {
        WinTargets = winTargets;
        WinSymbols = winSymbols;
        FillSymbols = fillSymbols;
        NearMissTargets = nearMissTargets;
        SymbolCaps = symbolCaps;
        PrizeTiers = prizeTiers;
        PrizeValues = prizeValues;
        NonWinPrizeTiers = nonWinPrizeTiers;
        MaxSymbol = maxSymbol;
        IsNoWin = isNoWin;
        TopPrizeSymbol = topPrizeSymbol;
        WinCompletionTurn = winCompletionTurn;
    }

    internal IReadOnlyDictionary<int, int> WinTargets { get; }
    internal IReadOnlyList<int> WinSymbols { get; }
    internal IReadOnlyList<int> FillSymbols { get; }
    internal IReadOnlyDictionary<int, int> NearMissTargets { get; }
    internal IReadOnlyDictionary<int, int> SymbolCaps { get; }
    internal IReadOnlyDictionary<int, int> PrizeTiers { get; }
    internal IReadOnlyDictionary<int, IReadOnlyDictionary<int, decimal>> PrizeValues { get; }
    internal IReadOnlyDictionary<int, int> NonWinPrizeTiers { get; }
    internal int MaxSymbol { get; }
    internal bool IsNoWin { get; }
    internal int TopPrizeSymbol { get; }
    internal int? WinCompletionTurn { get; }
}

internal sealed class ForwardObjectiveResult
{
    internal ForwardObjectiveResult(
        ForwardObjectiveStatus status,
        string detail,
        ForwardObjectives? objectives)
    {
        Status = status;
        Detail = detail;
        Objectives = objectives;
    }

    internal ForwardObjectiveStatus Status { get; }
    internal string Detail { get; }
    internal ForwardObjectives? Objectives { get; }
    internal bool IsValid => Status == ForwardObjectiveStatus.Valid;
}

internal sealed class ForwardObjectivePlanner
{
    internal ForwardObjectiveResult Resolve(MathInput? input, int seed)
    {
        if (input == null)
            return Fail(ForwardObjectiveStatus.MissingInput, "MathInput is null");
        if (Settings.PrizeLadderRows == null || Settings.PrizeLadderRows.Count == 0)
            return Fail(ForwardObjectiveStatus.MissingPrizeLadder, "settings.PrizeLadderRows is empty");
        if (input.Targets == null)
            return Fail(ForwardObjectiveStatus.MissingInput, "MathInput.Targets is null");
        if (input.MaxSym < 1)
            return Fail(ForwardObjectiveStatus.InvalidMaxSymbol, $"MaxSym={input.MaxSym} must be positive");

        var maxSymbol = Math.Min(input.MaxSym, Settings.PrizeLadderRows.Count);
        var symbolCaps = Enumerable.Range(1, maxSymbol)
            .ToDictionary(symbol => symbol, symbol => Settings.SymbolFillCap(symbol));
        var winTargets = input.Targets
            .OrderBy(kv => kv.Key)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        foreach (var (symbol, target) in winTargets)
        {
            if (!ValidSymbol(symbol, maxSymbol))
                return Fail(ForwardObjectiveStatus.InvalidWinSymbol, $"winning symbol {symbol} outside 1..{maxSymbol}");
            if (target <= 0)
                return Fail(ForwardObjectiveStatus.InvalidWinTarget, $"winning symbol {symbol} target {target} must be positive");
            if (target != symbolCaps[symbol])
            {
                return Fail(
                    ForwardObjectiveStatus.InvalidWinTarget,
                    $"winning symbol {symbol} target {target} must match configured ladder target {symbolCaps[symbol]}");
            }
        }

        var winSymbols = winTargets.Keys.OrderBy(symbol => symbol).ToArray();
        var fillSymbols = Enumerable.Range(1, maxSymbol)
            .Where(symbol => !winTargets.ContainsKey(symbol))
            .ToArray();

        if (input.PrizeValues != null)
        {
            var nullTierMapSymbol = input.PrizeValues
                .Where(kv => kv.Value == null)
                .Select(kv => (int?)kv.Key)
                .FirstOrDefault();
            if (nullTierMapSymbol.HasValue)
                return Fail(ForwardObjectiveStatus.InvalidPrizeValue, $"PrizeValues symbol {nullTierMapSymbol.Value} has a null tier map");
        }

        var prizeTiers = input.PrizeTiers?.ToDictionary(kv => kv.Key, kv => kv.Value)
            ?? new Dictionary<int, int>();
        var prizeValues = ClonePrizeValues(input.PrizeValues);
        foreach (var (symbol, tier) in prizeTiers)
        {
            if (!winTargets.ContainsKey(symbol) || !ValidSymbol(symbol, maxSymbol) || tier < 0)
            {
                return Fail(
                    ForwardObjectiveStatus.InvalidPrizeTier,
                    $"PrizeTiers symbol={symbol}, tier={tier} must refer to a winning symbol and non-negative tier");
            }

            if (prizeValues.Count > 0
                && (!prizeValues.TryGetValue(symbol, out var tierValues) || !tierValues.ContainsKey(tier)))
            {
                return Fail(
                    ForwardObjectiveStatus.InvalidPrizeTier,
                    $"PrizeTiers symbol={symbol}, tier={tier} has no configured prize value");
            }
        }

        foreach (var (symbol, tiers) in prizeValues)
        {
            if (!ValidSymbol(symbol, maxSymbol))
                return Fail(ForwardObjectiveStatus.InvalidPrizeValue, $"PrizeValues symbol {symbol} outside 1..{maxSymbol}");
            foreach (var (tier, value) in tiers)
            {
                if (tier < 0 || value < 0)
                    return Fail(ForwardObjectiveStatus.InvalidPrizeValue, $"PrizeValues symbol={symbol}, tier={tier}, value={value} invalid");
            }
        }

        var rng = new Random(seed);
        var nearMissTargets = ResolveNearMissTargets(input, fillSymbols, winTargets, symbolCaps, rng, maxSymbol);
        if (!nearMissTargets.IsValid)
            return nearMissTargets.Result!;

        var nonWinPrizeTiers = input.NonWinPrizeTiers?.ToDictionary(kv => kv.Key, kv => kv.Value)
            ?? new Dictionary<int, int>();
        foreach (var (symbol, tier) in nonWinPrizeTiers)
        {
            if (!ValidSymbol(symbol, maxSymbol))
                return Fail(ForwardObjectiveStatus.InvalidNonWinSymbol, $"NonWinPrizeTiers symbol {symbol} outside 1..{maxSymbol}");
            if (winTargets.ContainsKey(symbol))
                return Fail(ForwardObjectiveStatus.NonWinOverlapsWin, $"NonWinPrizeTiers symbol {symbol} is also a win symbol");
            if (!nearMissTargets.Targets.ContainsKey(symbol))
                return Fail(ForwardObjectiveStatus.InvalidNonWinPrizeTier, $"NonWinPrizeTiers symbol {symbol} has no near-miss target");
            if (tier <= 0)
                return Fail(ForwardObjectiveStatus.InvalidNonWinPrizeTier, $"NonWinPrizeTiers symbol {symbol} tier {tier} must be positive");
            if (!prizeValues.TryGetValue(symbol, out var tierValues) || !tierValues.ContainsKey(tier))
            {
                return Fail(
                    ForwardObjectiveStatus.InvalidNonWinPrizeTier,
                    $"NonWinPrizeTiers symbol {symbol} tier {tier} has no configured prize value");
            }
        }

        var objectives = new ForwardObjectives(
            winTargets,
            winSymbols,
            fillSymbols,
            nearMissTargets.Targets,
            symbolCaps,
            prizeTiers,
            prizeValues,
            nonWinPrizeTiers,
            maxSymbol,
            winTargets.Count == 0,
            TopPrizeSymbol(prizeValues),
            input.WinCompletionTurn);

        return new ForwardObjectiveResult(ForwardObjectiveStatus.Valid, "ok", objectives);
    }

    private NearMissResolveResult ResolveNearMissTargets(
        MathInput input,
        IReadOnlyList<int> fillSymbols,
        IReadOnlyDictionary<int, int> winTargets,
        IReadOnlyDictionary<int, int> symbolCaps,
        Random rng,
        int maxSymbol)
    {
        if (input.NonWinTargets != null)
        {
            var provided = new Dictionary<int, int>();
            foreach (var (symbol, target) in input.NonWinTargets)
            {
                if (!ValidSymbol(symbol, maxSymbol))
                    return NearMissResolveResult.Fail(Fail(ForwardObjectiveStatus.InvalidNonWinSymbol, $"NonWinTargets symbol {symbol} outside 1..{maxSymbol}"));
                if (winTargets.ContainsKey(symbol))
                    return NearMissResolveResult.Fail(Fail(ForwardObjectiveStatus.NonWinOverlapsWin, $"NonWinTargets symbol {symbol} is also a win symbol"));

                var cap = symbolCaps[symbol];
                if (target < Settings.NONWIN_MIN_TARGET || target >= cap)
                {
                    return NearMissResolveResult.Fail(Fail(
                        ForwardObjectiveStatus.InvalidNonWinTarget,
                        $"NonWinTargets symbol {symbol} target {target} must be in {Settings.NONWIN_MIN_TARGET}..{cap - 1}"));
                }

                provided[symbol] = target;
            }

            return NearMissResolveResult.Ok(provided);
        }

        var profile = PickNearMissProfile(rng);
        if (profile.MaxSymbols <= 0 || profile.Max <= 0)
            return NearMissResolveResult.Ok(new Dictionary<int, int>());

        var minTarget = Math.Max(Settings.NONWIN_MIN_TARGET, profile.Min);
        var eligible = fillSymbols
            .Where(symbol => symbolCaps[symbol] > minTarget)
            .ToArray();
        if (eligible.Length == 0)
            return NearMissResolveResult.Ok(new Dictionary<int, int>());

        var count = PickNearMissCount(input, eligible.Length, profile.MaxSymbols, rng);
        if (count <= 0)
            return NearMissResolveResult.Ok(new Dictionary<int, int>());

        var targets = new Dictionary<int, int>();
        foreach (var symbol in PickNearMissSymbols(eligible, count, rng))
        {
            var maxTarget = Math.Min(profile.Max, symbolCaps[symbol] - 1);
            if (maxTarget < minTarget) continue;
            targets[symbol] = rng.Next(minTarget, maxTarget + 1);
        }

        return NearMissResolveResult.Ok(targets);
    }

    private (double P, int Min, int Max, int MaxSymbols) PickNearMissProfile(Random rng)
    {
        var profiles = Settings.NonWinTargetProfiles ?? Array.Empty<(double P, int Min, int Max, int MaxSymbols)>();
        var total = profiles.Sum(profile => Math.Max(0.0, profile.P));
        if (total <= 0) return (0, 0, 0, 0);

        var roll = rng.NextDouble() * total;
        var acc = 0.0;
        foreach (var profile in profiles)
        {
            acc += Math.Max(0.0, profile.P);
            if (roll <= acc) return profile;
        }

        return profiles[^1];
    }

    private int PickNearMissCount(
        MathInput input,
        int eligibleCount,
        int profileMaxSymbols,
        Random rng)
    {
        var maxSymbols = Math.Min(eligibleCount, Math.Min(profileMaxSymbols, NearMissCapByWinCount(input)));
        if (maxSymbols <= 0) return 0;

        var min = input.Targets.Count switch
        {
            0 when maxSymbols >= 3 => 3,
            1 when maxSymbols >= 5 => 4,
            1 when maxSymbols >= 3 => 3,
            2 when maxSymbols >= 4 => 2,
            3 when maxSymbols >= 3 => 2,
            _ => 1,
        };
        min = Math.Min(min, maxSymbols);

        var weights = (Settings.NonWinCountWeights ?? Array.Empty<double>())
            .Select((weight, index) => new { Count = index + 1, Weight = Math.Max(0.0, weight) })
            .Where(item => item.Count >= min && item.Count <= maxSymbols && item.Weight > 0)
            .ToArray();
        if (weights.Length == 0) return min;

        var total = weights.Sum(item => item.Weight);
        var roll = rng.NextDouble() * total;
        var acc = 0.0;
        foreach (var item in weights)
        {
            acc += item.Weight;
            if (roll <= acc) return item.Count;
        }

        return weights[^1].Count;
    }

    private static int NearMissCapByWinCount(MathInput input)
    {
        if (input.Targets.Count >= 4 || input.Targets.Values.Sum() >= 80 || (input.Required?.GetValueOrDefault("PRIZE_UPGRADE") ?? 0) >= 4)
            return 1;

        return input.Targets.Count switch
        {
            0 => 5,
            1 => 5,
            2 => 4,
            3 => 3,
            _ => 2,
        };
    }

    private IReadOnlyList<int> PickNearMissSymbols(
        IReadOnlyList<int> eligibleSymbols,
        int count,
        Random rng)
    {
        var available = eligibleSymbols.OrderBy(symbol => symbol).ToList();
        var picked = new List<int>();
        while (picked.Count < count && available.Count > 0)
        {
            var groups = available
                .GroupBy(symbol => NearMissBand(symbol, eligibleSymbols))
                .Select(group => new
                {
                    Band = group.Key,
                    Symbols = group.OrderBy(_ => rng.Next()).ToList(),
                    Weight = NearMissBandWeight(group.Key),
                })
                .Where(group => group.Weight > 0)
                .ToArray();
            if (groups.Length == 0) break;

            var total = groups.Sum(group => group.Weight);
            var roll = rng.NextDouble() * total;
            var acc = 0.0;
            var chosen = groups[^1];
            foreach (var group in groups)
            {
                acc += group.Weight;
                if (roll <= acc)
                {
                    chosen = group;
                    break;
                }
            }

            var symbol = chosen.Symbols[0];
            picked.Add(symbol);
            available.Remove(symbol);
        }

        return picked;
    }

    private static int NearMissBand(int symbol, IReadOnlyList<int> orderedSymbols)
    {
        var ordered = orderedSymbols.OrderBy(s => s).ToArray();
        var index = Array.IndexOf(ordered, symbol);
        if (index < 0) return 2;

        var lowLimit = (int)Math.Ceiling(ordered.Length / 3.0);
        var midLimit = (int)Math.Ceiling(ordered.Length * 2 / 3.0);
        if (index < lowLimit) return 0;
        return index < midLimit ? 1 : 2;
    }

    private double NearMissBandWeight(int band) =>
        band switch
        {
            0 => Settings.WNonWinLow,
            1 => Settings.WNonWinMid,
            _ => Settings.WNonWinHigh,
        };

    private bool ValidSymbol(int symbol, int maxSymbol) =>
        symbol >= 1 && symbol <= maxSymbol && !Settings.IsFeat(symbol);

    private static Dictionary<int, IReadOnlyDictionary<int, decimal>> ClonePrizeValues(
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, decimal>>? values) =>
        values?.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyDictionary<int, decimal>)kv.Value.ToDictionary(tier => tier.Key, tier => tier.Value))
        ?? new Dictionary<int, IReadOnlyDictionary<int, decimal>>();

    private static int TopPrizeSymbol(IReadOnlyDictionary<int, IReadOnlyDictionary<int, decimal>> prizeValues)
    {
        if (prizeValues.Count == 0) return 0;
        return prizeValues
            .Select(kv => (Symbol: kv.Key, Value: kv.Value.Values.DefaultIfEmpty(0m).Max()))
            .OrderByDescending(item => item.Value)
            .ThenBy(item => item.Symbol)
            .First().Symbol;
    }

    private static ForwardObjectiveResult Fail(ForwardObjectiveStatus status, string detail) =>
        new(status, detail, null);

    private sealed class NearMissResolveResult
    {
        private NearMissResolveResult(
            Dictionary<int, int> targets,
            ForwardObjectiveResult? result)
        {
            Targets = targets;
            Result = result;
        }

        internal Dictionary<int, int> Targets { get; }
        internal ForwardObjectiveResult? Result { get; }
        internal bool IsValid => Result == null;

        internal static NearMissResolveResult Ok(Dictionary<int, int> targets) =>
            new(targets, null);

        internal static NearMissResolveResult Fail(ForwardObjectiveResult result) =>
            new(new Dictionary<int, int>(), result);
    }
}
