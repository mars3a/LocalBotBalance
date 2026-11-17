using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace LocalBotBalancer;

/// <summary>
/// Plugin configuration. CounterStrikeSharp creates and loads the file automatically:
/// addons/counterstrikesharp/configs/plugins/LocalBotBalancer/LocalBotBalancer.json
/// </summary>
public sealed class PluginConfig : BasePluginConfig
{
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; } = true;

    /// <summary>Language of chat/console replies: "en" or "ru". Log lines are always English.</summary>
    [JsonPropertyName("Language")] public string Language { get; set; } = "en";

    /// <summary>Match cap used by this plugin (humans + bots). Not the server's connection limit.</summary>
    [JsonPropertyName("MaxPlayers")] public int MaxPlayers { get; set; } = 10;

    /// <summary>
    /// true: fill each team with bots up to half of MaxPlayers (minus reserved slots).
    /// false: only balance, i.e. bring the weaker team up to the human count of the stronger one.
    /// </summary>
    [JsonPropertyName("FillEmptySlots")] public bool FillEmptySlots { get; set; } = true;

    /// <summary>Free places left per team when filling with bots. Used only when FillEmptySlots=true.</summary>
    [JsonPropertyName("ReservedHumanSlotsPerTeam")] public int ReservedHumanSlotsPerTeam { get; set; } = 1;

    [JsonPropertyName("RemoveBotsForHumans")] public bool RemoveBotsForHumans { get; set; } = true;
    [JsonPropertyName("ApplyOnlyBetweenRounds")] public bool ApplyOnlyBetweenRounds { get; set; } = true;

    /// <summary>false: target is 0 bots per team (existing bots are removed if RemoveBotsForHumans=true).</summary>
    [JsonPropertyName("BalanceTeams")] public bool BalanceTeams { get; set; } = true;

    [JsonPropertyName("BotDifficulty")] public int BotDifficulty { get; set; } = 2;
    [JsonPropertyName("MaximumBots")] public int MaximumBots { get; set; } = 10;
    [JsonPropertyName("ReconcileDelaySeconds")] public float ReconcileDelaySeconds { get; set; } = 0.5f;
    [JsonPropertyName("Debug")] public bool Debug { get; set; } = false;
}

public sealed class LocalBotBalancerPlugin : BasePlugin, IPluginConfig<PluginConfig>
{
    public override string ModuleName => "LocalBotBalancer";
    public override string ModuleVersion => "1.2.0";
    public override string ModuleAuthor => "Local server";
    public override string ModuleDescription => "Round-safe human/bot team balancing for a private CS2 server.";

    public PluginConfig Config { get; set; } = new();

    // How many removal rounds (kick, wait, recount) are tried before giving up for the current round.
    private const int MaxRemovalAttempts = 3;

    private bool _enabled = true;          // runtime switch (css_lbb_enable); initialised from config
    private bool _safeToApply;             // true between round_end and the next round_start
    private bool _awaitingFirstHuman;      // no human on a team yet: the first one triggers an immediate fill
    private bool _applyQueued;
    private int _removalAttempts;
    private string? _lastDebugLine;
    private string? _lastWarning;

    // ---------------------------------------------------------------- lifecycle

    /// <summary>
    /// Called by CounterStrikeSharp after the config file is parsed (before Load).
    /// Only static sanity checks live here. Anything that depends on the running server
    /// (Server.MaxPlayers) is evaluated at use time, because it is not reliable this early.
    /// </summary>
    public void OnConfigParsed(PluginConfig config)
    {
        config.MaxPlayers = Math.Max(2, config.MaxPlayers);
        config.BotDifficulty = Math.Clamp(config.BotDifficulty, 0, 3);
        config.MaximumBots = Math.Clamp(config.MaximumBots, 0, config.MaxPlayers);
        config.ReservedHumanSlotsPerTeam = Math.Clamp(config.ReservedHumanSlotsPerTeam, 0, config.MaxPlayers / 2);
        config.ReconcileDelaySeconds = Math.Clamp(config.ReconcileDelaySeconds, 0.5f, 15.0f);
        config.Language = string.Equals(config.Language, "ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en";

        Config = config;
        _enabled = config.Enabled;
    }

    public override void Load(bool hotReload)
    {
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);

        // A late load on a server without humans on teams must still fill at once for the first one.
        if (hotReload)
            _awaitingFirstHuman = !GetPlayers().Any(p =>
                !p.IsBot && p.Team is CsTeam.CounterTerrorist or CsTeam.Terrorist);

        AddTimer(5.0f, Poll, TimerFlags.REPEAT);
        Logger.LogInformation("Loaded (hotReload={HotReload}).", hotReload);
    }

    public override void Unload(bool hotReload)
    {
        Logger.LogInformation("Unloaded.");
    }

    // ---------------------------------------------------------------- events / timers

    private void OnMapStart(string mapName)
    {
        _safeToApply = false;
        _awaitingFirstHuman = true;
        _applyQueued = false;
        _removalAttempts = 0;
        _lastDebugLine = null;
        _lastWarning = null;
    }

    private HookResult OnRoundStart(EventRoundStart ev, GameEventInfo info)
    {
        _safeToApply = false;
        _removalAttempts = 0;
        _lastWarning = null;
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd ev, GameEventInfo info)
    {
        _safeToApply = true;
        _removalAttempts = 0;
        _lastWarning = null;
        QueueReconcile();
        return HookResult.Continue;
    }

    private HookResult OnPlayerTeam(EventPlayerTeam ev, GameEventInfo info)
    {
        QueueReconcile();
        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect ev, GameEventInfo info)
    {
        QueueReconcile();
        return HookResult.Continue;
    }

    private void Poll()
    {
        if (!_enabled) return;
        // Retry exhausted removals periodically even when no round events arrive (warmup, empty server).
        if (_removalAttempts >= MaxRemovalAttempts) _removalAttempts = 0;
        QueueReconcile();
    }

    // Safety is checked when the timer fires, not here: team changes and disconnects
    // are not yet reflected in the player list when their events arrive.
    private void QueueReconcile()
    {
        if (_applyQueued || !_enabled) return;

        _applyQueued = true;
        AddTimer(Config.ReconcileDelaySeconds, () =>
        {
            _applyQueued = false;
            if (_enabled) Reconcile();
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    // ---------------------------------------------------------------- safety window

    /// <summary>
    /// Bot changes are allowed when ApplyOnlyBetweenRounds is off, or when
    /// (a) a round has ended and the next one has not started, or
    /// (b) the server is in warmup, or
    /// (c) no human is connected, so nobody can be disturbed, or
    /// (d) the first human has just joined a team on a server without humans on teams.
    /// Cases (b)-(d) cover a fresh/empty server where round_end may never fire.
    /// </summary>
    private bool IsSafeToApply(IReadOnlyCollection<CCSPlayerController> players)
    {
        if (!Config.ApplyOnlyBetweenRounds) return true;
        if (_safeToApply) return true;
        if (_awaitingFirstHuman) return true;
        if (IsWarmup()) return true;
        return !players.Any(p => !p.IsBot);
    }

    private static bool IsWarmup()
    {
        try
        {
            var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;
            return rules?.WarmupPeriod ?? false;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- balancing

    private void Reconcile()
    {
        var players = GetPlayers();

        var humansCt = players.Count(p => !p.IsBot && p.Team == CsTeam.CounterTerrorist);
        var humansT = players.Count(p => !p.IsBot && p.Team == CsTeam.Terrorist);
        if (humansCt + humansT == 0) _awaitingFirstHuman = true;

        if (!IsSafeToApply(players)) return;

        var botsCt = players.Where(p => p.IsBot && p.Team == CsTeam.CounterTerrorist).ToList();
        var botsT = players.Where(p => p.IsBot && p.Team == CsTeam.Terrorist).ToList();

        var currentHumans = players.Count(p => !p.IsBot);
        var currentBots = players.Count(p => p.IsBot);
        var nonTeamBots = currentBots - botsCt.Count - botsT.Count;

        // bot_add/bot_kick issued from the server console adjust bot_quota by themselves, so a
        // quota equal to the bot count is the plugin's own bookkeeping. Anything else means
        // another quota manager is active.
        var botQuota = GetBotQuota();
        if (botQuota != 0 && botQuota != currentBots)
        {
            WarnOnce("Skipping bot changes: bot_quota is " +
                     (botQuota.HasValue ? botQuota.Value.ToString() : "unavailable") +
                     $" but {currentBots} bot(s) are present; set bot_quota 0, bot_quota_mode normal, " +
                     "and disable other bot quota managers.");
            return;
        }

        var maxSlots = GetMatchCap();
        var halfCap = maxSlots / 2;

        // Target number of bot-or-human "places to fill" per team.
        // FillEmptySlots=true : fill up to half the cap, keeping the reserved places free.
        // FillEmptySlots=false: only even out the humans (no reservation, no filling).
        // No human on a team: no bots at all.
        var botTeamTarget = humansCt + humansT == 0 ? 0
            : Config.FillEmptySlots
                ? Math.Max(0, halfCap - Config.ReservedHumanSlotsPerTeam)
                : Math.Min(Math.Max(humansCt, humansT), halfCap);

        var availableBotSlots = Math.Max(0, maxSlots - currentHumans - nonTeamBots);
        var targetBotsCt = Config.BalanceTeams ? Math.Max(0, botTeamTarget - humansCt) : 0;
        var targetBotsT = Config.BalanceTeams ? Math.Max(0, botTeamTarget - humansT) : 0;
        var allowedBots = Math.Min(
            Math.Min(targetBotsCt + targetBotsT, Math.Max(0, Config.MaximumBots - nonTeamBots)),
            availableBotSlots);

        var desiredCt = 0;
        var desiredT = 0;
        while (allowedBots-- > 0 && (desiredCt < targetBotsCt || desiredT < targetBotsT))
        {
            var addCt = desiredCt < targetBotsCt &&
                (desiredT >= targetBotsT || humansCt + desiredCt <= humansT + desiredT);
            if (addCt) desiredCt++;
            else desiredT++;
        }

        // ---- removal
        if (Config.RemoveBotsForHumans)
        {
            var excessCt = Math.Max(0, botsCt.Count - desiredCt);
            var excessT = Math.Max(0, botsT.Count - desiredT);

            if (excessCt + excessT == 0)
            {
                _removalAttempts = 0;
                _lastWarning = null;
            }
            else if (_removalAttempts < MaxRemovalAttempts)
            {
                var attempt = _removalAttempts++;
                var kicked = RemoveBots(botsCt, excessCt, attempt) + RemoveBots(botsT, excessT, attempt);
                // Keep the engine's quota manager from re-adding bots kicked by userid.
                if (botQuota != 0) Server.ExecuteCommand($"bot_quota {Math.Max(0, currentBots - kicked)}");
                // Recount shortly; additions happen in that next pass.
                AddTimer(0.2f, QueueReconcile, TimerFlags.STOP_ON_MAPCHANGE);
                return;
            }
            else
            {
                // Kicks did not take effect. Do not starve the add step: warn once and continue.
                WarnOnce($"Could not remove {excessCt + excessT} excess bot(s) after {MaxRemovalAttempts} " +
                         "attempts; continuing without removal this round.");
            }
        }

        // ---- addition
        var room = Math.Max(0, maxSlots - players.Count);
        var botBudget = Math.Max(0, Config.MaximumBots - currentBots);
        var addsRemaining = Math.Min(room, botBudget);
        var plannedCt = botsCt.Count;
        var plannedT = botsT.Count;

        if (addsRemaining > 0 && (plannedCt < desiredCt || plannedT < desiredT))
        {
            Server.ExecuteCommand("bot_difficulty " + Config.BotDifficulty);
            while (addsRemaining > 0 && (plannedCt < desiredCt || plannedT < desiredT))
            {
                var addCt = plannedCt < desiredCt &&
                    (plannedT >= desiredT || humansCt + plannedCt <= humansT + plannedT);
                Server.ExecuteCommand(addCt ? "bot_add_ct" : "bot_add_t");
                if (addCt) plannedCt++;
                else plannedT++;
                addsRemaining--;
            }
        }

        if (humansCt + humansT > 0) _awaitingFirstHuman = false;

        DebugLog(
            $"Humans CT/T={humansCt}/{humansT}; bots CT/T={botsCt.Count}/{botsT.Count}; " +
            $"target bots CT/T={desiredCt}/{desiredT}; reserved/team={Config.ReservedHumanSlotsPerTeam}; " +
            $"match cap={maxSlots}; bot_quota={botQuota}.");
    }

    /// <summary>
    /// Removes up to <paramref name="excess"/> bots and returns how many kicks were issued.
    /// First attempt uses "bot_kick" by sanitised name, which also lowers bot_quota. If that did
    /// not work, retries fall back to "kickid &lt;userid&gt;".
    /// </summary>
    private static int RemoveBots(List<CCSPlayerController> bots, int excess, int attempt)
    {
        if (excess <= 0) return 0;

        var kicked = 0;
        foreach (var bot in bots.Where(b => b.IsValid).Take(excess))
        {
            if (attempt > 0 && bot.UserId is int userId)
            {
                Server.ExecuteCommand($"kickid {userId}");
            }
            else
            {
                Server.ExecuteCommand($"bot_kick \"{SanitizeName(bot.PlayerName)}\"");
            }
            kicked++;
        }
        return kicked;
    }

    /// <summary>Strips characters that could break out of a quoted console argument.</summary>
    private static string SanitizeName(string name) =>
        new(name.Where(c => c != '"' && c != '\\' && c != ';' && !char.IsControl(c)).ToArray());

    // ---------------------------------------------------------------- helpers

    private static List<CCSPlayerController> GetPlayers() =>
        Utilities.GetPlayers().Where(p => p != null && p.IsValid && !p.IsHLTV).ToList();

    private int GetMatchCap()
    {
        var serverMax = Server.MaxPlayers;
        return serverMax > 0 ? Math.Min(Config.MaxPlayers, serverMax) : Config.MaxPlayers;
    }

    private static int? GetBotQuota()
    {
        try
        {
            var botQuota = ConVar.Find("bot_quota");
            if (botQuota == null) return null;
            return botQuota.GetPrimitiveValue<int>();
        }
        catch
        {
            return null;
        }
    }

    private void DebugLog(string line)
    {
        if (!Config.Debug || line == _lastDebugLine) return; // log only when something changed
        _lastDebugLine = line;
        Logger.LogInformation("{Line}", line);
    }

    private void WarnOnce(string line)
    {
        if (line == _lastWarning) return;
        _lastWarning = line;
        Logger.LogWarning("{Line}", line);
    }

    private string Msg(string en, string ru) => Config.Language == "ru" ? ru : en;

    // ---------------------------------------------------------------- commands

    [ConsoleCommand("css_lbb_status", "Show LocalBotBalancer status.")]
    [RequiresPermissions("@css/root")]
    public void StatusCommand(CCSPlayerController? player, CommandInfo command)
    {
        var players = GetPlayers();
        var hct = players.Count(p => !p.IsBot && p.Team == CsTeam.CounterTerrorist);
        var ht = players.Count(p => !p.IsBot && p.Team == CsTeam.Terrorist);
        var bct = players.Count(p => p.IsBot && p.Team == CsTeam.CounterTerrorist);
        var bt = players.Count(p => p.IsBot && p.Team == CsTeam.Terrorist);
        var botQuota = GetBotQuota();

        command.ReplyToCommand(
            $"[LBB] Humans CT/T {hct}/{ht}; bots CT/T {bct}/{bt}; " +
            $"safeNow={IsSafeToApply(players)} (betweenRounds={_safeToApply}, warmup={IsWarmup()}, " +
            $"awaitingFirstHuman={_awaitingFirstHuman}); " +
            $"bot_quota={botQuota?.ToString() ?? "unavailable"}; enabled={_enabled}");
    }

    [ConsoleCommand("css_lbb_reconcile", "Queue a safe bot balance update.")]
    [RequiresPermissions("@css/root")]
    public void ReconcileCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!_enabled)
        {
            command.ReplyToCommand(Msg(
                "[LBB] Plugin is disabled. Use css_lbb_enable 1.",
                "[LBB] Плагин выключен. Используйте css_lbb_enable 1."));
            return;
        }

        if (!IsSafeToApply(GetPlayers()))
        {
            command.ReplyToCommand(Msg(
                "[LBB] A round is in progress. Balancing will run after it ends.",
                "[LBB] Сейчас идёт раунд. Балансировка будет выполнена после его окончания."));
            return;
        }

        QueueReconcile();
        command.ReplyToCommand(Msg("[LBB] Reconcile queued.", "[LBB] Пересчёт поставлен в очередь."));
    }

    [ConsoleCommand("css_lbb_enable", "Enable/disable balancing until reload. Usage: css_lbb_enable <0|1>")]
    [RequiresPermissions("@css/root")]
    public void EnableCommand(CCSPlayerController? player, CommandInfo command)
    {
        var arg = command.ArgCount > 1 ? command.GetArg(1) : "";
        if (arg != "0" && arg != "1")
        {
            command.ReplyToCommand("[LBB] Usage: css_lbb_enable <0|1>");
            return;
        }

        _enabled = arg == "1";
        if (_enabled) QueueReconcile();
        command.ReplyToCommand(_enabled
            ? Msg("[LBB] Enabled (until plugin reload).", "[LBB] Включено (до перезагрузки плагина).")
            : Msg("[LBB] Disabled (until plugin reload).", "[LBB] Выключено (до перезагрузки плагина)."));
    }
}
