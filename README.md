# LocalBotBalancer 1.1.0

[Русская версия](README.ru.md)

CounterStrikeSharp C# plugin prototype for a private CS2 server.

## What changed in 1.1.0

- Config is now a standard CounterStrikeSharp plugin config
  (`IPluginConfig`). It lives in `configs/plugins/LocalBotBalancer/`, so
  updating the plugin DLL no longer overwrites your settings.
- `MaxPlayers` is no longer clamped by `Server.MaxPlayers` at load time (that
  value is unreliable during server start and could silently shrink the whole
  config). The server limit is applied when balancing runs.
- Bot removal uses `kickid <userid>` first and falls back to `bot_kick "name"`.
  Removal is retried at most 3 times per round; if it keeps failing, the plugin
  logs a warning and still adds bots for the other team instead of looping.
- Bots can now be managed during warmup and on a server with no humans, not
  only after the first `round_end`.
- Removed the unused `MinimumBots` option and the diagnostic-only alive-human
  counters. Old configs containing `MinimumBots` still load; the key is ignored.
- `FillEmptySlots=false` now has a clear meaning (see below).
- Commands require the `@css/root` permission; `css_lbb_enable` added.
- `Debug` is off by default and logs only when the state changes.
- Uses the plugin `Logger`; chat/console replies follow the `Language` setting.

## Important behavior

- **Safe window.** With `ApplyOnlyBetweenRounds=true`, bots are changed only
  when one of these holds: a round has ended and the next one has not started;
  the server is in warmup; or no human is connected. Loading or hot-reloading
  during a live round does not assume the round is safe.
- Counts human CT/T players separately from bots.
- With `MaxPlayers=10` and `FillEmptySlots=true`, targets five players per
  team, counting humans and bots. `MaxPlayers` is the plugin's match cap, not the
  server's connection limit; the effective cap is `min(MaxPlayers, server max)`.
- `ReservedHumanSlotsPerTeam` (default `1`) leaves that many unoccupied places
  per team when filling with bots, so incoming humans can choose either side.
  With the default cap of 10, bot-filled teams target four players each. Applies
  only when `FillEmptySlots=true`.
- `FillEmptySlots=false`: no filling. The plugin only evens out the teams, i.e.
  the weaker team gets bots up to the human count of the stronger one
  (no reservation is applied).
- `BalanceTeams=false`: the target is 0 bots per team (existing bots are removed
  if `RemoveBotsForHumans=true`). To pause the plugin use `Enabled` or
  `css_lbb_enable 0`.
- Does not move human players.
- `MaximumBots` is a global cap.
- The plugin makes no bot changes unless it can read `bot_quota` and its value is
  `0`. Disable any other bot quota/management plugin as well; those cannot be
  detected automatically. The plugin does not change `bot_quota`.
- Adds all currently needed bots in one reconciliation, subject to team targets,
  free match slots, and `MaximumBots`.
- Recommended server settings: `bot_quota 0`, `mp_autoteambalance 0` (the engine
  must not move humans behind the plugin's back), and a `mp_limitteams` value
  that does not block humans from joining a bot-filled team.

## Algorithm

With `MaxPlayers=10`, `FillEmptySlots=true`, and
`ReservedHumanSlotsPerTeam=1`:
- No humans => 4 bots per team, leaving one place on each team.
- 1 human CT and no human T => 3 CT bots and 4 T bots; one place remains open
  on each team.
- 1 human on each team => 3 bots per team; either team still has room for one
  more human.
- 3 humans CT and 1 human T => 1 CT bot and 3 T bots; each team has four players.
- Reservations are best-effort: if a team already has more humans than its
  target occupancy, the plugin does not move or remove humans.
- If one side has more humans than the half-server target, the plugin does not
  move humans; perfect equality may therefore be impossible without
  voluntary/forced human switching.

## Configuration

Created automatically on first start:

```text
<server>/addons/counterstrikesharp/configs/plugins/LocalBotBalancer/LocalBotBalancer.json
```

`LocalBotBalancer.example.json` in this folder shows all options with defaults.
Values are validated on load (`MaxPlayers >= 2`, `BotDifficulty` 0-3,
`MaximumBots <= MaxPlayers`, and so on). `Language` is `en` or `ru` and affects
only chat/console replies.

To apply config changes: `css_plugins reload LocalBotBalancer`.

## Build on Ubuntu 22.04

The plugin is built with the .NET 10 SDK, and the server's CounterStrikeSharp
host must be able to run .NET 10 plugins. I could not verify from here which
.NET version your installed CounterStrikeSharp build targets; check it before
building, and change `TargetFramework` in the `.csproj` if it differs.

Install the .NET 10 SDK (Microsoft repository):

```bash
sudo apt update
sudo apt install -y wget ca-certificates
wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb
sudo dpkg -i /tmp/packages-microsoft-prod.deb
sudo apt update
sudo apt install -y dotnet-sdk-10.0
dotnet --info
```

**Option A (recommended): build against your server's API DLL.**

```bash
find /home -path '*/addons/counterstrikesharp/api/CounterStrikeSharp.API.dll' 2>/dev/null
CSSHARP_PATH=/home/steam/cs2/game/csgo/addons/counterstrikesharp/api dotnet build -c Release
```

`CSSHARP_PATH` must be the folder that contains `CounterStrikeSharp.API.dll`.

**Option B: NuGet package** (when `CSSHARP_PATH` is not set). Pick the version
that matches your server:

```bash
dotnet build -c Release -p:CSSharpVersion=1.0.371
```

The default in the `.csproj` is `1.0.371`.

## Install

Set your actual CS2 path. Example:

```bash
SERVER=/home/steam/cs2/game/csgo
PLUGIN="$SERVER/addons/counterstrikesharp/plugins/LocalBotBalancer"

sudo systemctl stop cs2
mkdir -p "$PLUGIN"
cp bin/Release/net10.0/LocalBotBalancer.dll "$PLUGIN/"
cp bin/Release/net10.0/LocalBotBalancer.deps.json "$PLUGIN/" 2>/dev/null || true
sudo chown -R steam:steam "$PLUGIN"
sudo systemctl start cs2
```

CounterStrikeSharp expects a folder named exactly like the plugin DLL,
containing the DLL and optional deps/PDB files. The config file is created in
`configs/plugins/LocalBotBalancer/` on first start. If you want to start from
the example, copy it there before starting:

```bash
CFG="$SERVER/addons/counterstrikesharp/configs/plugins/LocalBotBalancer"
mkdir -p "$CFG"
cp LocalBotBalancer.example.json "$CFG/LocalBotBalancer.json"
```

If you used 1.0.0, its `config.json` next to the DLL is no longer read; copy
your values into the new file.

## Console commands

All commands require the `@css/root` permission (the server console is always
allowed).

```text
css_lbb_status          current counts, safe-window state, bot_quota
css_lbb_reconcile       queue a balance update (respects the safe window)
css_lbb_enable <0|1>    switch balancing off/on until the plugin is reloaded
```

In chat, use the `!` prefix, for example `!lbb_status`.

## Test checklist

1. Start with an empty server and check the server log for plugin lines.
2. Run `css_lbb_status`. With no humans, bots should be added even though no
   round has ended yet.
3. Keep `bot_quota 0` and disable other bot managers. With
   `ReservedHumanSlotsPerTeam=1`, join as one human. At round end, verify each
   team has one open place for a human.
4. Join another human on either team. At the next round end, verify the joining
   team still has one open place and the match remains within the 10-player cap.
5. Add humans to both teams and verify that bots are removed as needed to
   preserve one open place per team.
6. Check the server never exceeds `MaxPlayers`.
7. Load/reload during a live round (with humans present) and verify no bots
   change until round end.
8. Kill one human during a round: no bot should be added immediately. At round
   end, verify reconciliation occurs.
9. Test disconnects and map changes.
10. Check that bots are actually removed with `kickid`. To test the fallback,
    watch the log: if removal fails you should see a single warning after 3
    attempts, and bots for the other team must still be added.
11. As a non-admin player, try `!lbb_reconcile`; it must be denied.
12. Verify `mp_autoteambalance 0` and your `mp_limitteams` do not move or block
    humans.
13. Inspect logs for errors.

## Rollback

```bash
sudo systemctl stop cs2
rm -rf /home/steam/cs2/game/csgo/addons/counterstrikesharp/plugins/LocalBotBalancer
sudo systemctl start cs2
```

The config in `configs/plugins/LocalBotBalancer/` can be deleted as well.

## Known limitations to validate

- `bot_add_ct`, `bot_add_t`, `kickid <userid>` and `bot_kick "bot name"` must be
  accepted by the exact current CS2 server build. Bot removal by name depends on
  the server's `bot_kick` implementation and unique bot names; that is why it is
  only the fallback.
- Warmup detection reads `CCSGameRules.WarmupPeriod` through the `cs_gamerules`
  entity. If your API version names these differently, the build will fail on
  that member; compare with your installed API.
- The plugin controls bot counts, not bot AI skill beyond the server
  `bot_difficulty` cvar.
- Other plugins or external scripts that manage bots cannot be detected. Keep
  them disabled while this plugin is managing bots. A nonzero or unreadable
  `bot_quota` causes the plugin to skip all bot changes (and logs a warning).
- Version 1.1.0 has not been compiled or run yet. Build it and run the checklist
  above on a test copy of the server first.
