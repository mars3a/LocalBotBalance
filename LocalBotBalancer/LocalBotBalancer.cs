using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace LocalBotBalancer;

public sealed class PluginConfig
{
    public bool Enabled { get; set; } = true;
    public int MaxPlayers { get; set; } = 10;
    public bool FillEmptySlots { get; set; } = true;
    public bool RemoveBotsForHumans { get; set; } = true;
    public bool ApplyOnlyBetweenRounds { get; set; } = true;
    public bool BalanceTeams { get; set; } = true;
    public int BotDifficulty { get; set; } = 2;
    public int MinimumBots { get; set; } = 0;
    public int MaximumBots { get; set; } = 10;
    public float ReconcileDelaySeconds { get; set; } = 0.5f;
    public bool Debug { get; set; } = true;
}

public sealed class LocalBotBalancer : BasePlugin
{
    public override string ModuleName => "LocalBotBalancer";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "Local server";
    public override string ModuleDescription => "Round-safe human/bot team balancing for a private CS2 server.";

    private PluginConfig _config = new();
    private bool _safeToApply;
    private bool _applyQueued;
    private string ConfigPath => Path.Combine(ModuleDirectory, "config.json");

    public override void Load(bool hotReload)
    {
        LoadConfig();

        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);

        AddTimer(5.0f, Poll, TimerFlags.REPEAT);
        Console.WriteLine("[LocalBotBalancer] Loaded. Config: " + ConfigPath);
    }

    public override void Unload(bool hotReload)
    {
        Console.WriteLine("[LocalBotBalancer] Unloaded.");
    }

    private void LoadConfig()
    {
        try
        {
            Directory.CreateDirectory(ModuleDirectory);
            if (!File.Exists(ConfigPath))
            {
                File.WriteAllText(ConfigPath,
                    JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }

            var loaded = JsonSerializer.Deserialize<PluginConfig>(File.ReadAllText(ConfigPath));
            if (loaded != null) _config = loaded;

            _config.MaxPlayers = Math.Clamp(_config.MaxPlayers, 2, Math.Max(2, Server.MaxPlayers));
            _config.BotDifficulty = Math.Clamp(_config.BotDifficulty, 0, 3);
            _config.MinimumBots = Math.Max(0, _config.MinimumBots);
            _config.MaximumBots = Math.Clamp(_config.MaximumBots, 0, _config.MaxPlayers);
            _config.MinimumBots = Math.Min(_config.MinimumBots, _config.MaximumBots);
            _config.ReconcileDelaySeconds = Math.Clamp(_config.ReconcileDelaySeconds, 0.5f, 15.0f);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[LocalBotBalancer] Config error; defaults used: " + ex);
        }
    }

    private HookResult OnRoundStart(EventRoundStart ev, GameEventInfo info)
    {
        _safeToApply = false;
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd ev, GameEventInfo info)
    {
        _safeToApply = true;
        QueueReconcile();
        return HookResult.Continue;
    }

    private void Poll()
    {
        if (!_config.Enabled) return;
        QueueReconcile();
    }

    private void QueueReconcile()
    {
        if (_applyQueued || !_config.Enabled) return;
        if (_config.ApplyOnlyBetweenRounds)
        {
            if (!_safeToApply) return;
            if (HasLivingHumans())
            {
                _safeToApply = false;
                return;
            }
        }

        _applyQueued = true;
        AddTimer(_config.ReconcileDelaySeconds, () =>
        {
            _applyQueued = false;
            if (!_config.Enabled) return;
            if (_config.ApplyOnlyBetweenRounds)
            {
                if (!_safeToApply) return;
                if (HasLivingHumans())
                {
                    _safeToApply = false;
                    return;
                }
            }
            Reconcile();
        });
    }

    private void Reconcile()
    {
        var players = Utilities.GetPlayers()
            .Where(p => p != null && p.IsValid && !p.IsHLTV)
            .ToList();

        var humansCt = players.Count(p => !p.IsBot && p.Team == CsTeam.CounterTerrorist);
        var humansT = players.Count(p => !p.IsBot && p.Team == CsTeam.Terrorist);
        var aliveHumansCt = players.Count(IsAliveHumanOnTeamCt);
        var aliveHumansT = players.Count(IsAliveHumanOnTeamT);

        if (_config.ApplyOnlyBetweenRounds &&
            (!_safeToApply || aliveHumansCt + aliveHumansT > 0))
        {
            if (aliveHumansCt + aliveHumansT > 0) _safeToApply = false;
            return;
        }

        var botQuota = GetBotQuota();
        if (botQuota != 0)
        {
            if (_config.Debug)
            {
                Console.WriteLine(
                    $"[LocalBotBalancer] Skipping bot changes: bot_quota is " +
                    $"{(botQuota.HasValue ? botQuota.Value.ToString() : "unavailable")}; " +
                    "set it to 0 and disable other bot quota managers.");
            }
            return;
        }

        var botsCt = players.Where(p => p.IsBot && p.Team == CsTeam.CounterTerrorist).ToList();
        var botsT = players.Where(p => p.IsBot && p.Team == CsTeam.Terrorist).ToList();

        var maxSlots = Math.Min(_config.MaxPlayers, Server.MaxPlayers);
        var teamTarget = _config.FillEmptySlots
            ? maxSlots / 2
            : Math.Max(humansCt, humansT);

        teamTarget = Math.Min(teamTarget, maxSlots / 2);

        var currentHumans = players.Count(p => !p.IsBot);
        var nonTeamBots = players.Count(p => p.IsBot &&
            p.Team != CsTeam.CounterTerrorist && p.Team != CsTeam.Terrorist);
        var availableBotSlots = Math.Max(0, maxSlots - currentHumans - nonTeamBots);
        var targetBotsCt = _config.BalanceTeams ? Math.Max(0, teamTarget - humansCt) : 0;
        var targetBotsT = _config.BalanceTeams ? Math.Max(0, teamTarget - humansT) : 0;
        var allowedBots = Math.Min(
            Math.Min(targetBotsCt + targetBotsT, Math.Max(0, _config.MaximumBots - nonTeamBots)),
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

        if (_config.RemoveBotsForHumans)
        {
            var removedCt = RemoveExcessBots(botsCt, desiredCt);
            var removedT = RemoveExcessBots(botsT, desiredT);
            if (removedCt + removedT > 0)
            {
                AddTimer(0.2f, QueueReconcile);
                return;
            }
        }

        var currentBots = players.Count(p => p.IsBot);
        var room = Math.Max(0, maxSlots - players.Count);
        var botBudget = Math.Max(0, _config.MaximumBots - currentBots);
        var addsRemaining = Math.Min(room, botBudget);
        var plannedCt = botsCt.Count;
        var plannedT = botsT.Count;

        if (addsRemaining > 0 &&
            (plannedCt < desiredCt || plannedT < desiredT))
        {
            Server.ExecuteCommand("bot_difficulty " + _config.BotDifficulty);
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

        if (_config.Debug)
        {
            Console.WriteLine(
                $"[LocalBotBalancer] Humans CT/T={humansCt}/{humansT}; alive humans CT/T={aliveHumansCt}/{aliveHumansT}; " +
                $"bots CT/T={botsCt.Count}/{botsT.Count}; target CT/T={desiredCt}/{desiredT}; " +
                $"slots={maxSlots}; bot_quota={botQuota}.");
        }
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

    private static bool IsLivingHuman(CCSPlayerController player) =>
        !player.IsBot &&
        (player.Team == CsTeam.CounterTerrorist || player.Team == CsTeam.Terrorist) &&
        player.PlayerPawn.IsValid &&
        player.PlayerPawn.Value?.LifeState == (byte)LifeState_t.LIFE_ALIVE;

    private static bool IsAliveHumanOnTeamCt(CCSPlayerController player) =>
        player.Team == CsTeam.CounterTerrorist && IsLivingHuman(player);

    private static bool IsAliveHumanOnTeamT(CCSPlayerController player) =>
        player.Team == CsTeam.Terrorist && IsLivingHuman(player);

    private bool HasLivingHumans() =>
        Utilities.GetPlayers().Any(p => p != null && p.IsValid && IsLivingHuman(p));

    private int RemoveExcessBots(List<CCSPlayerController> bots, int desired)
    {
        var excess = bots.Count - desired;
        if (excess <= 0) return 0;

        var removed = 0;
        foreach (var bot in bots.Take(excess))
        {
            if (!bot.IsValid) continue;
            // bot_kick accepts a bot name. Escape quotes to avoid malformed server commands.
            var safeName = bot.PlayerName.Replace("\"", "").Replace("\\", "");
            Server.ExecuteCommand($"bot_kick \"{safeName}\"");
            removed++;
        }
        return removed;
    }

    [ConsoleCommand("css_lbb_status", "Show LocalBotBalancer status.")]
    public void StatusCommand(CCSPlayerController? player, CommandInfo command)
    {
        var players = Utilities.GetPlayers().Where(p => p != null && p.IsValid && !p.IsHLTV).ToList();
        var hct = players.Count(p => !p.IsBot && p.Team == CsTeam.CounterTerrorist);
        var ht = players.Count(p => !p.IsBot && p.Team == CsTeam.Terrorist);
        var bct = players.Count(p => p.IsBot && p.Team == CsTeam.CounterTerrorist);
        var bt = players.Count(p => p.IsBot && p.Team == CsTeam.Terrorist);
        var botQuota = GetBotQuota();
        var message = $"[LBB] Humans CT/T {hct}/{ht}; bots CT/T {bct}/{bt}; " +
            $"safeToApply={_safeToApply}; bot_quota={botQuota?.ToString() ?? "unavailable"}; " +
            $"enabled={_config.Enabled}";
        if (player != null && player.IsValid) player.PrintToChat(message);
        else Console.WriteLine(message);
    }

    [ConsoleCommand("css_lbb_reconcile", "Queue a safe bot balance update.")]
    public void ReconcileCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (_config.ApplyOnlyBetweenRounds && (!_safeToApply || HasLivingHumans()))
        {
            _safeToApply = false;
            var msg = "[LBB] Сейчас идёт раунд. Балансировка будет выполнена после его окончания.";
            if (player != null && player.IsValid) player.PrintToChat(msg);
            else Console.WriteLine(msg);
            return;
        }

        QueueReconcile();
        if (player != null && player.IsValid) player.PrintToChat("[LBB] Пересчёт поставлен в очередь.");
        else Console.WriteLine("[LBB] Reconcile queued.");
    }
}
