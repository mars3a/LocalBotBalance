# LocalBotBalancer 1.0.0

CounterStrikeSharp C# plugin prototype for a private CS2 server.

## Important behavior

- With `ApplyOnlyBetweenRounds=true`, changes are made only after `round_end`.
  Loading or hot-reloading during a round does not assume that the round is
  safe; the plugin waits for `round_end`. Alive-human counts are logged for
  diagnostics; round events determine whether changes are safe.
- Counts human CT/T players separately from bots.
- With `MaxPlayers=10` and `FillEmptySlots=true`, targets five players per team,
  counting humans and bots. `MaxPlayers` is the plugin's match cap, not the
  server's 20-player connection limit.
- Does not move human players.
- `MaximumBots` is a global cap.
- The plugin makes no bot changes unless it can read `bot_quota` and its value is
  `0`. Disable any other bot quota/management plugin as well; those controllers
  cannot be detected automatically. The plugin does not change `bot_quota`.
- Adds all currently needed bots in one reconciliation, subject to team targets,
  free match slots, and `MaximumBots`.
- Validate bot command syntax and API compatibility on a test copy before relying
  on it.

## Algorithm

With `MaxPlayers=10`, `FillEmptySlots=true`:
- 1 human CT and 1 human T => target population 5 per side, so 4 bots per side.
- 1 human CT and no human T => 4 CT bots and 5 T bots (10 players total).
- 3 humans CT and 1 human T => 2 CT bots and 4 T bots, reaching the 5-per-team
  target.
- If one side has more humans than the half-server target, the plugin does not move humans; perfect equality may therefore be impossible without voluntary/forced human switching.

## Build on Ubuntu 22.04

Install .NET 8 SDK (Microsoft repository):

```bash
sudo apt update
sudo apt install -y wget ca-certificates
wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb
sudo dpkg -i /tmp/packages-microsoft-prod.deb
sudo apt update
sudo apt install -y dotnet-sdk-8.0
dotnet --info
```

Find your server's API DLL:

```bash
find /home -path '*/addons/counterstrikesharp/api/CounterStrikeSharp.API.dll' 2>/dev/null
```

Build (replace the path if yours differs):

```bash
cd LocalBotBalancer
CSSHARP_PATH=/home/steam/cs2/game/csgo/addons/counterstrikesharp/api dotnet build -c Release
```

The `CSSHARP_PATH` directory must contain `CounterStrikeSharp.API.dll`.

## Install

Set your actual CS2 path. Example:

```bash
SERVER=/home/steam/cs2/game/csgo
PLUGIN="$SERVER/addons/counterstrikesharp/plugins/LocalBotBalancer"

sudo systemctl stop cs2
mkdir -p "$PLUGIN"
cp bin/Release/net8.0/LocalBotBalancer.dll "$PLUGIN/"
cp bin/Release/net8.0/LocalBotBalancer.deps.json "$PLUGIN/" 2>/dev/null || true
cp config.json "$PLUGIN/"
sudo chown -R steam:steam "$PLUGIN"
sudo systemctl start cs2
```

CounterStrikeSharp expects a folder named exactly like the plugin DLL, containing the DLL and optional deps/PDB files.

## Console commands

```text
css_lbb_status
css_lbb_reconcile
```

`css_lbb_reconcile` queues an update, but respects round-safe mode.

## Test checklist

1. Start with an empty server and inspect `[LocalBotBalancer]` in server logs.
2. Run `css_lbb_status`.
3. Keep `bot_quota 0` and disable other bot managers. Join as one human CT and
   one human T. At round end, verify bots fill the five-per-team targets.
4. Join another human on a side with bots. At the next round end, verify a bot is removed from that side.
5. Check the server never exceeds `MaxPlayers`.
6. Load/reload during a live round and verify no bots change until round end.
7. Kill one human during a round: no bot should be added immediately. At round
   end, verify reconciliation occurs.
8. Test disconnects and map changes.
9. Inspect logs for errors.

## Rollback

```bash
sudo systemctl stop cs2
rm -rf /home/steam/cs2/game/csgo/addons/counterstrikesharp/plugins/LocalBotBalancer
sudo systemctl start cs2
```

## Known limitations to validate

- `bot_add_ct`, `bot_add_t`, `bot_kick "bot name"` must be accepted by the exact current CS2 server build.
- Bot removal by name depends on the server's bot_kick implementation and bot names being unique.
- The plugin controls bot counts, not bot AI skill beyond the server `bot_difficulty` cvar.
- Other plugins or external scripts that manage bots cannot be detected. Keep
  them disabled while this plugin is managing bots. A nonzero or unreadable
  `bot_quota` causes the plugin to skip all bot changes.
- API compatibility is compiled against the installed CounterStrikeSharp API DLL, not a NuGet package. If compilation reports an API member mismatch, compare against your installed version's API and adjust accordingly.
