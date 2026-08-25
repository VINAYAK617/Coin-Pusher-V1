using System;
using System.Collections.Generic;
using System.Linq;
using GameEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static CoinPusherEngine.TicketSerializer;
namespace CoinPusherEngine
{
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
    ///    EXTRA_SPIN feature id, _settings.F_XSPIN) meaning "there is more chain to unwrap,
    ///    look inside ReTrigger" — it is NOT "no real target, fall back to filler".
    ///    An earlier version of this checker treated that placeholder as an invalid
    ///    convert target and substituted _settings.F_COIN (symbol 1), which silently
    ///    injected a fake extra collection of whichever win symbol happened to
    ///    equal _settings.F_COIN's value. Confirmed via direct comparison against Sim.Run
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
    public sealed class CPTicketChecker
    {
        private const int MaxPublicWheelStackValue = 3;

        private readonly ICustomProfileSettings _settings;

        public CPTicketChecker(ICustomProfileSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public enum Status
        {
            Pass,
            Fail,
            Warning
        }

        public sealed class TicketCheckResult
        {
            public List<string> Errors { get; } = new();
            public bool IsValid => Errors.Count == 0;
        }

        public sealed class CheckItem
        {
            public string Category { get; init; } = "";
            public string Name { get; init; } = "";
            public Status Result { get; init; }
            public string Detail { get; init; } = "";
        }

        public sealed class Report
        {
            public List<CheckItem> Checks { get; } = new();
            public bool IsValid => Checks.All(c => c.Result != Status.Fail);
            public int PassCount => Checks.Count(c => c.Result == Status.Pass);
            public int FailCount => Checks.Count(c => c.Result == Status.Fail);
            public int WarningCount => Checks.Count(c => c.Result == Status.Warning);

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
        public static TicketCheckResult CheckJson(ICustomProfileSettings settings, string json) =>
            new CPTicketChecker(settings).CheckJson(json);

        public static TicketCheckResult CheckObject(ICustomProfileSettings settings, Ticket? ticket) =>
            new CPTicketChecker(settings).CheckObject(ticket);

        public static TicketCheckResult CheckObject(ICustomProfileSettings settings, TicketDto? ticket) =>
            new CPTicketChecker(settings).CheckObject(ticket);

        public static Report CheckTicket(ICustomProfileSettings settings, Ticket? ticket) =>
            new CPTicketChecker(settings).CheckTicket(ticket);

        public static Report CheckTicket(ICustomProfileSettings settings, TicketDto? ticket) =>
            new CPTicketChecker(settings).CheckTicket(ticket);

        public TicketCheckResult CheckJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                var result = new TicketCheckResult();
                result.Errors.Add("Ticket JSON is empty.");
                return result;
            }

            JObject? root;
            try
            {
                root = JsonConvert.DeserializeObject<JObject>(json);
            }
            catch (JsonException ex)
            {
                var result = new TicketCheckResult();
                result.Errors.Add($"Ticket JSON could not be parsed: {ex.Message}");
                return result;
            }

            if (root == null)
            {
                var result = new TicketCheckResult();
                result.Errors.Add("Ticket JSON could not be parsed: root object is null.");
                return result;
            }

            var requiredJsonFieldErrors = ValidateRequiredJsonFields(root);
            if (requiredJsonFieldErrors.Count > 0)
            {
                var result = new TicketCheckResult();
                result.Errors.AddRange(requiredJsonFieldErrors);
                return result;
            }

            try
            {
                if (root["WinInfo"] != null || root["StartingBoard"] != null || root["Turns"] != null)
                    return CheckTicket(root.ToObject<TicketDto>()).ToResult();

                return CheckTicket(Ticket.Load(json)).ToResult();
            }
            catch (Exception ex)
            {
                var result = new TicketCheckResult();
                result.Errors.Add($"Ticket JSON could not be mapped to ticket objects: {ex.Message}");
                return result;
            }
        }

        private IReadOnlyList<string> ValidateRequiredJsonFields(JObject root)
        {
            var errors = new List<string>();
            var gameData = FindGameDataJson(root);
            if (gameData == null)
                return errors;

            var game = AsObject(gameData, "GameData", errors);
            if (game == null)
                return errors;

            var winInfo = AsObject(game["WinInfo"], "GameData.WinInfo", errors);
            if (winInfo != null)
            {
                RequireJsonProperty(winInfo, "TotalSpins", "GameData.WinInfo", errors);
                foreach (var (obj, path) in ObjectItems(winInfo["WinSymbols"], "GameData.WinInfo.WinSymbols", errors))
                {
                    RequireJsonProperty(obj, "Id", path, errors);
                    RequireJsonProperty(obj, "Target", path, errors);
                }

                foreach (var (obj, path) in ObjectItems(winInfo["NonWinSymbols"], "GameData.WinInfo.NonWinSymbols",
                             errors))
                {
                    RequireJsonProperty(obj, "Id", path, errors);
                    RequireJsonProperty(obj, "MinTarget", path, errors);
                    RequireJsonProperty(obj, "MaxThreshold", path, errors);
                }

                foreach (var (obj, path) in ObjectItems(winInfo["PrizeTiers"], "GameData.WinInfo.PrizeTiers", errors))
                {
                    RequireJsonProperty(obj, "SymId", path, errors);
                    RequireJsonProperty(obj, "Tier", path, errors);
                }
            }

            ValidateStartingBoardJson(game["StartingBoard"], errors);
            foreach (var (turn, turnPath) in ObjectItems(game["Turns"], "GameData.Turns", errors))
            {
                foreach (var (pusher, pusherPath) in ObjectItems(turn["Pushers"], $"{turnPath}.Pushers", errors))
                    RequireJsonProperty(pusher, "PushValue", pusherPath, errors);

                foreach (var (spawn, spawnPath) in ObjectItems(turn["Spawns"], $"{turnPath}.Spawns", errors))
                {
                    RequireJsonProperty(spawn, "Pos", spawnPath, errors);
                    RequireJsonProperty(spawn, "Id", spawnPath, errors);

                    if (spawn["Feature"] is JObject feature)
                        ValidateFeatureJson(feature, $"{spawnPath}.Feature", errors);
                    else if (spawn["Feature"] != null && spawn["Feature"]!.Type != JTokenType.Null)
                        errors.Add($"Schema/{spawnPath}.Feature: expected object when present.");
                }
            }

            return errors;
        }

        private static JToken? FindGameDataJson(JObject root)
        {
            if (root["WinInfo"] != null || root["StartingBoard"] != null || root["Turns"] != null)
                return root;

            return root["game"]?["publicState"]?["game"]?["GameData"]
                   ?? root["game"]?["publicState"]?["game"]?["gameData"];
        }

        private static void ValidateStartingBoardJson(JToken? token, List<string> errors)
        {
            var rows = AsArray(token, "GameData.StartingBoard", errors);
            if (rows == null)
                return;

            for (var row = 0; row < rows.Count; row++)
            {
                var cells = AsArray(rows[row], $"GameData.StartingBoard[{row}]", errors);
                if (cells == null)
                    continue;

                for (var col = 0; col < cells.Count; col++)
                {
                    var cell = AsObject(cells[col], $"GameData.StartingBoard[{row}][{col}]", errors);
                    if (cell != null)
                        RequireJsonProperty(cell, "Id", $"GameData.StartingBoard[{row}][{col}]", errors);
                }
            }
        }

        private void ValidateFeatureJson(JObject feature, string path, List<string> errors)
        {
            RequireJsonProperty(feature, "FeatureId", path, errors);
            RequireJsonProperty(feature, "ConvertToId", path, errors);

            var featureId = feature["FeatureId"]?.Type == JTokenType.Integer
                ? feature["FeatureId"]!.Value<int>()
                : 0;

            if (featureId == _settings.F_WHEEL)
            {
                RequireJsonProperty(feature, "WheelSymbolId", path, errors);
                RequireJsonProperty(feature, "WheelStackValue", path, errors);
            }
            else if (featureId == _settings.F_PRUP)
            {
                RequireJsonProperty(feature, "UpgradeSymbolId", path, errors);
                RequireJsonProperty(feature, "UpgradePrizeValue", path, errors);
            }

            if (feature["ReTrigger"] == null || feature["ReTrigger"]!.Type == JTokenType.Null)
                return;

            foreach (var (child, childPath) in ObjectItems(feature["ReTrigger"], $"{path}.ReTrigger", errors))
                ValidateFeatureJson(child, childPath, errors);
        }

        private static IEnumerable<(JObject Obj, string Path)> ObjectItems(
            JToken? token,
            string path,
            List<string> errors)
        {
            var array = AsArray(token, path, errors);
            if (array == null)
                yield break;

            for (var index = 0; index < array.Count; index++)
            {
                var obj = AsObject(array[index], $"{path}[{index}]", errors);
                if (obj != null)
                    yield return (obj, $"{path}[{index}]");
            }
        }

        private static JArray? AsArray(JToken? token, string path, List<string> errors)
        {
            if (token is JArray array)
                return array;

            errors.Add($"Schema/{path}: expected array but found {TokenKind(token)}.");
            return null;
        }

        private static JObject? AsObject(JToken? token, string path, List<string> errors)
        {
            if (token is JObject obj)
                return obj;

            errors.Add($"Schema/{path}: expected object but found {TokenKind(token)}.");
            return null;
        }

        private static void RequireJsonProperty(
            JObject obj,
            string propertyName,
            string path,
            List<string> errors)
        {
            var property = obj.Property(propertyName);
            if (property == null || property.Value.Type == JTokenType.Null)
                errors.Add($"Schema/{path}.{propertyName}: required JSON property is missing.");
        }

        private static string TokenKind(JToken? token) =>
            token == null ? "missing" : token.Type.ToString();

        public TicketCheckResult CheckObject(Ticket? ticket) =>
            CheckTicket(ticket).ToResult();

        public TicketCheckResult CheckObject(TicketDto? ticket) =>
            CheckTicket(ticket).ToResult();

        public Report CheckTicket(Ticket? ticket)
        {
            var report = new Report();

            void Add(string cat, string name, Status s, string detail) =>
                report.Checks.Add(new CheckItem { Category = cat, Name = name, Result = s, Detail = detail });

            try
            {

                if (ticket == null || ticket.GameData == null)
                {
                    Add("Ticket", "Coin Pusher GameData present", Status.Fail,
                        "ticket.game.publicState.game.GameData is missing; cannot run Coin Pusher logic validation");
                    return report;
                }

                var gameData = ticket.GameData;
                Merge(report, CheckTicket(gameData));
                CheckFrameworkLogicValues(ticket, gameData, Add);
                CheckExactPpsTier(ticket, gameData, Add);
                return report;
            }
            catch (Exception ex)
            {
                Add("Checker", "TicketChecker completed without exception", Status.Fail,
                    $"{ex.GetType().Name}: {ex.Message}");
                return report;
            }
        }

        public Report CheckTicket(TicketDto? t)
        {
            var report = new Report();

            void Add(string cat, string name, Status s, string detail) =>
                report.Checks.Add(new CheckItem { Category = cat, Name = name, Result = s, Detail = detail });

            try
            {

                // ── 1. STRUCTURE ──────────────────────────────────────────────────
                if (t == null)
                {
                    Add("Structure", "Ticket object present", Status.Fail, "ticket is null");
                    return report;
                }

                if (t.WinInfo == null)
                {
                    Add("Structure", "WinInfo present", Status.Fail, "WinInfo is null");
                    return report;
                }

                if (t.StartingBoard == null || t.StartingBoard.Length != _settings.ROWS)
                {
                    Add("Structure", "StartingBoard rows", Status.Fail,
                        $"expected {_settings.ROWS} rows, got {t.StartingBoard?.Length ?? 0}");
                    return report;
                }

                for (int r = 0; r < _settings.ROWS; r++)
                    if (t.StartingBoard[r] == null || t.StartingBoard[r].Length != _settings.COLS)
                    {
                        Add("Structure", $"StartingBoard row {r} width", Status.Fail,
                            $"expected {_settings.COLS} cols, got {t.StartingBoard[r]?.Length ?? 0}");
                        return report;
                    }

                Add("Structure", "StartingBoard is 5x5", Status.Pass, "ok");
                CheckStartingBoardCells(t, Add);

                if (t.Turns == null || t.Turns.Length == 0)
                {
                    Add("Structure", "Turns present", Status.Fail, "no turns");
                    return report;
                }

                Add("Structure", "Turns present", Status.Pass, $"{t.Turns.Length} turns");

                bool structureOk = true;
                foreach (var (turn, i) in t.Turns.Select((x, i) => (x, i)))
                {
                    if (turn.Pushers == null || turn.Pushers.Length != _settings.COLS)
                    {
                        structureOk = false;
                        Add("Structure", $"Turn {i + 1} pusher count", Status.Fail,
                            $"expected {_settings.COLS}, got {turn.Pushers?.Length ?? 0}");
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

                if (totalSpins < _settings.BASE_SPINS || totalSpins > _settings.MAX_SPINS)
                    Add("SpinCount", "TotalSpins within fixed baseline range", Status.Fail,
                        $"TotalSpins={totalSpins} outside the fixed [{_settings.BASE_SPINS}..{_settings.MAX_SPINS}] range " +
                        $"(BaseSpins is always {_settings.BASE_SPINS}; only EXTRA_SPIN can extend it, up to {_settings.MAX_SPINS} total)");
                else
                    Add("SpinCount", "TotalSpins within fixed baseline range", Status.Pass,
                        $"{totalSpins} (base {_settings.BASE_SPINS} + {totalSpins - _settings.BASE_SPINS} extra)");

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
                        bool isFlush = p.FeatureId == _settings.F_FLUSH_ID;
                        if (p.FeatureId.HasValue && !isFlush)
                        {
                            pusherGeometryOk = false;
                            Add("Geometry", $"Turn {i + 1} col {c} pusher feature id", Status.Fail,
                                $"FeatureId={p.FeatureId} is invalid on a pusher; only FLUSH/PUSH id {_settings.F_FLUSH_ID} is allowed");
                            continue;
                        }

                        bool valid = isFlush
                            ? p.PushValue == _settings.ROWS
                            : p.PushValue >= _settings.MIN_PUSH && p.PushValue <= _settings.MAX_PUSH;
                        if (!valid)
                        {
                            pusherGeometryOk = false;
                            Add("Geometry", $"Turn {i + 1} col {c} push value", Status.Fail,
                                $"PushValue={p.PushValue} FeatureId={p.FeatureId} — not a valid normal push " +
                                $"({_settings.MIN_PUSH}-{_settings.MAX_PUSH}) or flush ({_settings.ROWS})");
                        }
                    }
                }

                if (pusherGeometryOk) Add("Geometry", "All pusher values valid", Status.Pass, "ok");

                bool pusherSpawnCountOk = true;
                for (int i = 0; i < t.Turns.Length; i++)
                {
                    var turn = t.Turns[i];
                    var pushedCells = (turn.Pushers ?? Array.Empty<PusherDto>()).Sum(p => p.PushValue);
                    var spawnCount = (turn.Spawns ?? Array.Empty<SpawnDto>()).Length;
                    if (pushedCells != spawnCount)
                    {
                        pusherSpawnCountOk = false;
                        Add("Geometry", $"Turn {i + 1} pushed cells match drops", Status.Fail,
                            $"Pushers sum to {pushedCells} physical popped cell(s), but Spawns contains {spawnCount} drop(s)");
                    }
                }

                if (pusherSpawnCountOk)
                    Add("Geometry", "Every turn pushed-cell count matches spawn count", Status.Pass, "ok");

                // ── 4. SPAWN POSITION SANITY ────────────────────────────────────────
                bool spawnPosOk = true;
                for (int i = 0; i < t.Turns.Length; i++)
                {
                    var seen = new HashSet<int>();
                    foreach (var sp in t.Turns[i].Spawns ?? Array.Empty<SpawnDto>())
                    {
                        if (sp.Pos < 0 || sp.Pos >= _settings.ROWS * _settings.COLS)
                        {
                            spawnPosOk = false;
                            Add("Geometry", $"Turn {i + 1} spawn position range", Status.Fail,
                                $"Pos={sp.Pos} out of range 0..{_settings.ROWS * _settings.COLS - 1}");
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
                CheckPrizeUpgradeValues(t, Add);
                CheckFeatureTiming(t, Add);
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

                foreach (var balance in replay.SpawnBalances)
                {
                    if (balance.EmptySlotsBeforeSpawns != balance.SpawnCount)
                    {
                        Add("Replay", $"Turn {balance.Turn} spawn count matches popped cells", Status.Fail,
                            $"physical popped cells={balance.EmptySlotsBeforeSpawns}, drops/spawns={balance.SpawnCount}");
                    }
                    else if (balance.OverwrittenSpawnPositions.Count == 0)
                    {
                        Add("Replay", $"Turn {balance.Turn} spawn count matches popped cells", Status.Pass,
                            $"{balance.SpawnCount}/{balance.EmptySlotsBeforeSpawns}");
                    }
                }

                foreach (var balance in replay.SpawnBalances.Where(b => b.OverwrittenSpawnPositions.Count > 0))
                {
                    Add("Replay", $"Turn {balance.Turn} spawns only fill empty cells", Status.Fail,
                        "spawn Pos overwrote occupied board cells: " +
                        string.Join(",", balance.OverwrittenSpawnPositions));
                }

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

                CheckTopPrizeCompletionTurn(t, replay, Add);
                CheckWinningRoundPolicy(t, replay, Add);

                // ── 7. NEAR-MISS BOUND VERIFICATION ────────────────────────────────
                // Near-miss symbols are checked with the same hard replay standard as wins.
                foreach (var nw in t.WinInfo.NonWinSymbols ?? Array.Empty<NonWinSymbolDto>())
                {
                    int got = replay.Totals.GetValueOrDefault(nw.Id);
                    bool ok = got >= nw.MinTarget && got < nw.MaxThreshold;
                    Add("Payout", $"Near-miss symbol {nw.Id} within bounds", ok ? Status.Pass : Status.Fail,
                        $"got={got} want [{nw.MinTarget}..{nw.MaxThreshold})");
                }

                // ── 8. WININFO DECLARATION / FILLER CAP CHECK ───────────────────────
                var declaredWin = (t.WinInfo.WinSymbols ?? Array.Empty<WinSymbolDto>()).Select(w => w.Id).ToHashSet();
                var declaredNonWin = (t.WinInfo.NonWinSymbols ?? Array.Empty<NonWinSymbolDto>()).Select(w => w.Id)
                    .ToHashSet();
                bool declarationOk = true;
                foreach (var (sym, count) in replay.Totals)
                {
                    if (_settings.IsFeat(sym) || count == 0) continue;
                    if (!declaredWin.Contains(sym) && !declaredNonWin.Contains(sym))
                    {
                        declarationOk = false;
                        Add("WinInfo", $"Collected symbol {sym} declared", Status.Fail,
                            $"replay collected {count} of symbol {sym}, but it is absent from both WinSymbols and NonWinSymbols");
                        continue;
                    }

                }

                if (declarationOk) Add("WinInfo", "Every collected symbol declared", Status.Pass, "ok");

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

                    if (w.OverflowAttemptCount > 0)
                    {
                        Add("Feature", $"WHEEL fire turn {w.Turn} sym {w.WheelSymbolId} stack cap", Status.Fail,
                            $"WHEEL attempted to push {w.OverflowAttemptCount} existing stack(s) above MAX_COIN_STACK={_settings.MAX_COIN_STACK}: {w.OverflowDetails}");
                    }
                    else
                    {
                        Add("Feature", $"WHEEL fire turn {w.Turn} sym {w.WheelSymbolId} stack cap", Status.Pass,
                            $"no attempted stack above {_settings.MAX_COIN_STACK}");
                    }

                    if (w.AffectedCellCount == 0)
                        Add("Feature", $"WHEEL fire turn {w.Turn} sym {w.WheelSymbolId} affects board", Status.Fail,
                            "WHEEL selected a symbol that did not gain stack on any existing normal board cell; self-conversion alone is not valid");
                    else
                        Add("Feature", $"WHEEL fire turn {w.Turn} sym {w.WheelSymbolId} affects board", Status.Pass,
                            $"existing increased cells={w.AffectedCellCount}, selfConversion={w.SelfConversionAffected}");
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

                int extraSpinTokenCount = CountLogicalFeatureSpawns(t, _settings.F_XSPIN);
                int expectedExtras = totalSpins - _settings.BASE_SPINS;
                if (extraSpinTokenCount != expectedExtras)
                    Add("Feature", "EXTRA_SPIN token count matches bonus spins", Status.Fail,
                        $"TotalSpins implies {expectedExtras} extra spin(s), but found {extraSpinTokenCount} " +
                        "logical EXTRA_SPIN feature occurrence(s), including ReTrigger children");
                else
                    Add("Feature", "EXTRA_SPIN token count matches bonus spins", Status.Pass,
                        $"{extraSpinTokenCount} logical token(s) for {expectedExtras} extra spin(s)");

                CheckExtraSpinTimeline(t, Add);
                CheckFeatureCaps(t, extraSpinTokenCount, Add);

                var finalTurn = t.Turns[^1];
                var finalFeature = (finalTurn.Spawns ?? Array.Empty<SpawnDto>())
                    .FirstOrDefault(spawn => spawn.Feature != null);
                if (finalFeature != null)
                    Add("Feature", "No board feature on final spin", Status.Fail,
                        $"final turn contains feature {finalFeature.Feature!.FeatureId} at Pos={finalFeature.Pos}");
                else
                    Add("Feature", "No board feature on final spin", Status.Pass, "ok");

                var finalFlush = (finalTurn.Pushers ?? Array.Empty<PusherDto>())
                    .Select((pusher, col) => (pusher, col))
                    .FirstOrDefault(item => item.pusher.FeatureId == _settings.F_FLUSH_ID);
                if (finalFlush.pusher != null)
                    Add("Feature", "No FLUSH/PUSH on final spin", Status.Fail,
                        $"final turn contains FLUSH/PUSH pusher at col {finalFlush.col}");
                else
                    Add("Feature", "No FLUSH/PUSH on final spin", Status.Pass, "ok");

                // ── 11. PRIZE_UPGRADE TIER CONSISTENCY ──────────────────────────────
                // Declared tiers can come from EITHER WinInfo.PrizeTiers (winning
                // symbols) OR a near-miss symbol's own NonWinSymbolDto.PrizeTier field.
                // This cross-check catches any mismatch between declared tiers and the
                // public token sequence, independent of the internal collected-total
                // verifier.
                var declaredTiers = (t.WinInfo.PrizeTiers ?? Array.Empty<PrizeTierDto>())
                    .GroupBy(p => p.SymId)
                    .ToDictionary(group => group.Key, group => group.First().Tier);
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

                CheckExperienceWarnings(t, replay, Add);

                return report;
            }
            catch (Exception ex)
            {
                Add("Checker", "TicketChecker completed without exception", Status.Fail,
                    $"{ex.GetType().Name}: {ex.Message}");
                return report;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // INDEPENDENT REPLAY — deliberately separate from Sim.cs
        // ═══════════════════════════════════════════════════════════════════════

        private void Merge(Report target, Report source)
        {
            target.Checks.AddRange(source.Checks);
        }

        private void CheckFrameworkLogicValues(
            Ticket ticket,
            TicketDto gameData,
            Action<string, string, Status, string> add)
        {
            var parameters = ticket.Parameters;
            if (parameters == null)
            {
                add("Framework", "Parameters available", Status.Fail,
                    "ticket Parameters is missing");
                return;
            }

            if (!parameters.Stake.HasValue || parameters.Stake.Value <= 0m)
            {
                add("Framework", "Stake positive", Status.Fail,
                    $"Parameters.Stake must be positive, found {parameters.Stake}");
            }
            else
            {
                var expectedMultiplier = parameters.CashWin / parameters.Stake.Value;
                if (parameters.StakeMultiplier != expectedMultiplier)
                    add("Framework", "StakeMultiplier matches CashWin / Stake", Status.Fail,
                        $"StakeMultiplier={parameters.StakeMultiplier}, expected {expectedMultiplier}");
                else
                    add("Framework", "StakeMultiplier matches CashWin / Stake", Status.Pass,
                        $"{parameters.StakeMultiplier}");
            }

            var expectedWinner = parameters.CashWin > 0m;
            if (parameters.IsWinner != expectedWinner)
                add("Framework", "IsWinner matches CashWin", Status.Fail,
                    $"IsWinner={parameters.IsWinner}, CashWin={parameters.CashWin}");
            else
                add("Framework", "IsWinner matches CashWin", Status.Pass,
                    $"{parameters.IsWinner}");

            var expectedPrizeMultiplier = ExpectedCashWin(gameData, out var payoutErrors);
            foreach (var error in payoutErrors)
                add("Framework", "Prize ladder lookup", Status.Fail, error);

            if (payoutErrors.Count == 0)
            {
                if (parameters.StakeMultiplier != expectedPrizeMultiplier)
                    add("Framework", "StakeMultiplier matches WinInfo prizes", Status.Fail,
                        $"StakeMultiplier={parameters.StakeMultiplier}, WinInfo resolves to {expectedPrizeMultiplier}");
                else
                    add("Framework", "StakeMultiplier matches WinInfo prizes", Status.Pass,
                        $"{expectedPrizeMultiplier}");
            }
        }

        private void CheckExactPpsTier(
            Ticket ticket,
            TicketDto gameData,
            Action<string, string, Status, string> add)
        {
            if (_settings.PpsCombinations == null || _settings.PpsCombinations.Count == 0)
                return;

            var parameters = ticket.Parameters;
            if (parameters == null)
                return;

            var tierNumber = parameters.TierNumber;
            if (tierNumber == 77)
            {
                var noWin = (gameData.WinInfo.WinSymbols?.Length ?? 0) == 0
                    && (gameData.WinInfo.PrizeTiers?.Length ?? 0) == 0
                    && parameters.CashWin == 0m
                    && parameters.StakeMultiplier == 0m;
                add("PPS", "Tier77 is exact no-win", noWin ? Status.Pass : Status.Fail,
                    noWin
                        ? "no winning symbols, prize tiers, or payout"
                        : $"WinSymbols={gameData.WinInfo.WinSymbols?.Length ?? 0}, PrizeTiers={gameData.WinInfo.PrizeTiers?.Length ?? 0}, CashWin={parameters.CashWin}, StakeMultiplier={parameters.StakeMultiplier}");
                return;
            }

            var combination = _settings.PpsCombinations
                .SingleOrDefault(item => item.Id == tierNumber);
            if (combination == null)
            {
                add("PPS", "Tier maps to PPS row", Status.Fail,
                    $"TierNumber={tierNumber} has no exact PPS combination");
                return;
            }

            var expected = combination.Components.OrderBy(item => item.SymbolId).ToArray();
            var actualWins = (gameData.WinInfo.WinSymbols ?? Array.Empty<WinSymbolDto>())
                .OrderBy(item => item.Id)
                .ToArray();
            var symbolMatch = actualWins.Select(item => item.Id)
                .SequenceEqual(expected.Select(item => item.SymbolId));
            add("PPS", "Winning symbols match tier row", symbolMatch ? Status.Pass : Status.Fail,
                symbolMatch
                    ? $"PPS combination #{combination.Id}"
                    : $"actual=[{string.Join(",", actualWins.Select(item => item.Id))}], expected=[{string.Join(",", expected.Select(item => item.SymbolId))}]");

            var declaredTiers = (gameData.WinInfo.PrizeTiers ?? Array.Empty<PrizeTierDto>())
                .GroupBy(item => item.SymId)
                .ToDictionary(group => group.Key, group => group.First().Tier);
            var tierMatch = expected.All(component =>
                    declaredTiers.GetValueOrDefault(component.SymbolId) == component.Tier)
                && declaredTiers.Keys.OrderBy(id => id).SequenceEqual(
                    expected.Where(component => component.Tier > 0)
                        .Select(component => component.SymbolId)
                        .OrderBy(id => id));
            add("PPS", "Prize tiers match tier row", tierMatch ? Status.Pass : Status.Fail,
                tierMatch
                    ? "exact"
                    : $"declared=[{string.Join(",", declaredTiers.OrderBy(item => item.Key).Select(item => $"{item.Key}:{item.Value}"))}], expected=[{string.Join(",", expected.Select(item => $"{item.SymbolId}:{item.Tier}"))}]");

            var targetMatch = actualWins.Length == expected.Length
                && expected.All(component => actualWins.SingleOrDefault(win => win.Id == component.SymbolId)?.Target
                    == _settings.PrizeLadderRows[component.SymbolId - 1].Target);
            add("PPS", "Winning targets match ladder", targetMatch ? Status.Pass : Status.Fail,
                targetMatch ? "exact" : "one or more targets differ from the configured ladder");

            var stakeMultiplierMatch = parameters.StakeMultiplier == combination.TotalPrize;
            add("PPS", "StakeMultiplier matches PPS total", stakeMultiplierMatch ? Status.Pass : Status.Fail,
                $"StakeMultiplier={parameters.StakeMultiplier}, PPS total={combination.TotalPrize}");
        }

        private void CheckStartingBoardCells(
            TicketDto t,
            Action<string, string, Status, string> add)
        {
            var ok = true;
            for (var r = 0; r < _settings.ROWS; r++)
            {
                for (var c = 0; c < _settings.COLS; c++)
                {
                    var id = t.StartingBoard[r][c].Id;
                    if (!IsNormalSymbolId(id))
                    {
                        ok = false;
                        add("Schema", $"StartingBoard ({r},{c}) symbol id", Status.Fail,
                            $"Id={id} must be a configured normal coin symbol in 1..{_settings.PrizeLadderRows.Count}");
                    }
                }
            }

            if (ok) add("Schema", "StartingBoard contains only valid coin symbols", Status.Pass, "ok");
        }

        private void CheckFeatureCaps(
            TicketDto t,
            int extraSpinTokenCount,
            Action<string, string, Status, string> add)
        {
            var wheelTokenCount = CountLogicalFeatureSpawns(t, _settings.F_WHEEL);
            var prizeUpgradeTokenCount = CountLogicalFeatureSpawns(t, _settings.F_PRUP);
            var flushPusherCount = t.Turns
                .Sum(turn => (turn.Pushers ?? Array.Empty<PusherDto>())
                    .Count(pusher => pusher.FeatureId == _settings.F_FLUSH_ID));

            CheckMax("WHEEL", wheelTokenCount, _settings.FeatureConfig("WHEEL").Max);
            CheckMax(
                "EXTRA_SPIN",
                extraSpinTokenCount,
                Math.Min(
                    _settings.FeatureConfig("EXTRA_SPIN").Max,
                    _settings.MAX_SPINS - _settings.BASE_SPINS));
            CheckMax("FLUSH/PUSH", flushPusherCount, _settings.FeatureConfig("FLUSH").Max);
            CheckMax("PRIZE_UPGRADE", prizeUpgradeTokenCount, _settings.FeatureConfig("PRIZE_UPGRADE").Max);

            void CheckMax(string feature, int actual, int max)
            {
                if (actual > max)
                    add("Feature", $"{feature} count within max", Status.Fail,
                        $"found {actual}, max allowed is {max}");
                else
                    add("Feature", $"{feature} count within max", Status.Pass,
                        $"{actual}/{max}");
            }
        }

        private void CheckExtraSpinTimeline(
            TicketDto t,
            Action<string, string, Status, string> add)
        {
            var ok = true;
            var availableTurns = _settings.BASE_SPINS;

            for (var turnIndex = 0; turnIndex < t.Turns.Length; turnIndex++)
            {
                var turn = t.Turns[turnIndex];
                var turnNumber = turnIndex + 1;

                if (turnNumber > availableTurns)
                {
                    ok = false;
                    add("Feature", "EXTRA_SPIN timeline earns every turn before play", Status.Fail,
                        $"turn {turnNumber} exists before the player has earned it; " +
                        $"available turns before this turn={availableTurns}, total serialized turns={t.Turns.Length}");
                }

                var extrasThisTurn = (turn.Spawns ?? Array.Empty<SpawnDto>())
                    .Where(spawn => spawn.Feature != null)
                    .Sum(spawn => CountFeatureTree(spawn.Feature!, _settings.F_XSPIN));
                var remainingFutureTurns = t.Turns.Length - turnNumber;

                if (extrasThisTurn > remainingFutureTurns)
                {
                    ok = false;
                    add("Feature", "EXTRA_SPIN timeline has enough future turns", Status.Fail,
                        $"turn {turnNumber} has {extrasThisTurn} EXTRA_SPIN token(s), but only " +
                        $"{remainingFutureTurns} future turn(s) remain");
                }

                availableTurns += extrasThisTurn;

                if (availableTurns > t.Turns.Length)
                {
                    ok = false;
                    add("Feature", "EXTRA_SPIN timeline does not over-award turns", Status.Fail,
                        $"after turn {turnNumber}, earned turns={availableTurns}, but ticket only serializes " +
                        $"{t.Turns.Length} turn(s)");
                }
            }

            if (availableTurns != t.Turns.Length)
            {
                ok = false;
                add("Feature", "EXTRA_SPIN timeline final entitlement matches ticket length", Status.Fail,
                    $"earned turns={availableTurns}, serialized turns={t.Turns.Length}");
            }

            if (ok)
                add("Feature", "EXTRA_SPIN timeline earns every serialized turn", Status.Pass, "ok");
        }

        private decimal ExpectedCashWin(TicketDto gameData, out List<string> errors)
        {
            errors = new List<string>();
            var settings = _settings;
            var total = 0m;
            var tiers = (gameData.WinInfo?.PrizeTiers ?? Array.Empty<PrizeTierDto>())
                .GroupBy(tier => tier.SymId)
                .ToDictionary(group => group.Key, group => group.First().Tier);

            foreach (var win in gameData.WinInfo?.WinSymbols ?? Array.Empty<WinSymbolDto>())
            {
                var tier = tiers.GetValueOrDefault(win.Id, 0);
                if (win.Id < 1 || win.Id > settings.PrizeLadderRows.Count)
                {
                    errors.Add($"winning symbol {win.Id} has no matching prize ladder row");
                    continue;
                }

                var row = settings.PrizeLadderRows[win.Id - 1];
                if (tier < 0 || tier >= row.Tiers.Count)
                {
                    errors.Add(
                        $"winning symbol {win.Id} declares tier {tier}, but ladder has tiers 0..{row.Tiers.Count - 1}");
                    continue;
                }

                total += row.Tiers[tier];
            }

            return total;
        }

        private void CheckTopPrizeCompletionTurn(
            TicketDto t,
            ReplayResult replay,
            Action<string, string, Status, string> add)
        {
            var topPrizeSymbol = TopPrizeSymbol();
            if (topPrizeSymbol <= 0)
                return;

            var topWin = (t.WinInfo.WinSymbols ?? Array.Empty<WinSymbolDto>())
                .FirstOrDefault(win => win.Id == topPrizeSymbol);
            if (topWin == null)
                return;

            var completionTurn = replay.CumulativeTotalsByTurn
                .Select((totals, index) => new
                {
                    Turn = index + 1,
                    Count = totals.GetValueOrDefault(topPrizeSymbol),
                })
                .FirstOrDefault(item => item.Count >= topWin.Target);

            if (completionTurn == null)
            {
                add("Payout", $"Top prize symbol {topPrizeSymbol} completes on final turn", Status.Fail,
                    $"replay never reached target {topWin.Target}");
                return;
            }

            if (completionTurn.Turn != t.WinInfo.TotalSpins)
            {
                add("Payout", $"Top prize symbol {topPrizeSymbol} completes on final turn", Status.Fail,
                    $"target {topWin.Target} first reached on turn {completionTurn.Turn}, final turn is {t.WinInfo.TotalSpins}");
                return;
            }

            add("Payout", $"Top prize symbol {topPrizeSymbol} completes on final turn", Status.Pass,
                $"turn {completionTurn.Turn}");
        }

        private int TopPrizeSymbol()
        {
            var rows = _settings.PrizeLadderRows;
            if (rows == null || rows.Count == 0) return 0;

            return rows
                .Select((row, index) => new
                {
                    Symbol = index + 1,
                    Prize = row.Tiers?.DefaultIfEmpty(0m).Max() ?? 0m,
                })
                .OrderByDescending(item => item.Prize)
                .ThenBy(item => item.Symbol)
                .First().Symbol;
        }

        private void CheckExperienceWarnings(
            TicketDto t,
            ReplayResult replay,
            Action<string, string, Status, string> add)
        {
            var repeatedPusherPattern = t.Turns
                .Select((turn, index) => new
                {
                    Turn = index + 1,
                    Pattern = string.Join(",", (turn.Pushers ?? Array.Empty<PusherDto>()).Select(p => p.PushValue)),
                })
                .GroupBy(item => item.Pattern)
                .Where(group => group.Key.Length > 0 && group.Count() >= 3)
                .OrderByDescending(group => group.Count())
                .FirstOrDefault();
            if (repeatedPusherPattern != null)
            {
                add("Experience", "Pusher pattern variety", Status.Warning,
                    $"pattern [{repeatedPusherPattern.Key}] appears {repeatedPusherPattern.Count()} time(s), turns " +
                    string.Join(",", repeatedPusherPattern.Select(item => item.Turn)));
            }
            else
            {
                add("Experience", "Pusher pattern variety", Status.Pass, "ok");
            }

            var repeatedPusherBag = t.Turns
                .Select((turn, index) => new
                {
                    Turn = index + 1,
                    Bag = string.Join(",", (turn.Pushers ?? Array.Empty<PusherDto>())
                        .Select(p => p.PushValue)
                        .OrderBy(value => value)),
                })
                .GroupBy(item => item.Bag)
                .Where(group => group.Key.Length > 0 && group.Count() >= 3)
                .OrderByDescending(group => group.Count())
                .FirstOrDefault();
            if (repeatedPusherBag != null)
            {
                add("Experience", "Pusher bag variety", Status.Warning,
                    $"push-value bag [{repeatedPusherBag.Key}] appears {repeatedPusherBag.Count()} time(s), turns " +
                    string.Join(",", repeatedPusherBag.Select(item => item.Turn)));
            }
            else
            {
                add("Experience", "Pusher bag variety", Status.Pass, "ok");
            }

            var wheelStackValues = replay.WheelFireEvents.Select(w => w.WheelStackValue).ToArray();
            if (wheelStackValues.Length >= 3 && wheelStackValues.Distinct().Count() == 1)
            {
                add("Experience", "WHEEL stack value variety", Status.Warning,
                    $"{wheelStackValues.Length} WHEEL fire(s) all used WheelStackValue={wheelStackValues[0]}");
            }
            else
            {
                add("Experience", "WHEEL stack value variety", Status.Pass,
                    wheelStackValues.Length == 0 ? "no wheels" : string.Join(",", wheelStackValues));
            }
        }

        private void CheckWinInfoSchema(
            TicketDto t,
            Action<string, string, Status, string> add)
        {
            var ok = true;
            var winIds = new HashSet<int>();
            foreach (var win in t.WinInfo.WinSymbols ?? Array.Empty<WinSymbolDto>())
            {
                if (!IsNormalSymbolId(win.Id))
                {
                    ok = false;
                    add("WinInfo", $"Win symbol {win.Id} id", Status.Fail,
                        $"winning symbol id must be a configured normal coin symbol in 1..{_settings.PrizeLadderRows.Count}, got {win.Id}");
                }
                else if (win.Target != _settings.SymbolFillCap(win.Id))
                {
                    ok = false;
                    add("WinInfo", $"Win symbol {win.Id} target", Status.Fail,
                        $"Target={win.Target}, configured ladder target={_settings.SymbolFillCap(win.Id)}");
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
                if (!IsNormalSymbolId(nonWin.Id))
                {
                    ok = false;
                    add("WinInfo", $"Non-win symbol {nonWin.Id} id", Status.Fail,
                        $"non-winning symbol id must be a configured normal coin symbol in 1..{_settings.PrizeLadderRows.Count}, got {nonWin.Id}");
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

                if (nonWin.MinTarget <= 0 || nonWin.MinTarget >= nonWin.MaxThreshold)
                {
                    ok = false;
                    add("WinInfo", $"Non-win symbol {nonWin.Id} threshold", Status.Fail,
                        $"MinTarget={nonWin.MinTarget}, MaxThreshold={nonWin.MaxThreshold}; expected positive min and min < max");
                }

                if (IsNormalSymbolId(nonWin.Id)
                    && nonWin.MaxThreshold != _settings.SymbolFillCap(nonWin.Id))
                {
                    ok = false;
                    add("WinInfo", $"Non-win symbol {nonWin.Id} configured threshold", Status.Fail,
                        $"MaxThreshold={nonWin.MaxThreshold}, configured ladder threshold={_settings.SymbolFillCap(nonWin.Id)}");
                }

                if (nonWin.PrizeTier.HasValue && nonWin.PrizeTier.Value <= 0)
                {
                    ok = false;
                    add("WinInfo", $"Non-win symbol {nonWin.Id} prize tier", Status.Fail,
                        $"PrizeTier={nonWin.PrizeTier} must be positive when present");
                }

                if (IsNormalSymbolId(nonWin.Id))
                {
                    if (nonWin.PrizeTier.HasValue)
                    {
                        var row = _settings.PrizeLadderRows[nonWin.Id - 1];
                        var tier = nonWin.PrizeTier.Value;
                        if (tier <= 0 || tier >= row.Tiers.Count)
                        {
                            ok = false;
                            add("WinInfo", $"Non-win symbol {nonWin.Id} prize tier range", Status.Fail,
                                $"PrizeTier={tier}, configured tiers are 0..{row.Tiers.Count - 1}");
                        }
                        else if (nonWin.PrizeValue != row.Tiers[tier])
                        {
                            ok = false;
                            add("WinInfo", $"Non-win symbol {nonWin.Id} prize value", Status.Fail,
                                $"PrizeValue={nonWin.PrizeValue}, configured tier {tier} value={row.Tiers[tier]}");
                        }
                    }
                    else if (nonWin.PrizeValue.HasValue)
                    {
                        ok = false;
                        add("WinInfo", $"Non-win symbol {nonWin.Id} prize value without tier", Status.Fail,
                            $"PrizeValue={nonWin.PrizeValue} is present while PrizeTier is null");
                    }
                }
            }

            var prizeTierIds = new HashSet<int>();
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

                if (IsNormalSymbolId(tier.SymId)
                    && (tier.Tier <= 0 || tier.Tier >= _settings.PrizeLadderRows[tier.SymId - 1].Tiers.Count))
                {
                    ok = false;
                    add("WinInfo", $"Prize tier sym {tier.SymId} configured range", Status.Fail,
                        $"Tier={tier.Tier}, configured upgrade tiers are 1..{_settings.PrizeLadderRows[tier.SymId - 1].Tiers.Count - 1}");
                }

                if (!prizeTierIds.Add(tier.SymId))
                {
                    ok = false;
                    add("WinInfo", $"Prize tier sym {tier.SymId} duplicate", Status.Fail,
                        "same symbol appears more than once in WinInfo.PrizeTiers");
                }
            }

            if (ok) add("WinInfo", "Declarations are internally consistent", Status.Pass, "ok");
        }

        private bool CheckFeatureSchema(
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

                    if (spawn.Id == _settings.F_FLUSH_ID)
                    {
                        ok = false;
                        add("Schema", $"{prefix} board symbol", Status.Fail,
                            $"Id={_settings.F_FLUSH_ID} is FLUSH/PUSH pusher-only and must not appear as a board spawn");
                    }

                    if (spawn.Feature == null)
                    {
                        if (!IsNormalSymbolId(spawn.Id))
                        {
                            ok = false;
                            add("Schema", $"{prefix} normal symbol id", Status.Fail,
                                $"Id={spawn.Id} must be a configured normal coin symbol in 1..{_settings.PrizeLadderRows.Count}");
                        }

                        if (spawn.Stack.HasValue
                            && (spawn.Stack.Value < 1 || spawn.Stack.Value > _settings.MAX_COIN_STACK))
                        {
                            ok = false;
                            add("Schema", $"{prefix} normal stack", Status.Fail,
                                $"Stack={spawn.Stack} must be in 1..{_settings.MAX_COIN_STACK}");
                        }

                        continue;
                    }

                    if (spawn.Stack.HasValue)
                    {
                        ok = false;
                        add("Schema", $"{prefix} feature stack", Status.Fail,
                            $"Stack={spawn.Stack} is only valid on normal coin spawns");
                    }

                    ok &= CheckFeatureTree(spawn.Feature, spawn.Id, prefix, add, depth: 0, parentFeatureId: null);
                }
            }

            if (ok) add("Schema", "All feature payloads valid", Status.Pass, "ok");
            return ok;
        }

        private bool CheckFeatureTree(
            FeatureDto feature,
            int owningSpawnId,
            string prefix,
            Action<string, string, Status, string> add,
            int depth,
            int? parentFeatureId)
        {
            var ok = true;
            if (!_settings.IsFeat(feature.FeatureId))
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

            if (depth > 0
                && (feature.FeatureId != _settings.F_PRUP
                    || (parentFeatureId != _settings.F_XSPIN && parentFeatureId != _settings.F_PRUP)))
            {
                ok = false;
                add("Schema", $"{prefix} ReTrigger payload type", Status.Fail,
                    "only PRIZE_UPGRADE may be nested, under an EXTRA_GO or PRIZE_UPGRADE chain start");
            }

            if (feature.ReTrigger is { Length: > 1 })
            {
                ok = false;
                add("Schema", $"{prefix} ReTrigger count", Status.Fail,
                    $"ReTrigger has {feature.ReTrigger.Length} child feature(s); max allowed is 1");
            }

            if (feature.ReTrigger is { Length: 1 } && feature.ConvertToId != feature.ReTrigger[0].FeatureId)
            {
                ok = false;
                add("Schema", $"{prefix} ReTrigger convert target", Status.Fail,
                    $"ConvertToId={feature.ConvertToId} must equal nested FeatureId={feature.ReTrigger[0].FeatureId}");
            }
            else if (feature.ReTrigger is not { Length: 1 } && !IsNormalSymbolId(feature.ConvertToId))
            {
                ok = false;
                add("Schema", $"{prefix} convert target", Status.Fail,
                    $"ConvertToId={feature.ConvertToId} must be a normal symbol id when no ReTrigger child is present");
            }

            if (feature.FeatureId == _settings.F_WHEEL)
            {
                if (!feature.WheelSymbolId.HasValue || !IsNormalSymbolId(feature.WheelSymbolId.Value))
                {
                    ok = false;
                    add("Schema", $"{prefix} WHEEL symbol", Status.Fail,
                        $"WheelSymbolId={feature.WheelSymbolId} must be a configured normal coin symbol");
                }

                var maxPublicWheelStackValue = Math.Min(
                    Math.Min(_settings.MAX_WHEEL_STACK_VALUE, Math.Max(0, _settings.MAX_COIN_STACK - 1)),
                    MaxPublicWheelStackValue);
                if (!feature.WheelStackValue.HasValue
                    || feature.WheelStackValue.Value < _settings.MIN_WHEEL_STACK_VALUE
                    || feature.WheelStackValue.Value > maxPublicWheelStackValue)
                {
                    ok = false;
                    add("Schema", $"{prefix} WHEEL stack value", Status.Fail,
                        $"WheelStackValue={feature.WheelStackValue} must be in {_settings.MIN_WHEEL_STACK_VALUE}..{maxPublicWheelStackValue}; " +
                        $"WheelStackValue game-rule range is 1..{MaxPublicWheelStackValue}, and total stack is " +
                        $"1 + WheelStackValue, capped by MAX_COIN_STACK={_settings.MAX_COIN_STACK}");
                }
            }
            else if (feature.WheelSymbolId.HasValue || feature.WheelStackValue.HasValue)
            {
                ok = false;
                add("Schema", $"{prefix} non-WHEEL payload", Status.Fail,
                    "WheelSymbolId/WheelStackValue are only valid for WHEEL features");
            }

            if (feature.FeatureId == _settings.F_PRUP)
            {
                if (!feature.UpgradeSymbolId.HasValue || !IsNormalSymbolId(feature.UpgradeSymbolId.Value))
                {
                    ok = false;
                    add("Schema", $"{prefix} PRIZE_UPGRADE symbol", Status.Fail,
                        $"UpgradeSymbolId={feature.UpgradeSymbolId} must be a configured normal coin symbol");
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
                ok &= CheckFeatureTree(
                    child,
                    child.FeatureId,
                    $"{prefix} ReTrigger->{child.FeatureId}",
                    add,
                    depth + 1,
                    feature.FeatureId);

            return ok;
        }

        private void CheckWinningRoundPolicy(
            TicketDto t,
            ReplayResult replay,
            Action<string, string, Status, string> add)
        {
            var totalWin = ExpectedCashWin(t, out var payoutErrors);
            if (payoutErrors.Count > 0)
            {
                add("WinningRound", "Prize-band rule resolved", Status.Fail,
                    "cannot resolve prize band: " + string.Join("; ", payoutErrors));
                return;
            }

            var matchingRules = _settings.WinningRoundRules
                .Where(rule => rule.Matches(totalWin))
                .ToArray();
            if (matchingRules.Length != 1)
            {
                add("WinningRound", "Prize-band rule resolved", Status.Fail,
                    $"CashWin={totalWin} matched {matchingRules.Length} winning-round rule(s), expected exactly one");
                return;
            }

            var rule = matchingRules[0];
            var extraGoCount = CountLogicalFeatureSpawns(t, _settings.F_XSPIN);
            if (!rule.ExtraGoCounts.Contains(extraGoCount))
            {
                add("WinningRound", "Extra Go count matches prize band", Status.Fail,
                    $"CashWin={totalWin} allows Extra Go [{string.Join(",", rule.ExtraGoCounts)}], found {extraGoCount}");
            }
            else
            {
                add("WinningRound", "Extra Go count matches prize band", Status.Pass,
                    $"CashWin={totalWin}, Extra Go={extraGoCount}");
            }

            var wins = t.WinInfo.WinSymbols ?? Array.Empty<WinSymbolDto>();
            if (wins.Length == 0)
            {
                if (rule.MinWinningTurn.HasValue || rule.MaxWinningTurn.HasValue)
                {
                    add("WinningRound", "No-win rule has no completion turn", Status.Fail,
                        $"CashWin={totalWin} resolved to a rule with a winning-turn range");
                }
                else
                {
                    add("WinningRound", "No-win rule has no completion turn", Status.Pass, "not applicable");
                }

                return;
            }

            var completionTurn = replay.CumulativeTotalsByTurn
                .Select((totals, index) => new { Turn = index + 1, Totals = totals })
                .FirstOrDefault(item => wins.All(win => item.Totals.GetValueOrDefault(win.Id) >= win.Target))
                ?.Turn;
            if (!completionTurn.HasValue)
            {
                add("WinningRound", "All winning symbols complete in allowed round", Status.Fail,
                    "replay never completed every declared winning-symbol target");
                return;
            }

            var minTurn = rule.MinWinningTurn.GetValueOrDefault();
            var maxTurn = rule.MaxWinningTurn.GetValueOrDefault();
            if (!rule.MinWinningTurn.HasValue
                || !rule.MaxWinningTurn.HasValue
                || completionTurn.Value < minTurn
                || completionTurn.Value > maxTurn)
            {
                add("WinningRound", "All winning symbols complete in allowed round", Status.Fail,
                    $"CashWin={totalWin} completed all wins on turn {completionTurn.Value}; " +
                    $"allowed range is {rule.MinWinningTurn?.ToString() ?? "none"}..{rule.MaxWinningTurn?.ToString() ?? "none"}");
                return;
            }

            add("WinningRound", "All winning symbols complete in allowed round", Status.Pass,
                $"CashWin={totalWin}, completion turn={completionTurn.Value}, allowed={minTurn}..{maxTurn}");
        }

        private void CheckPrizeUpgradeValues(
            TicketDto ticket,
            Action<string, string, Status, string> add)
        {
            var tiersBySymbol = new Dictionary<int, int>();
            var ok = true;

            for (var turnIndex = 0; turnIndex < ticket.Turns.Length; turnIndex++)
            {
                foreach (var spawn in (ticket.Turns[turnIndex].Spawns ?? Array.Empty<SpawnDto>())
                         .OrderBy(item => item.Pos))
                {
                    if (spawn.Feature == null) continue;

                    foreach (var feature in FlattenFeatureTree(spawn.Feature))
                    {
                        if (feature.FeatureId != _settings.F_PRUP
                            || !feature.UpgradeSymbolId.HasValue
                            || !IsNormalSymbolId(feature.UpgradeSymbolId.Value))
                        {
                            continue;
                        }

                        var symbol = feature.UpgradeSymbolId.Value;
                        var tier = tiersBySymbol.GetValueOrDefault(symbol) + 1;
                        tiersBySymbol[symbol] = tier;
                        var row = _settings.PrizeLadderRows[symbol - 1];
                        var name = $"Turn {turnIndex + 1} Pos {spawn.Pos} PRIZE_UPGRADE sym {symbol} tier {tier} value";

                        if (tier >= row.Tiers.Count)
                        {
                            ok = false;
                            add("Feature", name, Status.Fail,
                                $"token advances to tier {tier}, but configured tiers are 0..{row.Tiers.Count - 1}");
                        }
                        else if (feature.UpgradePrizeValue != row.Tiers[tier])
                        {
                            ok = false;
                            add("Feature", name, Status.Fail,
                                $"UpgradePrizeValue={feature.UpgradePrizeValue}, configured value={row.Tiers[tier]}");
                        }
                    }
                }
            }

            if (ok)
                add("Feature", "Every PRIZE_UPGRADE value matches its sequential ladder tier", Status.Pass, "ok");
        }

        private void CheckFeatureTiming(
            TicketDto ticket,
            Action<string, string, Status, string> add)
        {
            var ok = true;
            for (var turnIndex = 0; turnIndex < ticket.Turns.Length; turnIndex++)
            {
                var turn = turnIndex + 1;
                foreach (var spawn in ticket.Turns[turnIndex].Spawns ?? Array.Empty<SpawnDto>())
                {
                    if (spawn.Feature == null) continue;
                    foreach (var feature in FlattenFeatureTree(spawn.Feature))
                    {
                        var config = FeatureConfigForId(feature.FeatureId);
                        if (!config.HasValue) continue;
                        if (turn < config.Value.MinS || turn > config.Value.MaxS)
                        {
                            ok = false;
                            add("Feature", $"Turn {turn} Pos {spawn.Pos} feature {feature.FeatureId} timing",
                                Status.Fail,
                                $"turn {turn} is outside configured {config.Value.MinS}..{config.Value.MaxS}");
                        }
                    }
                }

                foreach (var (pusher, column) in (ticket.Turns[turnIndex].Pushers ?? Array.Empty<PusherDto>())
                         .Select((item, column) => (item, column)))
                {
                    if (pusher.FeatureId != _settings.F_FLUSH_ID) continue;
                    var config = _settings.FeatureConfig("FLUSH");
                    if (turn < config.MinS || turn > config.MaxS)
                    {
                        ok = false;
                        add("Feature", $"Turn {turn} col {column} FLUSH/PUSH timing", Status.Fail,
                            $"turn {turn} is outside configured {config.MinS}..{config.MaxS}");
                    }
                }
            }

            if (ok)
                add("Feature", "Every feature occurs inside its configured turn window", Status.Pass, "ok");
        }

        private (double P, int Max, int MinS, int MaxS, int Ord)? FeatureConfigForId(int featureId)
        {
            if (featureId == _settings.F_WHEEL) return _settings.FeatureConfig("WHEEL");
            if (featureId == _settings.F_XSPIN) return _settings.FeatureConfig("EXTRA_SPIN");
            if (featureId == _settings.F_PRUP) return _settings.FeatureConfig("PRIZE_UPGRADE");
            return null;
        }

        private static IEnumerable<FeatureDto> FlattenFeatureTree(FeatureDto feature)
        {
            yield return feature;
            foreach (var child in feature.ReTrigger ?? Array.Empty<FeatureDto>())
            {
                foreach (var nested in FlattenFeatureTree(child))
                    yield return nested;
            }
        }

        private sealed class ReplayCell
        {
            public int Sym;
            public int Stack = 1;
            public bool IsFeat;
            public int FeatureId;
            public int ConvertToId;
            public int WheelSymbolId;
            public int WheelStackValue;
            public FeatureDto[] ReTrigger = Array.Empty<FeatureDto>();
        }

        private sealed class WheelFireEvent
        {
            public int Turn;
            public int WheelSymbolId;
            public int WheelStackValue;
            public int ActualMultiplier;
            public int AffectedCellCount;
            public int OverflowAttemptCount;
            public string OverflowDetails = "";
            public bool SelfConversionAffected;
        }

        private sealed class SpawnBalance
        {
            public int Turn;
            public int EmptySlotsBeforeSpawns;
            public int SpawnCount;
            public List<int> OverwrittenSpawnPositions = new();
        }

        private sealed class ReplayResult
        {
            public Dictionary<int, int> Totals = new();
            public List<int> MissingCellTurns = new();
            public List<WheelFireEvent> WheelFireEvents = new();
            public Dictionary<int, int> PrupFinalTierPerSymbol = new();
            public List<SpawnBalance> SpawnBalances = new();
            public List<Dictionary<int, int>> CumulativeTotalsByTurn = new();
        }

        private ReplayResult ReplayTicket(TicketDto t)
        {
            var result = new ReplayResult();
            var board = new ReplayCell?[_settings.ROWS, _settings.COLS];

            for (int r = 0; r < _settings.ROWS; r++)
            {
                for (int c = 0; c < _settings.COLS; c++)
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
                for (int r = 0; r < _settings.ROWS; r++)
                {
                    for (int c = 0; c < _settings.COLS; c++)
                    {
                        if (board[r, c]?.IsFeat == true)
                            board[r, c] = ConvertCell(board[r, c]!);
                    }
                }

                // Phase 2: Collect
                for (int c = 0; c < _settings.COLS; c++)
                {
                    var pusher = turn.Pushers[c];
                    bool isFlush = pusher.FeatureId == _settings.F_FLUSH_ID;
                    if (isFlush)
                    {
                        for (int r = 0; r < _settings.ROWS; r++)
                        {
                            if (board[r, c] != null) Acc(result.Totals, board[r, c]!);
                            board[r, c] = null;
                        }
                    }
                    else
                    {
                        int push = pusher.PushValue;
                        for (int r = _settings.ROWS - push; r < _settings.ROWS; r++)
                            if (board[r, c] != null)
                                Acc(result.Totals, board[r, c]!);
                        for (int r = _settings.ROWS - 1; r >= 0; r--)
                        {
                            int src = r - push;
                            board[r, c] = src >= 0 ? board[src, c] : null;
                        }
                    }
                }

                result.CumulativeTotalsByTurn.Add(result.Totals.ToDictionary(kv => kv.Key, kv => kv.Value));

                // Phase 3: Rotate 90 clockwise
                board = RotCW(board);

                var spawnBalance = new SpawnBalance
                {
                    Turn = turnIdx + 1,
                    EmptySlotsBeforeSpawns = CountEmptyCells(board),
                    SpawnCount = (turn.Spawns ?? Array.Empty<SpawnDto>()).Length,
                };

                // Phase 4: ApplySpawns
                foreach (var sp in turn.Spawns ?? Array.Empty<SpawnDto>())
                {
                    int r = sp.Pos / _settings.COLS, c = sp.Pos % _settings.COLS;
                    if (board[r, c] != null)
                        spawnBalance.OverwrittenSpawnPositions.Add(sp.Pos);

                    var cell = new ReplayCell { Sym = sp.Id, Stack = sp.Stack ?? 1 };
                    if (sp.Feature != null)
                    {
                        cell.IsFeat = true;
                        cell.FeatureId = sp.Feature.FeatureId;
                        cell.ConvertToId = sp.Feature.ConvertToId;
                        cell.WheelSymbolId = sp.Feature.WheelSymbolId ?? 0;
                        cell.WheelStackValue = sp.Feature.WheelStackValue ?? 0;
                        cell.ReTrigger = sp.Feature.ReTrigger ?? Array.Empty<FeatureDto>();

                        AccumulatePrizeUpgradeTokens(sp.Feature, result);
                    }

                    board[r, c] = cell;
                }

                result.SpawnBalances.Add(spawnBalance);

                // Check: no cell left null after spawns
                bool anyMissing = false;
                for (int r = 0; r < _settings.ROWS; r++)
                {
                    for (int c = 0; c < _settings.COLS; c++)
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

        private void FireFeaturePass(
            ReplayCell?[,] board,
            ReplayResult result,
            int turn,
            bool wheelPass)
        {
            bool any;
            do
            {
                any = false;
                for (int r = 0; r < _settings.ROWS; r++)
                {
                    for (int c = 0; c < _settings.COLS; c++)
                    {
                        var fc = board[r, c];
                        if (fc?.IsFeat != true) continue;

                        var isWheel = fc.FeatureId == _settings.F_WHEEL;
                        if (isWheel != wheelPass) continue;

                        if (isWheel)
                            ApplyWheelEffect(board, result, turn, fc);

                        board[r, c] = fc.ReTrigger is { Length: > 0 }
                            ? FeatureCell(fc.ReTrigger[0])
                            : ConvertCell(fc);
                        any = true;
                    }
                }
            } while (any && BoardHasFeatureCell(board, wheelPass));
        }

        private void ApplyWheelEffect(
            ReplayCell?[,] board,
            ReplayResult result,
            int turn,
            ReplayCell fc)
        {
            if (fc.WheelStackValue + 1 <= 1) return;

            int multiplier = fc.WheelStackValue + 1;
            int sym = fc.WheelSymbolId;
            int affected = 0;
            int overflowAttempts = 0;
            var overflowDetails = new List<string>();
            for (int rr = 0; rr < _settings.ROWS; rr++)
            {
                for (int cc = 0; cc < _settings.COLS; cc++)
                {
                    var cell = board[rr, cc];
                    if (cell == null || cell.IsFeat || cell.Sym != sym) continue;

                    var before = cell.Stack;
                    var attempted = cell.Stack + multiplier - 1;
                    if (attempted > _settings.MAX_COIN_STACK)
                    {
                        overflowAttempts++;
                        overflowDetails.Add($"({rr},{cc}) {before}+{multiplier - 1}->{attempted}");
                    }

                    cell.Stack = Math.Min(_settings.MAX_COIN_STACK, attempted);
                    if (cell.Stack > before)
                        affected++;
                }
            }

            result.WheelFireEvents.Add(new WheelFireEvent
            {
                Turn = turn,
                WheelSymbolId = sym,
                WheelStackValue = fc.WheelStackValue,
                ActualMultiplier = multiplier,
                AffectedCellCount = affected,
                OverflowAttemptCount = overflowAttempts,
                OverflowDetails = string.Join(", ", overflowDetails),
                SelfConversionAffected = WheelSelfConversionAffected(fc),
            });
        }

        private bool WheelSelfConversionAffected(ReplayCell fc)
        {
            try
            {
                return fc.WheelSymbolId == ResolveConvert(fc) && fc.WheelStackValue > 0;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private ReplayCell FeatureCell(FeatureDto feature) =>
            new()
            {
                Sym = feature.FeatureId,
                IsFeat = true,
                FeatureId = feature.FeatureId,
                ConvertToId = feature.ConvertToId,
                WheelSymbolId = feature.WheelSymbolId ?? 0,
                WheelStackValue = feature.WheelStackValue ?? 0,
                ReTrigger = feature.ReTrigger ?? Array.Empty<FeatureDto>(),
            };

        private ReplayCell ConvertCell(ReplayCell fc)
        {
            var convertTo = ResolveConvert(fc);
            var converted = new ReplayCell { Sym = convertTo, Stack = 1 };
            if (fc.FeatureId == _settings.F_WHEEL && fc.WheelSymbolId == convertTo)
            {
                converted.Stack = Math.Min(
                    _settings.MAX_COIN_STACK,
                    Math.Max(1, fc.WheelStackValue + 1));
            }

            return converted;
        }

        /// <summary>
        /// Resolves the REAL eventual symbol a feature cell converts to. When a
        /// ConvertToId points at another feature and ReTrigger contains that nested
        /// feature, walk the chain until the deepest link's real conversion target.
        /// </summary>
        private int ResolveConvert(ReplayCell fc)
        {
            int convertTo;
            if (_settings.IsFeat(fc.ConvertToId) && fc.ReTrigger.Length > 0)
            {
                var link = fc.ReTrigger[0];
                while (link.ReTrigger is { Length: > 0 })
                    link = link.ReTrigger[0];
                convertTo = link.ConvertToId;
            }
            else
            {
                convertTo = fc.ConvertToId;
            }

            if (!IsNormalSymbolId(convertTo))
            {
                throw new InvalidOperationException(
                    $"Feature replay resolved invalid ConvertToId={convertTo}; " +
                    "feature conversion must target a normal symbol.");
            }

            return convertTo;
        }

        private bool IsNormalSymbolId(int symbol) =>
            symbol >= 1
            && symbol <= _settings.PrizeLadderRows.Count
            && !_settings.IsFeat(symbol)
            && symbol != _settings.F_FLUSH_ID;

        private bool BoardHasFeatureCell(ReplayCell?[,] board, bool wheelPass)
        {
            for (int r = 0; r < _settings.ROWS; r++)
            {
                for (int c = 0; c < _settings.COLS; c++)
                {
                    var cell = board[r, c];
                    if (cell?.IsFeat == true && (cell.FeatureId == _settings.F_WHEEL) == wheelPass)
                        return true;
                }
            }

            return false;
        }

        private int CountEmptyCells(ReplayCell?[,] board)
        {
            int count = 0;
            for (int r = 0; r < _settings.ROWS; r++)
            {
                for (int c = 0; c < _settings.COLS; c++)
                {
                    if (board[r, c] == null) count++;
                }
            }

            return count;
        }

        private void Acc(Dictionary<int, int> totals, ReplayCell cell)
        {
            if (_settings.IsFeat(cell.Sym)) return;
            totals.TryGetValue(cell.Sym, out int existing);
            totals[cell.Sym] = existing + cell.Stack;
        }

        private ReplayCell?[,] RotCW(ReplayCell?[,] b)
        {
            var r = new ReplayCell?[_settings.ROWS, _settings.COLS];
            for (int row = 0; row < _settings.ROWS; row++)
            {
                for (int col = 0; col < _settings.COLS; col++)
                {
                    r[col, _settings.ROWS - 1 - row] = b[row, col];
                }
            }

            return r;
        }

        private int CountLogicalFeatureSpawns(TicketDto t, int featureId) =>
            t.Turns
                .SelectMany(turn => turn.Spawns ?? Array.Empty<SpawnDto>())
                .Where(spawn => spawn.Feature != null)
                .Sum(spawn => CountFeatureTree(spawn.Feature!, featureId));

        private int CountFeatureTree(FeatureDto feature, int featureId) =>
            (feature.FeatureId == featureId ? 1 : 0)
            + (feature.ReTrigger ?? Array.Empty<FeatureDto>())
            .Sum(child => CountFeatureTree(child, featureId));

        private int MaxReTriggerDepth(FeatureDto feature)
        {
            if (feature.ReTrigger == null || feature.ReTrigger.Length == 0) return 0;
            return 1 + feature.ReTrigger.Max(MaxReTriggerDepth);
        }

        private void AccumulatePrizeUpgradeTokens(FeatureDto feature, ReplayResult result)
        {
            if (feature.FeatureId == _settings.F_PRUP && feature.UpgradeSymbolId.HasValue)
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
}
