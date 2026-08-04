namespace CoinPusherEngine;

using Newtonsoft.Json;
using static TicketSerializer;

/// <summary>
/// Independent, from-scratch auditor for a serialized ticket. Takes ONLY the
/// public ticket JSON shape (TicketDto) — never the internal GamePlan/MathInput
/// that produced it — and re-derives every claimed number from raw replay,
/// exactly the way a real client (or an auditor handed a ticket with zero other
/// context) would have to. Deliberately does NOT call Sim.cs: this is a second,
/// independent implementation of the collect/rotate/spawn/fire mechanics, so a
/// bug shared between the production simulator and this checker can't silently
/// cancel itself out.
///
/// Every check is its own CheckItem (Pass / Fail / Warning).
///
/// ── Two real bugs were found while building this ──
///
/// 1. EXTRA_SPIN ReTrigger unwrapping (FIXED HERE, in this checker):
///    When several physical EXTRA_SPIN tokens get folded into one nested
///    ReTrigger chain for presentation (see TicketSerializer.BuildTurns), the
///    chain-start spawn's OWN ConvertToId is a PLACEHOLDER (literally the
///    EXTRA_SPIN feature id, Settings.Default.F_XSPIN) meaning "there is more chain to unwrap,
///    look inside ReTrigger" — it is NOT "no real target, fall back to filler".
///    An earlier version of this checker treated that placeholder as an invalid
///    convert target and substituted Settings.Default.F_COIN (symbol 1), which silently
///    injected a fake extra collection of whichever win symbol happened to
///    equal Settings.Default.F_COIN's value. Confirmed via direct comparison against Sim.Run
///    (the engine's own trusted simulator) across 1000 real tickets — the
///    checker was wrong, not the engine. ResolveConvert below now walks the
///    ReTrigger chain to its end to find the real eventual symbol.
///
/// 2. PRIZE_UPGRADE token presence:
///    Verifier checks collected totals, but the serialized ticket also needs
///    every declared upgrade step to appear as a visible token. Resolver now
///    fails planning when it cannot place a required feature token; check #11
///    independently verifies the public ticket still matches the declared
///    tier data.
///
/// 3. WHEEL replay:
///    The engine now serializes enough evolving-board information for this checker
///    to replay WHEEL stack residue as normal board state. Count mismatches are hard
///    failures, not warnings.
/// </summary>
public static class TicketChecker
{
    public enum Status { Pass, Fail, Warning }

    public sealed class TicketCheckResult
    {
        public List<string> Errors { get; } = new();
        public bool IsValid => Errors.Count == 0;
    }

    public sealed class CheckItem
    {
        public string Category { get; init; } = "";
        public string Name     { get; init; } = "";
        public Status Result   { get; init; }
        public string Detail   { get; init; } = "";
    }

    public sealed class Report
    {
        public List<CheckItem> Checks    { get; } = new();
        public bool   IsValid      => Checks.All(c => c.Result != Status.Fail);
        public int    PassCount    => Checks.Count(c => c.Result == Status.Pass);
        public int    FailCount    => Checks.Count(c => c.Result == Status.Fail);
        public int    WarningCount => Checks.Count(c => c.Result == Status.Warning);

        public TicketCheckResult ToResult()
        {
            var result = new TicketCheckResult();
            result.Errors.AddRange(Checks
                .Where(check => check.Result == Status.Fail)
                .Select(check => $"{check.Category}/{check.Name}: {check.Detail}"));
            return result;
        }
    }

    // ── Entry point ──────────────────────────────────────────────────────────
    public static TicketCheckResult CheckJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            var result = new TicketCheckResult();
            result.Errors.Add("Ticket JSON is empty.");
            return result;
        }

        Ticket? ticket;
        try
        {
            ticket = JsonConvert.DeserializeObject<Ticket>(json);
        }
        catch (JsonException ex)
        {
            var result = new TicketCheckResult();
            result.Errors.Add($"Ticket JSON could not be parsed: {ex.Message}");
            return result;
        }

        return CheckTicket(ticket).ToResult();
    }

    public static TicketCheckResult CheckObject(Ticket? ticket) =>
        CheckTicket(ticket).ToResult();

    public static TicketCheckResult CheckObject(TicketDto? ticket) =>
        CheckTicket(ticket).ToResult();

    public static Report CheckTicket(Ticket? ticket)
    {
        var report = new Report();
        void Add(string cat, string name, Status s, string detail) =>
            report.Checks.Add(new CheckItem { Category = cat, Name = name, Result = s, Detail = detail });

        var game = ticket?.Game?.PublicState?.Game;
        var gameData = game?.GameData;
        if (gameData == null)
        {
            Add("Ticket", "Coin Pusher GameData present", Status.Fail,
                "ticket.game.publicState.game.GameData is missing; cannot run Coin Pusher logic validation");
            return report;
        }

        Merge(report, CheckTicket(gameData));
        CheckFrameworkLogicValues(ticket, gameData, Add);
        return report;
    }

    public static Report CheckTicket(TicketDto? t)
    {
        var report = new Report();
        void Add(string cat, string name, Status s, string detail) =>
            report.Checks.Add(new CheckItem { Category = cat, Name = name, Result = s, Detail = detail });

        // ── 1. STRUCTURE ──────────────────────────────────────────────────
        if (t == null) { Add("Structure", "Ticket object present", Status.Fail, "ticket is null"); return report; }
        if (t.WinInfo == null) { Add("Structure", "WinInfo present", Status.Fail, "WinInfo is null"); return report; }
        if (t.StartingBoard == null || t.StartingBoard.Length != Settings.Default.ROWS)
        { Add("Structure", "StartingBoard rows", Status.Fail, $"expected {Settings.Default.ROWS} rows, got {t.StartingBoard?.Length ?? 0}"); return report; }
        for (int r = 0; r < Settings.Default.ROWS; r++)
            if (t.StartingBoard[r] == null || t.StartingBoard[r].Length != Settings.Default.COLS)
            { Add("Structure", $"StartingBoard row {r} width", Status.Fail, $"expected {Settings.Default.COLS} cols, got {t.StartingBoard[r]?.Length ?? 0}"); return report; }
        Add("Structure", "StartingBoard is 5x5", Status.Pass, "ok");

        if (t.Turns == null || t.Turns.Length == 0)
        { Add("Structure", "Turns present", Status.Fail, "no turns"); return report; }
        Add("Structure", "Turns present", Status.Pass, $"{t.Turns.Length} turns");

        bool structureOk = true;
        foreach (var (turn, i) in t.Turns.Select((x, i) => (x, i)))
        {
            if (turn.Pushers == null || turn.Pushers.Length != Settings.Default.COLS)
            {
                structureOk = false;
                Add("Structure", $"Turn {i + 1} pusher count", Status.Fail,
                    $"expected {Settings.Default.COLS}, got {turn.Pushers?.Length ?? 0}");
            }

            if (turn.Spawns == null)
            {
                structureOk = false;
                Add("Structure", $"Turn {i + 1} spawns present", Status.Fail, "Spawns is null");
            }
        }

        if (!structureOk)
        {
            Add("Structure", "Replay skipped", Status.Fail,
                "ticket has structural errors that would make deterministic replay unsafe");
            return report;
        }

        // ── 2. SPIN-COUNT / BASE-SPINS SANITY ──────────────────────────────
        int totalSpins = t.WinInfo.TotalSpins;
        if (totalSpins != t.Turns.Length)
            Add("SpinCount", "TotalSpins matches Turns.Length", Status.Fail,
                $"WinInfo.TotalSpins={totalSpins} but Turns.Length={t.Turns.Length}");
        else
            Add("SpinCount", "TotalSpins matches Turns.Length", Status.Pass, $"{totalSpins}");

        if (totalSpins < Settings.Default.BASE_SPINS || totalSpins > Settings.Default.MAX_SPINS)
            Add("SpinCount", "TotalSpins within fixed baseline range", Status.Fail,
                $"TotalSpins={totalSpins} outside the fixed [{Settings.Default.BASE_SPINS}..{Settings.Default.MAX_SPINS}] range " +
                $"(BaseSpins is always {Settings.Default.BASE_SPINS}; only EXTRA_SPIN can extend it, up to {Settings.Default.MAX_SPINS} total)");
        else
            Add("SpinCount", "TotalSpins within fixed baseline range", Status.Pass,
                $"{totalSpins} (base {Settings.Default.BASE_SPINS} + {totalSpins - Settings.Default.BASE_SPINS} extra)");

        CheckWinInfoSchema(t, Add);

        // ── 3. PUSHER GEOMETRY SANITY ───────────────────────────────────────
        bool pusherGeometryOk = true;
        for (int i = 0; i < t.Turns.Length; i++)
        {
            var turn = t.Turns[i];
            if (turn.Pushers == null) continue;
            for (int c = 0; c < turn.Pushers.Length; c++)
            {
                var p = turn.Pushers[c];
                bool isFlush = p.FeatureId == Settings.Default.F_FLUSH_ID;
                if (p.FeatureId.HasValue && !isFlush)
                {
                    pusherGeometryOk = false;
                    Add("Geometry", $"Turn {i + 1} col {c} pusher feature id", Status.Fail,
                        $"FeatureId={p.FeatureId} is invalid on a pusher; only FLUSH/PUSH id {Settings.Default.F_FLUSH_ID} is allowed");
                    continue;
                }

                bool valid = isFlush ? p.PushValue == Settings.Default.ROWS
                                     : p.PushValue >= Settings.Default.MIN_PUSH && p.PushValue <= Settings.Default.MAX_PUSH;
                if (!valid)
                {
                    pusherGeometryOk = false;
                    Add("Geometry", $"Turn {i + 1} col {c} push value", Status.Fail,
                        $"PushValue={p.PushValue} FeatureId={p.FeatureId} — not a valid normal push " +
                        $"({Settings.Default.MIN_PUSH}-{Settings.Default.MAX_PUSH}) or flush ({Settings.Default.ROWS})");
                }
            }
        }
        if (pusherGeometryOk) Add("Geometry", "All pusher values valid", Status.Pass, "ok");

        // ── 4. SPAWN POSITION SANITY ────────────────────────────────────────
        bool spawnPosOk = true;
        for (int i = 0; i < t.Turns.Length; i++)
        {
            var seen = new HashSet<int>();
            foreach (var sp in t.Turns[i].Spawns ?? Array.Empty<SpawnDto>())
            {
                if (sp.Pos < 0 || sp.Pos >= Settings.Default.ROWS * Settings.Default.COLS)
                {
                    spawnPosOk = false;
                    Add("Geometry", $"Turn {i + 1} spawn position range", Status.Fail,
                        $"Pos={sp.Pos} out of range 0..{Settings.Default.ROWS * Settings.Default.COLS - 1}");
                }
                else if (!seen.Add(sp.Pos))
                {
                    spawnPosOk = false;
                    Add("Geometry", $"Turn {i + 1} duplicate spawn position", Status.Fail,
                        $"Pos={sp.Pos} claimed by more than one spawn in the same turn");
                }
            }
        }
        if (spawnPosOk) Add("Geometry", "All spawn positions valid and unique per turn", Status.Pass, "ok");

        bool featureSchemaOk = CheckFeatureSchema(t, Add);
        if (!pusherGeometryOk || !spawnPosOk || !featureSchemaOk)
        {
            Add("Structure", "Replay skipped", Status.Fail,
                "ticket has geometry or feature-schema errors that would make replay unsafe");
            return report;
        }

        // ── 5. FULL REPLAY: collect / rotate / spawn / fire ────────────────
        var replay = ReplayTicket(t);

        foreach (var miss in replay.MissingCellTurns)
            Add("Replay", $"Turn {miss} fully populated", Status.Fail,
                "one or more cells were left empty after applying spawns — the engine's natural " +
                "push+rotate didn't account for every position");
        if (replay.MissingCellTurns.Count == 0)
            Add("Replay", "Every turn fully populated after spawns", Status.Pass, "ok");

        // ── 6. WIN-SYMBOL EXACT-COUNT VERIFICATION ─────────────────────────
        // Serialized spawns now carry the evolving board state closely enough that
        // WHEEL-related count mismatches are treated as real failures.
        foreach (var w in t.WinInfo.WinSymbols ?? Array.Empty<WinSymbolDto>())
        {
            int got = replay.Totals.GetValueOrDefault(w.Id);
            if (got != w.Target)
                Add("Payout", $"Win symbol {w.Id} exact count", Status.Fail,
                    $"target={w.Target} actual(replayed)={got}");
            else
                Add("Payout", $"Win symbol {w.Id} exact count", Status.Pass, $"{got}/{w.Target}");
        }

        // ── 7. NEAR-MISS BOUND VERIFICATION ────────────────────────────────
        // Near-miss symbols are checked with the same hard replay standard as wins.
        foreach (var nw in t.WinInfo.NonWinSymbols ?? Array.Empty<NonWinSymbolDto>())
        {
            int got = replay.Totals.GetValueOrDefault(nw.Id);
            bool ok = got >= nw.MinTarget && got < nw.MaxThreshold;
            Add("Payout", $"Near-miss symbol {nw.Id} within bounds", ok ? Status.Pass : Status.Fail,
                $"got={got} want [{nw.MinTarget}..{nw.MaxThreshold})");
        }

        // ── 8. ORDINARY FILLER CAP CHECK ────────────────────────────────────
        var declaredWin    = (t.WinInfo.WinSymbols ?? Array.Empty<WinSymbolDto>()).Select(w => w.Id).ToHashSet();
        var declaredNonWin = (t.WinInfo.NonWinSymbols ?? Array.Empty<NonWinSymbolDto>()).Select(w => w.Id).ToHashSet();
        bool fillerCapOk = true;
        foreach (var (sym, count) in replay.Totals)
        {
            if (declaredWin.Contains(sym) || declaredNonWin.Contains(sym) || Settings.Default.IsFeat(sym) || count == 0) continue;
            var cap = Settings.Default.SymbolFillCap(sym);
            if (count >= cap)
            {
                fillerCapOk = false;
                Add("Payout", $"Filler symbol {sym} under cap", Status.Fail, $"count={count} >= cap={cap}");
            }
        }
        if (fillerCapOk) Add("Payout", "All ordinary filler symbols under cap", Status.Pass, "ok");

        // ── 9. WHEEL CONSISTENCY ────────────────────────────────────────────
        foreach (var w in replay.WheelFireEvents)
        {
            int expectedMultiplier = w.WheelStackValue + 1;
            if (w.ActualMultiplier != expectedMultiplier)
                Add("Feature", $"WHEEL fire turn {w.Turn} sym {w.WheelSymbolId} stack value", Status.Fail,
                    $"declared WheelStackValue={w.WheelStackValue} (multiplier {expectedMultiplier}) " +
                    $"but replay applied multiplier {w.ActualMultiplier}");
            else
                Add("Feature", $"WHEEL fire turn {w.Turn} sym {w.WheelSymbolId} stack value", Status.Pass,
                    $"multiplier {w.ActualMultiplier}");
        }

        // ── 10. EXTRA_SPIN CHAIN CONSISTENCY ────────────────────────────────
        var tooDeepRetrigger = t.Turns
            .SelectMany(turn => turn.Spawns ?? Array.Empty<SpawnDto>())
            .Where(spawn => spawn.Feature != null)
            .Select(spawn => (spawn.Pos, Depth: MaxReTriggerDepth(spawn.Feature!)))
            .FirstOrDefault(item => item.Depth > 1);
        if (tooDeepRetrigger.Depth > 1)
            Add("Feature", "ReTrigger depth at most one", Status.Fail,
                $"spawn Pos={tooDeepRetrigger.Pos} has ReTrigger depth={tooDeepRetrigger.Depth}");
        else
            Add("Feature", "ReTrigger depth at most one", Status.Pass, "ok");

        int extraSpinTokenCount = CountLogicalFeatureSpawns(t, Settings.Default.F_XSPIN);
        int expectedExtras = totalSpins - Settings.Default.BASE_SPINS;
        if (extraSpinTokenCount != expectedExtras)
            Add("Feature", "EXTRA_SPIN token count matches bonus spins", Status.Fail,
                $"TotalSpins implies {expectedExtras} extra spin(s), but found {extraSpinTokenCount} " +
                "logical EXTRA_SPIN feature occurrence(s), including ReTrigger children");
        else
            Add("Feature", "EXTRA_SPIN token count matches bonus spins", Status.Pass,
                $"{extraSpinTokenCount} logical token(s) for {expectedExtras} extra spin(s)");

        var finalTurn = t.Turns[^1];
        var finalFeature = (finalTurn.Spawns ?? Array.Empty<SpawnDto>())
            .FirstOrDefault(spawn => spawn.Feature != null);
        if (finalFeature != null)
            Add("Feature", "No board feature on final spin", Status.Fail,
                $"final turn contains feature {finalFeature.Feature!.FeatureId} at Pos={finalFeature.Pos}");
        else
            Add("Feature", "No board feature on final spin", Status.Pass, "ok");

        // ── 11. PRIZE_UPGRADE TIER CONSISTENCY ──────────────────────────────
        // Declared tiers can come from EITHER WinInfo.PrizeTiers (winning
        // symbols) OR a near-miss symbol's own NonWinSymbolDto.PrizeTier field.
        // This cross-check catches any mismatch between declared tiers and the
        // public token sequence, independent of the internal collected-total
        // verifier.
        var declaredTiers = (t.WinInfo.PrizeTiers ?? Array.Empty<PrizeTierDto>())
            .ToDictionary(p => p.SymId, p => p.Tier);
        foreach (var nw in t.WinInfo.NonWinSymbols ?? Array.Empty<NonWinSymbolDto>())
            if (nw.PrizeTier.HasValue)
                declaredTiers[nw.Id] = nw.PrizeTier.Value;

        foreach (var (sym, finalTier) in replay.PrupFinalTierPerSymbol)
        {
            if (!declaredTiers.TryGetValue(sym, out int declared))
            {
                Add("Feature", $"PRIZE_UPGRADE sym {sym} declared in WinInfo", Status.Fail,
                    $"replay shows tokens reaching tier {finalTier}, but neither WinInfo.PrizeTiers nor " +
                    $"NonWinSymbols has a matching entry for sym {sym}");
                continue;
            }
            if (finalTier != declared)
                Add("Feature", $"PRIZE_UPGRADE sym {sym} final tier matches declared", Status.Fail,
                    $"declared tier={declared} but last token in replay reaches tier={finalTier} " +
                    "(serialized token sequence does not match WinInfo tier declaration)");
            else
                Add("Feature", $"PRIZE_UPGRADE sym {sym} final tier matches declared", Status.Pass,
                    $"tier {finalTier}");
        }
        foreach (var (sym, declared) in declaredTiers)
        {
            if (!replay.PrupFinalTierPerSymbol.ContainsKey(sym))
                Add("Feature", $"PRIZE_UPGRADE sym {sym} has matching tokens", Status.Fail,
                    $"WinInfo declares tier {declared} for sym {sym}, but NO PRIZE_UPGRADE tokens for " +
                    "that symbol were found anywhere in the replay");
        }

        return report;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // INDEPENDENT REPLAY — deliberately separate from Sim.cs
    // ═══════════════════════════════════════════════════════════════════════

    private static void Merge(Report target, Report source)
    {
        target.Checks.AddRange(source.Checks);
    }

    private static void CheckFrameworkLogicValues(
        Ticket? ticket,
        TicketDto gameData,
        Action<string, string, Status, string> add)
    {
        var parameters = ticket?.Game?.PublicState?.Game?.Parameters;
        if (parameters == null)
        {
            add("Framework", "Parameters available for payout checks", Status.Warning,
                "Parameters is missing, so CashWin/Stake/IsWinner consistency could not be checked");
            return;
        }

        if (parameters.Stake <= 0m)
        {
            add("Framework", "Stake positive", Status.Fail,
                $"Parameters.Stake must be positive, found {parameters.Stake}");
        }
        else
        {
            var expectedMultiplier = parameters.CashWin / parameters.Stake;
            if (parameters.StakeMultiplier != expectedMultiplier)
                add("Framework", "StakeMultiplier matches CashWin / Stake", Status.Fail,
                    $"StakeMultiplier={parameters.StakeMultiplier}, expected {expectedMultiplier} from CashWin={parameters.CashWin} / Stake={parameters.Stake}");
            else
                add("Framework", "StakeMultiplier matches CashWin / Stake", Status.Pass,
                    $"{parameters.StakeMultiplier}");
        }

        var expectedWinner = parameters.CashWin > 0m;
        if (parameters.IsWinner != expectedWinner)
            add("Framework", "IsWinner matches CashWin", Status.Fail,
                $"IsWinner={parameters.IsWinner}, but CashWin={parameters.CashWin}");
        else
            add("Framework", "IsWinner matches CashWin", Status.Pass,
                $"{parameters.IsWinner}");

        var privateState = ticket?.Game?.PrivateState;
        if (privateState != null)
        {
            if (privateState.PendingCashWin != parameters.CashWin)
                add("Framework", "PendingCashWin matches CashWin", Status.Fail,
                    $"privateState.PendingCashWin={privateState.PendingCashWin}, Parameters.CashWin={parameters.CashWin}");
            else
                add("Framework", "PendingCashWin matches CashWin", Status.Pass,
                    $"{privateState.PendingCashWin}");

            if (privateState.Stake != parameters.Stake)
                add("Framework", "Private stake matches public stake", Status.Fail,
                    $"privateState.Stake={privateState.Stake}, Parameters.Stake={parameters.Stake}");
            else
                add("Framework", "Private stake matches public stake", Status.Pass,
                    $"{privateState.Stake}");
        }

        var hasWinningTargets = (gameData.WinInfo?.WinSymbols?.Length ?? 0) > 0;
        if (!hasWinningTargets && parameters.CashWin != 0m)
            add("Framework", "No-win ticket has zero CashWin", Status.Fail,
                $"WinInfo.WinSymbols is empty but CashWin={parameters.CashWin}");
        else if (!hasWinningTargets)
            add("Framework", "No-win ticket has zero CashWin", Status.Pass, "0");

        if (hasWinningTargets && parameters.CashWin <= 0m)
            add("Framework", "Winning ticket has positive CashWin", Status.Fail,
                $"WinInfo has winning targets but CashWin={parameters.CashWin}");
    }

    private static void CheckWinInfoSchema(
        TicketDto t,
        Action<string, string, Status, string> add)
    {
        var ok = true;
        var winIds = new HashSet<int>();
        foreach (var win in t.WinInfo.WinSymbols ?? Array.Empty<WinSymbolDto>())
        {
            if (win.Id <= 0 || Settings.Default.IsFeat(win.Id))
            {
                ok = false;
                add("WinInfo", $"Win symbol {win.Id} id", Status.Fail,
                    $"winning symbol id must be a positive non-feature coin symbol, got {win.Id}");
            }
            if (win.Target <= 0)
            {
                ok = false;
                add("WinInfo", $"Win symbol {win.Id} target", Status.Fail,
                    $"target must be positive, got {win.Target}");
            }
            if (!winIds.Add(win.Id))
            {
                ok = false;
                add("WinInfo", $"Win symbol {win.Id} duplicate", Status.Fail,
                    "same symbol appears more than once in WinSymbols");
            }
        }

        var nonWinIds = new HashSet<int>();
        foreach (var nonWin in t.WinInfo.NonWinSymbols ?? Array.Empty<NonWinSymbolDto>())
        {
            if (nonWin.Id <= 0 || Settings.Default.IsFeat(nonWin.Id))
            {
                ok = false;
                add("WinInfo", $"Non-win symbol {nonWin.Id} id", Status.Fail,
                    $"non-winning symbol id must be a positive non-feature coin symbol, got {nonWin.Id}");
            }
            if (winIds.Contains(nonWin.Id))
            {
                ok = false;
                add("WinInfo", $"Non-win symbol {nonWin.Id} overlap", Status.Fail,
                    "same symbol is declared as both winning and non-winning");
            }
            if (!nonWinIds.Add(nonWin.Id))
            {
                ok = false;
                add("WinInfo", $"Non-win symbol {nonWin.Id} duplicate", Status.Fail,
                    "same symbol appears more than once in NonWinSymbols");
            }
            if (nonWin.MinTarget < Settings.Default.NONWIN_MIN_TARGET || nonWin.MinTarget >= nonWin.MaxThreshold)
            {
                ok = false;
                add("WinInfo", $"Non-win symbol {nonWin.Id} threshold", Status.Fail,
                    $"MinTarget={nonWin.MinTarget}, MaxThreshold={nonWin.MaxThreshold}; expected min >= {Settings.Default.NONWIN_MIN_TARGET} and min < max");
            }
            if (nonWin.PrizeTier.HasValue && nonWin.PrizeTier.Value <= 0)
            {
                ok = false;
                add("WinInfo", $"Non-win symbol {nonWin.Id} prize tier", Status.Fail,
                    $"PrizeTier={nonWin.PrizeTier} must be positive when present");
            }
        }

        foreach (var tier in t.WinInfo.PrizeTiers ?? Array.Empty<PrizeTierDto>())
        {
            if (!winIds.Contains(tier.SymId))
            {
                ok = false;
                add("WinInfo", $"Prize tier sym {tier.SymId}", Status.Fail,
                    "WinInfo.PrizeTiers may only declare tiers for winning symbols; near-miss tiers belong on NonWinSymbols");
            }
            if (tier.Tier <= 0)
            {
                ok = false;
                add("WinInfo", $"Prize tier sym {tier.SymId} value", Status.Fail,
                    $"Tier={tier.Tier} must be positive");
            }
        }

        if (ok) add("WinInfo", "Declarations are internally consistent", Status.Pass, "ok");
    }

    private static bool CheckFeatureSchema(
        TicketDto t,
        Action<string, string, Status, string> add)
    {
        var ok = true;
        for (var turnIndex = 0; turnIndex < t.Turns.Length; turnIndex++)
        {
            foreach (var spawn in t.Turns[turnIndex].Spawns ?? Array.Empty<SpawnDto>())
            {
                var prefix = $"Turn {turnIndex + 1} Pos {spawn.Pos}";
                if (spawn.Id <= 0)
                {
                    ok = false;
                    add("Schema", $"{prefix} symbol id", Status.Fail, $"Id={spawn.Id} must be positive");
                }

                if (spawn.Id == Settings.Default.F_FLUSH_ID)
                {
                    ok = false;
                    add("Schema", $"{prefix} board symbol", Status.Fail,
                        $"Id={Settings.Default.F_FLUSH_ID} is FLUSH/PUSH pusher-only and must not appear as a board spawn");
                }

                if (spawn.Feature == null)
                {
                    if (Settings.Default.IsFeat(spawn.Id))
                    {
                        ok = false;
                        add("Schema", $"{prefix} feature payload", Status.Fail,
                            $"Id={spawn.Id} is a feature symbol but Feature object is missing");
                    }

                    continue;
                }

                ok &= CheckFeatureTree(spawn.Feature, spawn.Id, prefix, add, depth: 0);
            }
        }

        if (ok) add("Schema", "All feature payloads valid", Status.Pass, "ok");
        return ok;
    }

    private static bool CheckFeatureTree(
        FeatureDto feature,
        int owningSpawnId,
        string prefix,
        Action<string, string, Status, string> add,
        int depth)
    {
        var ok = true;
        if (!Settings.Default.IsFeat(feature.FeatureId))
        {
            ok = false;
            add("Schema", $"{prefix} feature id", Status.Fail,
                $"FeatureId={feature.FeatureId} is not a valid board feature id");
        }

        if (depth == 0 && feature.FeatureId != owningSpawnId)
        {
            ok = false;
            add("Schema", $"{prefix} feature id matches spawn", Status.Fail,
                $"spawn Id={owningSpawnId} but Feature.FeatureId={feature.FeatureId}");
        }

        if (feature.ConvertToId <= 0 || feature.ConvertToId == Settings.Default.F_FLUSH_ID)
        {
            ok = false;
            add("Schema", $"{prefix} convert target", Status.Fail,
                $"ConvertToId={feature.ConvertToId} must be a positive coin symbol or valid retrigger bridge, never FLUSH/PUSH");
        }

        if (feature.ReTrigger is { Length: > 1 })
        {
            ok = false;
            add("Schema", $"{prefix} ReTrigger count", Status.Fail,
                $"ReTrigger has {feature.ReTrigger.Length} child feature(s); max allowed is 1");
        }

        if (feature.FeatureId == Settings.Default.F_WHEEL)
        {
            if (!feature.WheelSymbolId.HasValue || feature.WheelSymbolId.Value <= 0 || Settings.Default.IsFeat(feature.WheelSymbolId.Value))
            {
                ok = false;
                add("Schema", $"{prefix} WHEEL symbol", Status.Fail,
                    $"WheelSymbolId={feature.WheelSymbolId} must be a positive non-feature coin symbol");
            }

            if (!feature.WheelStackValue.HasValue
                || feature.WheelStackValue.Value < Settings.Default.MIN_WHEEL_STACK_VALUE
                || feature.WheelStackValue.Value > Settings.Default.MAX_WHEEL_STACK_VALUE)
            {
                ok = false;
                add("Schema", $"{prefix} WHEEL stack value", Status.Fail,
                    $"WheelStackValue={feature.WheelStackValue} must be in {Settings.Default.MIN_WHEEL_STACK_VALUE}..{Settings.Default.MAX_WHEEL_STACK_VALUE}");
            }
        }
        else if (feature.WheelSymbolId.HasValue || feature.WheelStackValue.HasValue)
        {
            ok = false;
            add("Schema", $"{prefix} non-WHEEL payload", Status.Fail,
                "WheelSymbolId/WheelStackValue are only valid for WHEEL features");
        }

        if (feature.FeatureId == Settings.Default.F_PRUP)
        {
            if (!feature.UpgradeSymbolId.HasValue || feature.UpgradeSymbolId.Value <= 0 || Settings.Default.IsFeat(feature.UpgradeSymbolId.Value))
            {
                ok = false;
                add("Schema", $"{prefix} PRIZE_UPGRADE symbol", Status.Fail,
                    $"UpgradeSymbolId={feature.UpgradeSymbolId} must be a positive non-feature coin symbol");
            }

            if (!feature.UpgradePrizeValue.HasValue || feature.UpgradePrizeValue.Value < 0)
            {
                ok = false;
                add("Schema", $"{prefix} PRIZE_UPGRADE prize value", Status.Fail,
                    $"UpgradePrizeValue={feature.UpgradePrizeValue} must be present and non-negative");
            }
        }
        else if (feature.UpgradeSymbolId.HasValue || feature.UpgradePrizeValue.HasValue)
        {
            ok = false;
            add("Schema", $"{prefix} non-PRIZE_UPGRADE payload", Status.Fail,
                "UpgradeSymbolId/UpgradePrizeValue are only valid for PRIZE_UPGRADE features");
        }

        foreach (var child in feature.ReTrigger ?? Array.Empty<FeatureDto>())
            ok &= CheckFeatureTree(child, child.FeatureId, $"{prefix} ReTrigger->{child.FeatureId}", add, depth + 1);

        return ok;
    }

    private sealed class ReplayCell
    {
        public int  Sym;
        public int  Stack = 1;
        public bool IsFeat;
        public int  FeatureId;
        public int  ConvertToId;
        public int  WheelSymbolId;
        public int  WheelStackValue;
        public FeatureDto[] ReTrigger = Array.Empty<FeatureDto>();
    }

    private sealed class WheelFireEvent
    {
        public int Turn;
        public int WheelSymbolId;
        public int WheelStackValue;
        public int ActualMultiplier;
    }

    private sealed class ReplayResult
    {
        public Dictionary<int, int> Totals                 = new();
        public List<int>            MissingCellTurns        = new();
        public List<WheelFireEvent> WheelFireEvents         = new();
        public Dictionary<int, int> PrupFinalTierPerSymbol   = new();
    }

    private static ReplayResult ReplayTicket(TicketDto t)
    {
        var result = new ReplayResult();
        var board  = new ReplayCell?[Settings.Default.ROWS, Settings.Default.COLS];

        for (int r = 0; r < Settings.Default.ROWS; r++)
        {
            for (int c = 0; c < Settings.Default.COLS; c++)
            {
                board[r, c] = new ReplayCell { Sym = t.StartingBoard[r][c].Id };
            }
        }

        for (int turnIdx = 0; turnIdx < t.Turns.Length; turnIdx++)
        {
            var turn = t.Turns[turnIdx];

            // Phase 1: FlatStale — any feature cell still sitting around from a
            // previous turn (shouldn't normally happen, but defensive) reverts
            // to its converted symbol before collection.
            for (int r = 0; r < Settings.Default.ROWS; r++)
            {
                for (int c = 0; c < Settings.Default.COLS; c++)
                {
                    if (board[r, c]?.IsFeat == true)
                        board[r, c] = new ReplayCell { Sym = ResolveConvert(board[r, c]!) };
                }
            }

            // Phase 2: Collect
            for (int c = 0; c < Settings.Default.COLS; c++)
            {
                var pusher = turn.Pushers[c];
                bool isFlush = pusher.FeatureId == Settings.Default.F_FLUSH_ID;
                if (isFlush)
                {
                    for (int r = 0; r < Settings.Default.ROWS; r++)
                    {
                        if (board[r, c] != null) Acc(result.Totals, board[r, c]!);
                        board[r, c] = null;
                    }
                }
                else
                {
                    int push = pusher.PushValue;
                    for (int r = Settings.Default.ROWS - push; r < Settings.Default.ROWS; r++)
                        if (board[r, c] != null) Acc(result.Totals, board[r, c]!);
                    for (int r = Settings.Default.ROWS - 1; r >= 0; r--)
                    {
                        int src = r - push;
                        board[r, c] = src >= 0 ? board[src, c] : null;
                    }
                }
            }

            // Phase 3: Rotate 90 clockwise
            board = RotCW(board);

            // Phase 4: ApplySpawns
            foreach (var sp in turn.Spawns ?? Array.Empty<SpawnDto>())
            {
                int r = sp.Pos / Settings.Default.COLS, c = sp.Pos % Settings.Default.COLS;
                var cell = new ReplayCell { Sym = sp.Id, Stack = sp.Stack ?? 1 };
                if (sp.Feature != null)
                {
                    cell.IsFeat          = true;
                    cell.FeatureId       = sp.Feature.FeatureId;
                    cell.ConvertToId     = sp.Feature.ConvertToId;
                    cell.WheelSymbolId   = sp.Feature.WheelSymbolId ?? 0;
                    cell.WheelStackValue = sp.Feature.WheelStackValue ?? 0;
                    cell.ReTrigger       = sp.Feature.ReTrigger ?? Array.Empty<FeatureDto>();

                    AccumulatePrizeUpgradeTokens(sp.Feature, result);
                }
                board[r, c] = cell;
            }

            // Check: no cell left null after spawns
            bool anyMissing = false;
            for (int r = 0; r < Settings.Default.ROWS; r++)
            {
                for (int c = 0; c < Settings.Default.COLS; c++)
                {
                    if (board[r, c] == null) anyMissing = true;
                }
            }
            if (anyMissing) result.MissingCellTurns.Add(turnIdx + 1);

            // Phase 5: FireAll — mirror Sim.FireAll exactly: no-board effects first,
            // then WHEEL. This matters if a ticket has multiple feature cells visible
            // in the same turn.
            FireFeaturePass(board, result, turnIdx + 1, wheelPass: false);
            FireFeaturePass(board, result, turnIdx + 1, wheelPass: true);
        }

        return result;
    }

    private static void FireFeaturePass(
        ReplayCell?[,] board,
        ReplayResult result,
        int turn,
        bool wheelPass)
    {
        bool any;
        do
        {
            any = false;
            for (int r = 0; r < Settings.Default.ROWS; r++)
            {
                for (int c = 0; c < Settings.Default.COLS; c++)
                {
                    var fc = board[r, c];
                    if (fc?.IsFeat != true) continue;

                    var isWheel = fc.FeatureId == Settings.Default.F_WHEEL;
                    if (isWheel != wheelPass) continue;

                    if (isWheel && fc.WheelStackValue + 1 > 1)
                    {
                        int multiplier = fc.WheelStackValue + 1;
                        int sym = fc.WheelSymbolId;
                        for (int rr = 0; rr < Settings.Default.ROWS; rr++)
                        {
                            for (int cc = 0; cc < Settings.Default.COLS; cc++)
                            {
                                var cell = board[rr, cc];
                                if (cell != null && !cell.IsFeat && cell.Sym == sym)
                                    cell.Stack = Math.Min(Settings.Default.MAX_COIN_STACK, cell.Stack + multiplier - 1);
                            }
                        }

                        result.WheelFireEvents.Add(new WheelFireEvent
                        {
                            Turn = turn,
                            WheelSymbolId = sym,
                            WheelStackValue = fc.WheelStackValue,
                            ActualMultiplier = multiplier,
                        });
                    }

                    board[r, c] = new ReplayCell { Sym = ResolveConvert(fc) };
                    any = true;
                }
            }
        }
        while (any && BoardHasFeatureCell(board, wheelPass));
    }

    /// <summary>
    /// Resolves the REAL eventual symbol a feature cell converts to. When a
    /// ConvertToId points at another feature and ReTrigger contains that nested
    /// feature, walk the chain until the deepest link's real conversion target.
    /// </summary>
    private static int ResolveConvert(ReplayCell fc)
    {
        if (Settings.Default.IsFeat(fc.ConvertToId) && fc.ReTrigger.Length > 0)
        {
            var link = fc.ReTrigger[0];
            while (link.ReTrigger is { Length: > 0 })
                link = link.ReTrigger[0];
            return link.ConvertToId > 0 ? link.ConvertToId : Settings.Default.F_COIN;
        }
        return fc.ConvertToId > 0 ? fc.ConvertToId : Settings.Default.F_COIN;
    }

    private static bool BoardHasFeatureCell(ReplayCell?[,] board, bool wheelPass)
    {
        for (int r = 0; r < Settings.Default.ROWS; r++)
        {
            for (int c = 0; c < Settings.Default.COLS; c++)
            {
                var cell = board[r, c];
                if (cell?.IsFeat == true && (cell.FeatureId == Settings.Default.F_WHEEL) == wheelPass)
                    return true;
            }
        }
        return false;
    }

    private static void Acc(Dictionary<int, int> totals, ReplayCell cell)
    {
        if (Settings.Default.IsFeat(cell.Sym)) return;
        totals.TryGetValue(cell.Sym, out int existing);
        totals[cell.Sym] = existing + cell.Stack;
    }

    private static ReplayCell?[,] RotCW(ReplayCell?[,] b)
    {
        var r = new ReplayCell?[Settings.Default.ROWS, Settings.Default.COLS];
        for (int row = 0; row < Settings.Default.ROWS; row++)
        {
            for (int col = 0; col < Settings.Default.COLS; col++)
            {
                r[col, Settings.Default.ROWS - 1 - row] = b[row, col];
            }
        }
        return r;
    }

    private static int CountLogicalFeatureSpawns(TicketDto t, int featureId) =>
        t.Turns
            .SelectMany(turn => turn.Spawns ?? Array.Empty<SpawnDto>())
            .Where(spawn => spawn.Feature != null)
            .Sum(spawn => CountFeatureTree(spawn.Feature!, featureId));

    private static int CountFeatureTree(FeatureDto feature, int featureId) =>
        (feature.FeatureId == featureId ? 1 : 0)
        + (feature.ReTrigger ?? Array.Empty<FeatureDto>())
            .Sum(child => CountFeatureTree(child, featureId));

    private static int MaxReTriggerDepth(FeatureDto feature)
    {
        if (feature.ReTrigger == null || feature.ReTrigger.Length == 0) return 0;
        return 1 + feature.ReTrigger.Max(MaxReTriggerDepth);
    }

    private static void AccumulatePrizeUpgradeTokens(FeatureDto feature, ReplayResult result)
    {
        if (feature.FeatureId == Settings.Default.F_PRUP && feature.UpgradeSymbolId.HasValue)
        {
            // PRIZE_UPGRADE doesn't carry an explicit tier number in the
            // public schema — tier is inferred by COUNTING how many
            // PRIZE_UPGRADE tokens for this symbol have appeared so far.
            int sym = feature.UpgradeSymbolId.Value;
            int next = result.PrupFinalTierPerSymbol.GetValueOrDefault(sym, 0) + 1;
            result.PrupFinalTierPerSymbol[sym] = next;
        }

        foreach (var nested in feature.ReTrigger ?? Array.Empty<FeatureDto>())
            AccumulatePrizeUpgradeTokens(nested, result);
    }

}
