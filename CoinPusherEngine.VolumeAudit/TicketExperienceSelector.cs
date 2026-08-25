namespace CoinPusherEngine.VolumeAudit;

internal sealed class AlwFixedTicketCandidate
{
    internal AlwFixedTicketCandidate(
        int seed,
        int ppsCombinationId,
        decimal cashWin,
        Ticket envelope,
        TicketSerializer.TicketDto gameData,
        IReadOnlyCollection<string> experienceTokens)
    {
        Seed = seed;
        PpsCombinationId = ppsCombinationId;
        CashWin = cashWin;
        Envelope = envelope;
        GameData = gameData;
        ExperienceTokens = experienceTokens;
    }

    internal int Seed { get; }
    internal int PpsCombinationId { get; }
    internal decimal CashWin { get; }
    internal Ticket Envelope { get; }
    internal TicketSerializer.TicketDto GameData { get; }
    internal IReadOnlyCollection<string> ExperienceTokens { get; }
}

internal sealed class TicketExperienceSelector
{
    private readonly ICustomProfileSettings _settings;
    private readonly Dictionary<string, int> _globalUsage = new(StringComparer.Ordinal);

    internal TicketExperienceSelector(ICustomProfileSettings settings)
    {
        _settings = settings;
    }

    internal IReadOnlyList<AlwFixedTicketCandidate> Select(
        IReadOnlyList<AlwFixedTicketCandidate> candidates,
        int count)
    {
        if (candidates.Count < count)
            throw new InvalidOperationException($"candidate pool has {candidates.Count} tickets, expected at least {count}");

        var remaining = candidates.ToList();
        var selected = new List<AlwFixedTicketCandidate>(count);
        var localUsage = new Dictionary<string, int>(StringComparer.Ordinal);

        while (selected.Count < count)
        {
            var choice = remaining
                .Select(candidate => new
                {
                    Candidate = candidate,
                    Score = Score(candidate, localUsage),
                })
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Candidate.Seed)
                .First();

            selected.Add(choice.Candidate);
            remaining.Remove(choice.Candidate);
            foreach (var token in choice.Candidate.ExperienceTokens)
            {
                localUsage[token] = localUsage.GetValueOrDefault(token) + 1;
                _globalUsage[token] = _globalUsage.GetValueOrDefault(token) + 1;
            }
        }

        return selected;
    }

    internal IReadOnlyCollection<string> Fingerprint(TicketSerializer.TicketDto ticket)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal)
        {
            $"spins:{ticket.WinInfo.TotalSpins}",
            $"wins:{Bucket(ticket.WinInfo.WinSymbols.Length, 1, 2, 3, 4)}",
            $"nearMissCount:{Bucket(ticket.WinInfo.NonWinSymbols.Length, 0, 1, 2, 3, 4)}",
        };

        AddPusherTokens(ticket, tokens);
        AddNearMissTokens(ticket, tokens);
        AddFeatureTokens(ticket, tokens);
        return tokens;
    }

    private double Score(
        AlwFixedTicketCandidate candidate,
        IReadOnlyDictionary<string, int> localUsage)
    {
        var score = 0.0;
        foreach (var token in candidate.ExperienceTokens)
        {
            var global = _globalUsage.GetValueOrDefault(token);
            var local = localUsage.GetValueOrDefault(token);
            score += TokenWeight(token) / (1.0 + global + (local * 2.0));
        }

        return score;
    }

    private void AddPusherTokens(TicketSerializer.TicketDto ticket, ISet<string> tokens)
    {
        var allPushers = ticket.Turns.SelectMany(turn => turn.Pushers).ToArray();
        foreach (var value in allPushers.Select(pusher => pusher.PushValue).Distinct())
            tokens.Add($"pushValue:{value}");

        foreach (var turn in ticket.Turns)
        {
            var total = turn.Pushers.Sum(pusher => pusher.PushValue);
            tokens.Add(total <= 8 ? "pushLoad:low" : total <= 15 ? "pushLoad:mid" : "pushLoad:high");
            if (turn.Pushers.Select(pusher => pusher.PushValue).Distinct().Count() >= 4)
                tokens.Add("pushMix:fourPlusValues");
        }

        if (allPushers.Any(pusher => pusher.FeatureId == _settings.F_FLUSH_ID))
            tokens.Add("feature:FLUSH");
        if (ticket.Turns.Any(turn => turn.Pushers.Count(pusher => pusher.FeatureId == _settings.F_FLUSH_ID) >= 2))
            tokens.Add("sameTurn:multiFLUSH");
    }

    private static void AddNearMissTokens(TicketSerializer.TicketDto ticket, ISet<string> tokens)
    {
        if (ticket.WinInfo.NonWinSymbols.Length == 0)
        {
            tokens.Add("nearMiss:none");
            return;
        }

        foreach (var nearMiss in ticket.WinInfo.NonWinSymbols)
        {
            var target = nearMiss.MinTarget;
            tokens.Add(target <= 5
                ? "nearMiss:low"
                : target <= 15
                    ? "nearMiss:mid"
                    : "nearMiss:high");
            if (nearMiss.PrizeTier.HasValue)
                tokens.Add("nearMiss:prizeUpgrade");
        }
    }

    private void AddFeatureTokens(TicketSerializer.TicketDto ticket, ISet<string> tokens)
    {
        var logicalFeatures = new List<(int Turn, TicketSerializer.FeatureDto Feature)>();
        for (var turnIndex = 0; turnIndex < ticket.Turns.Length; turnIndex++)
        {
            foreach (var feature in ticket.Turns[turnIndex].Spawns
                         .Where(spawn => spawn.Feature != null)
                         .Select(spawn => spawn.Feature!))
            {
                Flatten(feature, turnIndex + 1, logicalFeatures);
            }

            var physicalCounts = ticket.Turns[turnIndex].Spawns
                .Where(spawn => spawn.Feature != null)
                .GroupBy(spawn => spawn.Feature!.FeatureId);
            foreach (var group in physicalCounts.Where(group => group.Count() >= 2))
                tokens.Add($"sameTurn:multi{FeatureName(group.Key)}");
        }

        if (logicalFeatures.Count == 0 && !tokens.Contains("feature:FLUSH"))
            tokens.Add("features:none");

        foreach (var group in logicalFeatures.GroupBy(item => item.Feature.FeatureId))
        {
            var name = FeatureName(group.Key);
            tokens.Add($"feature:{name}");
            tokens.Add($"featureCount:{name}:{Bucket(group.Count(), 1, 2, 3)}");
            if (group.Any(item => item.Turn <= _settings.BASE_SPINS))
                tokens.Add($"featurePhase:{name}:base");
            if (group.Any(item => item.Turn > _settings.BASE_SPINS))
                tokens.Add($"featurePhase:{name}:extra");
        }

        var featureNames = logicalFeatures.Select(item => FeatureName(item.Feature.FeatureId))
            .Concat(tokens.Contains("feature:FLUSH") ? new[] { "FLUSH" } : Array.Empty<string>())
            .Distinct()
            .OrderBy(name => name)
            .ToArray();
        tokens.Add(featureNames.Length == 0
            ? "featureMix:none"
            : $"featureMix:{string.Join("+", featureNames)}");

        var wheels = logicalFeatures
            .Where(item => item.Feature.FeatureId == _settings.F_WHEEL)
            .Select(item => item.Feature)
            .ToArray();
        foreach (var stack in wheels.Where(wheel => wheel.WheelStackValue.HasValue)
                     .Select(wheel => wheel.WheelStackValue!.Value).Distinct())
            tokens.Add($"wheelStack:{stack}");
        if (wheels.GroupBy(wheel => wheel.WheelSymbolId).Any(group => group.Count() >= 2))
            tokens.Add("wheelTarget:repeatSymbol");
        if (wheels.Select(wheel => wheel.WheelSymbolId).Distinct().Count() >= 2)
            tokens.Add("wheelTarget:multipleSymbols");

        if (ticket.Turns.SelectMany(turn => turn.Spawns)
            .Any(spawn => spawn.Feature == null && spawn.Stack.GetValueOrDefault(1) > 1))
        {
            tokens.Add("wheelState:stackedSpawn");
        }

        foreach (var item in logicalFeatures)
        {
            foreach (var child in item.Feature.ReTrigger ?? Array.Empty<TicketSerializer.FeatureDto>())
                tokens.Add($"retrigger:{FeatureName(item.Feature.FeatureId)}->{FeatureName(child.FeatureId)}");
        }
    }

    private static void Flatten(
        TicketSerializer.FeatureDto feature,
        int turn,
        ICollection<(int Turn, TicketSerializer.FeatureDto Feature)> output)
    {
        output.Add((turn, feature));
        foreach (var child in feature.ReTrigger ?? Array.Empty<TicketSerializer.FeatureDto>())
            Flatten(child, turn, output);
    }

    private string FeatureName(int id) =>
        id == _settings.F_WHEEL ? "WHEEL"
        : id == _settings.F_XSPIN ? "EXTRA_GO"
        : id == _settings.F_PRUP ? "PRIZE_UPGRADE"
        : id == _settings.F_FLUSH_ID ? "FLUSH"
        : $"FEATURE_{id}";

    private static string Bucket(int value, params int[] boundaries)
    {
        foreach (var boundary in boundaries)
        {
            if (value <= boundary)
                return boundary.ToString();
        }

        return $"{boundaries[^1]}+";
    }

    private static double TokenWeight(string token) =>
        token.StartsWith("retrigger:", StringComparison.Ordinal) ? 18.0
        : token.StartsWith("sameTurn:", StringComparison.Ordinal) ? 14.0
        : token == "wheelTarget:multipleSymbols" ? 12.0
        : token == "wheelTarget:repeatSymbol" ? 12.0
        : token == "wheelState:stackedSpawn" ? 10.0
        : token.StartsWith("featureMix:", StringComparison.Ordinal) ? 9.0
        : token.StartsWith("featurePhase:", StringComparison.Ordinal) ? 7.0
        : token.StartsWith("wheelStack:", StringComparison.Ordinal) ? 7.0
        : token.StartsWith("nearMiss:", StringComparison.Ordinal) ? 6.0
        : token.StartsWith("pushLoad:", StringComparison.Ordinal) ? 5.0
        : token.StartsWith("feature:", StringComparison.Ordinal) ? 4.0
        : 2.0;
}
