using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;

namespace CoinPusherEngine.VolumeAudit;

internal sealed class AlwFixedTicketExportResult
{
    internal AlwFixedTicketExportResult(
        string outputDirectory,
        string zipPath,
        int ticketCount,
        int generatedCandidateCount,
        int rejectedAttemptCount)
    {
        OutputDirectory = outputDirectory;
        ZipPath = zipPath;
        TicketCount = ticketCount;
        GeneratedCandidateCount = generatedCandidateCount;
        RejectedAttemptCount = rejectedAttemptCount;
    }

    internal string OutputDirectory { get; }
    internal string ZipPath { get; }
    internal int TicketCount { get; }
    internal int GeneratedCandidateCount { get; }
    internal int RejectedAttemptCount { get; }
}

internal sealed class AlwFixedTicketExporter
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
        DefaultValueHandling = DefaultValueHandling.Include,
    };

    private readonly ICustomProfileSettings _settings;
    private readonly CoinPusherTicketCheckerPlugin _checker;
    private readonly CoinPusherTicketGenerationValidator _generationValidator = new();
    private readonly TicketExperienceSelector _selector;

    internal AlwFixedTicketExporter(ICustomProfileSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _checker = new CoinPusherTicketCheckerPlugin(_settings);
        _selector = new TicketExperienceSelector(_settings);
    }

    internal AlwFixedTicketExportResult Export(string outputDirectory, int baseSeed)
    {
        var output = ValidateOutputPath(outputDirectory);
        var zipPath = output + ".zip";
        if (Directory.Exists(output) || File.Exists(zipPath))
        {
            throw new InvalidOperationException(
                $"output already exists; choose a new path or remove the previous export: {output}");
        }

        var catalog = AlwFixedTicketCatalog.Build(_settings);
        var selectedByTier = new Dictionary<int, IReadOnlyList<AlwFixedTicketCandidate>>();
        var attemptsByTier = new Dictionary<int, int>();
        var generatedCandidateCount = 0;
        var rejectedAttemptCount = 0;

        foreach (var tier in catalog.OrderBy(item => item.IsNoWin ? int.MaxValue : item.TierNumber))
        {
            var poolTarget = Math.Max(tier.TicketCount * 3, tier.TicketCount + 24);
            var pool = BuildCandidatePool(
                tier,
                poolTarget,
                baseSeed,
                out var attempts,
                out var rejected);
            var selected = _selector.Select(pool, tier.TicketCount);

            selectedByTier[tier.TierNumber] = selected;
            attemptsByTier[tier.TierNumber] = attempts;
            generatedCandidateCount += pool.Count;
            rejectedAttemptCount += rejected;
        }

        Directory.CreateDirectory(output);
        foreach (var tier in catalog)
        {
            var tierDirectory = Path.Combine(output, $"Tier{tier.TierNumber}");
            Directory.CreateDirectory(tierDirectory);
            var wrapper = new FixedTicketWrapper
            {
                Tickets = selectedByTier[tier.TierNumber]
                    .Select(candidate => candidate.Envelope)
                    .ToArray(),
            };
            WriteJson(Path.Combine(tierDirectory, "tickets.json"), wrapper);
        }

        var allSelected = selectedByTier.Values.SelectMany(items => items).ToArray();
        WriteJson(
            Path.Combine(output, "manifest.json"),
            BuildManifest(catalog, selectedByTier, attemptsByTier, allSelected, baseSeed));

        VerifyExport(output);
        ZipFile.CreateFromDirectory(output, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
        return new AlwFixedTicketExportResult(
            output,
            zipPath,
            allSelected.Length,
            generatedCandidateCount,
            rejectedAttemptCount);
    }

    internal void VerifyExport(string outputDirectory)
    {
        var output = ValidateOutputPath(outputDirectory);
        if (!Directory.Exists(output))
            throw new DirectoryNotFoundException(output);

        var catalog = AlwFixedTicketCatalog.Build(_settings);
        var total = 0;
        foreach (var tier in catalog)
        {
            var path = Path.Combine(output, $"Tier{tier.TierNumber}", "tickets.json");
            if (!File.Exists(path))
                throw new InvalidOperationException($"Tier{tier.TierNumber} tickets.json is missing");

            var wrapper = JsonConvert.DeserializeObject<FixedTicketWrapper>(File.ReadAllText(path));
            if (wrapper?.Tickets == null || wrapper.Tickets.Length != tier.TicketCount)
            {
                throw new InvalidOperationException(
                    $"Tier{tier.TierNumber} contains {wrapper?.Tickets?.Length ?? 0} tickets, " +
                    $"expected {tier.TicketCount}");
            }

            foreach (var ticket in wrapper.Tickets)
            {
                var publicGame = ticket.Game?.PublicState?.Game;
                var parameters = publicGame?.Parameters;
                var gameData = publicGame?.GameData;
                var privateState = ticket.Game?.PrivateState;
                if (parameters == null || gameData == null || privateState == null)
                    throw new InvalidOperationException($"Tier{tier.TierNumber} contains an incomplete framework envelope");
                if (parameters.TierNumber != tier.TierNumber || privateState.TierNumber != tier.TierNumber)
                {
                    throw new InvalidOperationException(
                        $"Tier{tier.TierNumber} envelope declares public/private tiers " +
                        $"{parameters.TierNumber}/{privateState.TierNumber}");
                }

                var exactPpsFailure = ValidateExactPpsTicket(tier, gameData, parameters.CashWin);
                if (exactPpsFailure != null)
                    throw new InvalidOperationException($"Tier{tier.TierNumber}: {exactPpsFailure}");

                var checkerReport = _checker.CheckTicket(ticket);
                if (!checkerReport.IsValid)
                {
                    var failures = string.Join(" | ", checkerReport.Checks
                        .Where(check => check.Result == TicketChecker.Status.Fail)
                        .Select(check => $"{check.Category}/{check.Name}: {check.Detail}"));
                    throw new InvalidOperationException($"Tier{tier.TierNumber} TicketChecker failed: {failures}");
                }

                total++;
            }
        }

        if (total != 400)
            throw new InvalidOperationException($"serialized catalog contains {total} tickets, expected 400");
    }

    private List<AlwFixedTicketCandidate> BuildCandidatePool(
        AlwFixedTicketTier tier,
        int poolTarget,
        int baseSeed,
        out int attempts,
        out int rejected)
    {
        var pool = new List<AlwFixedTicketCandidate>(poolTarget);
        var uniqueTickets = new HashSet<string>(StringComparer.Ordinal);
        var maxAttempts = Math.Max(2000, poolTarget * 120);
        rejected = 0;

        for (attempts = 1; attempts <= maxAttempts && pool.Count < poolTarget; attempts++)
        {
            var seed = CandidateSeed(baseSeed, tier.TierNumber, attempts);
            var prizes = tier.IsNoWin
                ? Array.Empty<decimal>()
                : new[] { PpsRow(tier.PpsCombinationId).TotalPrize };
            var generated = new ForwardTicketGenerator(_settings, seed).Generate(prizes);
            if (!generated.IsValid
                || generated.Ticket == null
                || generated.Build?.MathInput?.Bundle?.Input == null)
            {
                rejected++;
                continue;
            }

            var input = generated.Build.MathInput.Bundle.Input;
            if (tier.IsNoWin
                ? input.PpsCombinationId.HasValue
                : input.PpsCombinationId != tier.PpsCombinationId)
            {
                continue;
            }

            var generationCheck = _generationValidator.Validate(prizes, generated);
            if (!generationCheck.IsValid)
            {
                rejected++;
                continue;
            }

            var cashWin = tier.IsNoWin ? 0m : PpsRow(tier.PpsCombinationId).TotalPrize;
            var exactPpsFailure = ValidateExactPpsTicket(tier, generated.Ticket, cashWin);
            if (exactPpsFailure != null)
                throw new InvalidOperationException($"Tier{tier.TierNumber} seed {seed}: {exactPpsFailure}");

            var envelope = FrameworkTicketFactory.Create(
                generated.Ticket,
                tier.TierNumber,
                seed,
                cashWin);
            var checkerReport = _checker.CheckTicket(envelope);
            if (!checkerReport.IsValid)
            {
                rejected++;
                continue;
            }

            var identity = JsonConvert.SerializeObject(generated.Ticket, Formatting.None);
            if (!uniqueTickets.Add(identity))
                continue;

            pool.Add(new AlwFixedTicketCandidate(
                seed,
                tier.PpsCombinationId,
                cashWin,
                envelope,
                generated.Ticket,
                _selector.Fingerprint(generated.Ticket)));
        }

        if (pool.Count < poolTarget)
        {
            throw new InvalidOperationException(
                $"Tier{tier.TierNumber}: found {pool.Count}/{poolTarget} valid exact-PPS candidates " +
                $"after {maxAttempts} attempts");
        }

        attempts--;
        return pool;
    }

    private string? ValidateExactPpsTicket(
        AlwFixedTicketTier tier,
        TicketSerializer.TicketDto ticket,
        decimal cashWin)
    {
        if (tier.IsNoWin)
        {
            if (ticket.WinInfo.WinSymbols.Length != 0 || ticket.WinInfo.PrizeTiers.Length != 0)
                return "no-win tier contains winning symbols or prize tiers";
            return cashWin == 0m ? null : $"no-win CashWin is {cashWin}";
        }

        var row = PpsRow(tier.PpsCombinationId);
        if (cashWin != row.TotalPrize)
            return $"CashWin {cashWin} differs from PPS total {row.TotalPrize}";

        var actualWins = ticket.WinInfo.WinSymbols.OrderBy(win => win.Id).ToArray();
        var expectedComponents = row.Components.OrderBy(component => component.SymbolId).ToArray();
        if (!actualWins.Select(win => win.Id).SequenceEqual(expectedComponents.Select(component => component.SymbolId)))
        {
            return $"winning symbols [{string.Join(",", actualWins.Select(win => win.Id))}] differ from " +
                $"PPS row [{string.Join(",", expectedComponents.Select(component => component.SymbolId))}]";
        }

        var declaredTiers = ticket.WinInfo.PrizeTiers
            .ToDictionary(item => item.SymId, item => item.Tier);
        foreach (var component in expectedComponents)
        {
            var expectedTarget = _settings.PrizeLadderRows[component.SymbolId - 1].Target;
            var actualTarget = actualWins.Single(win => win.Id == component.SymbolId).Target;
            if (actualTarget != expectedTarget)
                return $"symbol {component.SymbolId} target {actualTarget}, expected {expectedTarget}";

            var actualTier = declaredTiers.GetValueOrDefault(component.SymbolId);
            if (actualTier != component.Tier)
                return $"symbol {component.SymbolId} tier {actualTier}, expected PPS tier {component.Tier}";
        }

        var resolvedCashWin = expectedComponents.Sum(component =>
            _settings.PrizeLadderRows[component.SymbolId - 1].Tiers[component.Tier]);
        return resolvedCashWin == row.TotalPrize
            ? null
            : $"resolved payout {resolvedCashWin}, expected {row.TotalPrize}";
    }

    private object BuildManifest(
        IReadOnlyList<AlwFixedTicketTier> catalog,
        IReadOnlyDictionary<int, IReadOnlyList<AlwFixedTicketCandidate>> selectedByTier,
        IReadOnlyDictionary<int, int> attemptsByTier,
        IReadOnlyList<AlwFixedTicketCandidate> allSelected,
        int baseSeed)
    {
        var experienceCounts = allSelected
            .SelectMany(candidate => candidate.ExperienceTokens)
            .GroupBy(token => token)
            .OrderBy(group => group.Key)
            .ToDictionary(group => group.Key, group => group.Count());

        return new
        {
            Market = "ALW Money Machine",
            BaseSeed = baseSeed,
            TotalTickets = allSelected.Count,
            WinningTickets = allSelected.Count(candidate => candidate.CashWin > 0m),
            NoWinTickets = allSelected.Count(candidate => candidate.CashWin == 0m),
            EveryTicketPassedGenerationValidator = true,
            EveryTicketPassedIndependentTicketChecker = true,
            Tiers = catalog.Select(tier => new
            {
                TierNumber = tier.TierNumber,
                PpsCombinationId = tier.IsNoWin ? (int?)null : tier.PpsCombinationId,
                CashWin = tier.IsNoWin ? 0m : PpsRow(tier.PpsCombinationId).TotalPrize,
                RequiredCount = tier.TicketCount,
                GeneratedCount = selectedByTier[tier.TierNumber].Count,
                CandidateAttempts = attemptsByTier[tier.TierNumber],
                Seeds = selectedByTier[tier.TierNumber].Select(candidate => candidate.Seed).ToArray(),
                PpsComponents = tier.IsNoWin
                    ? Array.Empty<object>()
                    : PpsRow(tier.PpsCombinationId).Components
                        .Select(component => (object)new
                        {
                            component.SymbolId,
                            component.Tier,
                            Prize = _settings.PrizeLadderRows[component.SymbolId - 1].Tiers[component.Tier],
                        })
                        .ToArray(),
            }).ToArray(),
            ExperienceCoverage = experienceCounts,
        };
    }

    private PpsPrizeCombination PpsRow(int id) =>
        _settings.PpsCombinations.Single(row => row.Id == id);

    private static int CandidateSeed(int baseSeed, int tierNumber, int attempt)
    {
        unchecked
        {
            var value = baseSeed;
            value = (value * 397) ^ tierNumber;
            value = (value * 397) ^ attempt;
            value ^= 0x5f356495;
            return value == int.MinValue ? 0 : Math.Abs(value);
        }
    }

    private static string ValidateOutputPath(string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("output directory is required", nameof(outputDirectory));

        var workspace = Path.GetFullPath(Directory.GetCurrentDirectory())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var output = Path.GetFullPath(outputDirectory);
        if (!output.StartsWith(workspace, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"output must stay inside workspace {workspace}");

        return output;
    }

    private static void WriteJson(string path, object value)
    {
        var json = JsonConvert.SerializeObject(value, JsonSettings);
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private sealed class FixedTicketWrapper
    {
        [JsonProperty("tickets")]
        public Ticket[] Tickets { get; init; } = Array.Empty<Ticket>();
    }
}
