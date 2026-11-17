# LocalBotBalancer 1.2.2

[Русская версия](README.ru.md)

CounterStrikeSharp C# plugin prototype for a private CS2 server.

## What changed in 1.2.2

- While humans connect, choose or join a team, CS2 keeps fewer bots than
  `bot_quota`. 1.2.1 treated that gap as a stale quota and lowered it, so the
  engine kicked a bot, the plugin re-added it, and the joining human could not
  pick a team. A quota above the bot count is now accepted, and the quota is
  synced only while no human is on a team.
- After `bot_kick` (which lowers `bot_quota` itself) the plugin no longer
  rewrites the quota; it lowers it only after the `kickid` fallback.

## What changed in 1.2.1

- A `bot_quota` that does not match the bot count is synced to the actual
  count instead of stopping the plugin. On CS2 the engine kicks bots without
  lowering the quota when the last human leaves (`bot_join_after_player 1`),
  which left 1.2.0 stuck with the warning "bot_quota is N but 0 bot(s) are
  present".
- Verified on a CS2 server (CounterStrikeSharp 1.0.380): console
  `bot_add_ct`/`bot_add_t` raise `bot_quota`, `bot_kick` lowers it.

## What changed in 1.2.0

- No bots while no human is on a team. When the first human joins a team, the
  teams are filled with bots immediately, without waiting for round end. After
  that, bots are adjusted at round end as before. When the last human leaves,
  all bots are removed.
- Joining a team and disconnecting trigger a recount right away (in addition
  to the 5-second poll).
- `bot_quota` equal to the current bot count is accepted: `bot_add`/`bot_kick`
  issued from the server console change `bot_quota` themselves, which used to
  stop the plugin after its first fill.
- Bot removal uses `bot_kick "name"` first and falls back to `kickid <userid>`;
  after a kick the plugin keeps `bot_quota` in line with the bot count so the
  engine does not re-add kicked bots.
- Failed removals are retried every 5 seconds, also in warmup and on an empty
  server.
- Pending timers are dropped on map change.

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
  the server is in warmup; no human is connected; or the first human has just
  joined a team on a server where no human was on a team. Loading or
  hot-reloading during a live round does not assume the round is safe.
- No human on CT/T (empty server, or only spectators) means no bots.
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
- `bot_quota` must be `0` or at least the current number of bots (console
  `bot_add`/`bot_kick` change the quota by themselves, and CS2 keeps fewer bots
  than the quota while humans connect or join a team). With no human on a team, any other
  value is synced to the bot count; otherwise the plugin skips bot changes and
  logs a warning. Disable any other bot quota/management
  plugin; those cannot be detected automatically and would fight the plugin.
- Adds all currently needed bots in one reconciliation, subject to team targets,
  free match slots, and `MaximumBots`.
- Recommended server settings: `bot_quota 0`, `bot_quota_mode normal`,
  `mp_autoteambalance 0` (the engine
  must not move humans behind the plugin's back), and a `mp_limitteams` value
  that does not block humans from joining a bot-filled team.

## Algorithm

With `MaxPlayers=10`, `FillEmptySlots=true`, and
`ReservedHumanSlotsPerTeam=1`:
- No humans on teams => no bots.
- The first human joins a team => bots are added at once (below); later
  changes happen at round end.
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

## Install / update directly from Git

Run these commands on the game server. The server needs `git` and the .NET 10 SDK
(see "Build" above). Replace paths and the service name with yours.

**First time: clone the repository**

```bash
sudo apt install -y git
cd /home/steam
git clone https://github.com/mars3a/LocalBotBalance.git
cd LocalBotBalance
git checkout feature/local-bot-balancer   # until it is merged; afterwards use: main
```

If the repository is private, clone over SSH with a read-only deploy key
(`git clone git@github.com:mars3a/LocalBotBalance.git`) or use an HTTPS
personal access token.

**Install or update (same steps every time)**

```bash
cd /home/steam/LocalBotBalance
git pull --ff-only

SERVER=/home/steam/cs2/game/csgo
PLUGIN="$SERVER/addons/counterstrikesharp/plugins/LocalBotBalancer"

CSSHARP_PATH="$SERVER/addons/counterstrikesharp/api" dotnet build -c Release
```

Continue only if the build finished with `0 Error(s)`:

```bash
sudo systemctl stop cs2
mkdir -p "$PLUGIN"
cp bin/Release/net10.0/LocalBotBalancer.dll "$PLUGIN/"
cp bin/Release/net10.0/LocalBotBalancer.deps.json "$PLUGIN/" 2>/dev/null || true
sudo chown -R steam:steam "$PLUGIN"
sudo systemctl start cs2
```

Notes:

- Updating does not touch your settings: the config lives in
  `configs/plugins/LocalBotBalancer/`, outside the plugin folder.
- If the build fails, do not run the second block: `git pull` has already
  changed the sources, but the old DLL on the server is still in place and the
  server keeps running. Fix the problem or roll back with the commands below.
- Restarting the server is the reliable way to load a new DLL.
  `css_plugins reload LocalBotBalancer` may also work on a test server, but a
  DLL that is currently loaded can be locked or cached, so do not rely on it.
- Check the log after the start for the "Loaded" line from the plugin.

**Switch branch or roll back to an earlier version**

```bash
cd /home/steam/LocalBotBalance
git fetch --all --tags
git log --oneline -10                 # find the commit you want
git checkout <commit-or-tag>          # or: git checkout main
# then repeat the build and copy steps above
```

To return to the latest version afterwards: `git checkout main && git pull --ff-only`.

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
2. Run `css_lbb_status`. With no humans on teams, there must be no bots.
3. Keep `bot_quota 0` and disable other bot managers. With
   `ReservedHumanSlotsPerTeam=1`, join a team as one human. Within a second,
   verify bots are added and each team has one open place for a human.
   Run `css_lbb_status` again: `bot_quota` must be `0` or equal to the bot count.
4. Join another human on either team. At the next round end, verify the joining
   team still has one open place and the match remains within the 10-player cap.
5. Add humans to both teams and verify that bots are removed as needed to
   preserve one open place per team.
6. Check the server never exceeds `MaxPlayers`.
7. Load/reload during a live round (with humans present) and verify no bots
   change until round end.
8. Kill one human during a round: no bot should be added immediately. At round
   end, verify reconciliation occurs.
9. Test disconnects and map changes. When the last human leaves, all bots must
   be removed.
10. Check that bots are actually removed with `bot_kick`. To test the fallback,
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
  the server's `bot_kick` implementation and unique bot names; `kickid` is the
  fallback.
- Warmup detection reads `CCSGameRules.WarmupPeriod` through the `cs_gamerules`
  entity. If your API version names these differently, the build will fail on
  that member; compare with your installed API.
- The plugin controls bot counts, not bot AI skill beyond the server
  `bot_difficulty` cvar.
- Other plugins or external scripts that manage bots cannot be detected. Keep
  them disabled while this plugin is managing bots. An unreadable or unexpected
  `bot_quota` causes the plugin to skip bot changes (and logs a warning).
- Version 1.2.2 builds against CounterStrikeSharp.API 1.0.371; 1.2.0 was run on
  a CS2 server with CounterStrikeSharp 1.0.380, but not every checklist item has
  been verified yet. Run the checklist above on a test copy of the server
  first.
