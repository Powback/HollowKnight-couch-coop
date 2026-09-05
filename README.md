# Hollow Knight Couch Co-op

Local two-player Hollow Knight on one screen, one PC, two controllers.

No Nucleus Co-op. No second game instance. No port forwarding, dedicated
server, or Hamachi. It is one BepInEx plugin: drop in a DLL, plug in a second
controller, press a key.

> **Status: early but playable.** Up to four Knights spawn, move and fight using
> the game's own movement code, and the camera frames the whole group. Progress,
> health and soul are shared. See [Limitations](#limitations) before expecting a
> full run.

## Why this exists

Every other Hollow Knight multiplayer option is *networked* — HKMP, SilklessCoop,
SSMP and friends all run one game instance per player and sync them over a socket.
That is the right design for playing with someone in another city and the wrong
one for playing with someone on your couch. The usual workaround is Nucleus Co-op,
which launches two windowed copies of the game and hands each one a controller.

This mod takes the other approach: one game, one camera, two Knights.

## How it works

Three facts about Hollow Knight's code make this simpler than it sounds:

1. **`HeroController` reads input through an instance field**, not a global —
   `inputHandler.inputActions.*`. Give a second Knight its own handler and every
   piece of vanilla movement, jumping, dashing, wall-clinging and nail-swinging
   works for player two with no reimplementation.
2. **InControl's `PlayerActionSet` has a settable `Device`**, so a second action
   set can be pinned to one specific gamepad.
3. **`HeroController` gates on singleton identity in only two places**, and
   `Update`/`FixedUpdate` are not among them — so a cloned Knight runs its own
   full physics loop unmodified.

So player two is a real `Instantiate` of player one, not a puppet. It is not a
visual shell driven by network packets the way remote players in HKMP are; it is
the same class running the same code against a different controller.

The two singleton guards are handled by briefly pointing `HeroController._instance`
at the extra Knight for the duration of the call, rather than rewriting any IL.

### World interactions

Wells, doors, benches and NPC prompts run on PlayMaker `ListenFor*` actions, and
every one of them resolves its input as
`GameManager.instance.GetComponent<InputHandler>()` — always player one. That
decouples the two halves of the check: the trigger notices *a* Knight is present,
then asks *player one* whether the button is down. Left alone, a player standing
somewhere else entirely can send the whole group down a well.

So each of those actions gets its handler swapped to the relevant Knight's before
every poll — the one that owns the FSM, or the nearest one for a world object.
Menu, pane and inventory listeners are deliberately excluded and stay with player
one, or extra pads would steer the pause menu.

## Install

Both players need nothing installed but the host machine; there is only one game.

1. Install [BepInEx 5 (x64)](https://github.com/BepInEx/BepInEx/releases) into your
   Hollow Knight folder — the one containing `hollow_knight.exe`. Run the game once
   to generate `BepInEx/`, then close it.
2. Drop `HKCouchCoop.dll` into `BepInEx/plugins/`.
3. Launch, load a save, plug in the extra controllers, and **press Start** on each
   one to join. Hold Start for about a second to drop out.

`F6` / `F7` add and remove players from the keyboard as a fallback.

Player one can stay on keyboard or use a pad. Each joining pad is added to player
one's exclude list, so it stops driving player one, the pause menu and menu
navigation, and is released again on leave.

> **How Start-to-join avoids pausing the game:** nobody's input is ever
> reconfigured. The game funnels every pause through one method
> (`GameManager.PauseGameToggle`); a Harmony prefix asks which physical pad
> pressed Start, and if it was a spare one, that single pause is swallowed and
> becomes a join instead. Player one's own pad and the keyboard always pause
> normally: the mod watches which device has been driving player one and
> treats that one as theirs, and the MULTIPLAYER screen can assign it
> explicitly if a ghost device confuses the guess. If joining ever misbehaves,
> `F6` works regardless, or set `JoinWithStart = false`.

### Game version

This targets Hollow Knight **1.5.12620** (Unity 6000.0.61f1) — the current build.
It is *not* compatible with 1.5.78.11833, the older build that the Modding
API / Lumafly ecosystem targets, and does not require or interact with that
ecosystem.

On a version mismatch the plugin refuses to load and says so in the log rather
than failing in a confusing way mid-run. Override with `IgnoreVersionCheck` if
you want to try your luck.

## Configuration

Written to `BepInEx/config/com.powback.hkcouchcoop.cfg` on first run.

| Setting | Default | Meaning |
|---|---|---|
| `JoinWithStart` | `true` | Press Start on a spare pad to join; hold Start ~1s to leave |
| `JoinKey` / `LeaveKey` | `F6` / `F7` | Keyboard fallback for joining/removing players |
| `MaxPlayers` | `4` | Total players including player one (2–4) |
| `LeashDistance` | `-1` | `-1` zooms out first and only pulls a straggler in when even maximum zoom cannot frame the group; a positive value is a fixed gap in world units; `0` never pulls |
| `SplitScreen` | `true` | Divide the screen when the group outgrows one view; merge when they regroup. One pane per Knight, up to four |
| `SplitMergeMargin` | `0.15` | Deadband around the split threshold, so a marginal group cannot tear the screen apart and back together |
| `AutoZoom` | `true` | Widen the view to hold everyone |
| `MaxZoomFactor` | `1.6` | Furthest zoom-out, relative to normal |
| `ZoomMargin` | `6` | World units kept clear around the group |
| `ZoomSpeed` / `FollowSpeed` | `3` / `8` | Camera responsiveness |


## Limitations

Known and expected at this stage:

- **Mirrored health display.** Extra players' masks — the game's own mask
  sprites, tinted with each player's color — render top-right, the reflection
  of player one's row. Lifeblood shows in lifeblood blue.
- **In-game settings.** Health mode, shade revival, join-with-Start, max
  players and group zoom are native rows in Options → Game, cloned from the
  vanilla menu so they inherit its fonts, sounds and controller navigation.
- **One screen, one darkness.** Scenes drive the darkness overlay through
  player one only; clones' stale vignette copies are disabled, and the shared
  lantern lights the group the way a shared screen implies.
- **Cutscene coherence.** When the game takes control from player one
  (dialogue, scripted moments), the extras freeze with the same vanilla
  control switch and release together.
- **Independent health.** Each extra player carries their own masks (charms like
  Joni's Blessing and lifeblood compute against them correctly — the game's own
  health code runs against each player's pool via a scoped swap). Soul, geo,
  charms and progress are shared: one save, one wallet, one loadout.
- **Spells and charm effects may target the wrong Knight.** These run as PlayMaker
  FSMs that reference singletons and hardcoded object names.
- **Extra players get a soft color cast** (blue, ember, green) to stay
  identifiable; damage flashes may override it briefly.
- **Camera zoom is capped** because the game's rendering assumes roughly its
  normal view size. Past that cap the screen splits rather than zooming further.
- **Split panes other than player one's have no image effects.** The game's own
  camera keeps the first pane, so it still carries the brightness and colour
  passes and the screen shake; the extra panes are plain cameras and look
  slightly flatter. Darkness is not affected — it is a world-projected cutout
  rather than a per-camera effect, and it is widened to cover every pane.

### Session rules

Things that intentionally end or suspend the co-op session:

- **Death is personal, revival is a fight.** When an extra player dies, their
  body bursts with the real hero-death effect and their **actual Shade** — the
  same entity the game spawns for any death — rises where they fell. Teammates
  defeat it to revive them at half masks. The shade follows the group through
  room changes. Player one's death is still a real death: they are the save
  file, so extras despawn and the game-over runs pure vanilla.
  (`IndependentHealth=false` restores the old shared-pool team-death rule;
  `ShadeRevive=false` makes death a simple drop-out-and-rejoin.)
- **Quit-to-menu dissolves the session** — clones never follow into the title
  screen or another save file.
- **Unplugging a pad removes its player** immediately.
- **Camera lock zones (boss arenas) suspend the co-op camera** — the game's own
  framing wins inside them, and takes back over when the lock releases. Split
  screen is the exception: an arena is where being off-screen hurts most, so
  the screen still divides there, with each pane held inside the arena's own
  bounds.

## Split-screen

When the Knights spread further than one view can hold, the screen divides —
one pane per Knight, up to four — along the axis they are actually separated
on, and becomes one view again when they regroup. Zoom happens first: the view
widens to hold everyone and only splits once it cannot stretch any further.

Panes are always the same size as each other. That is a rendering constraint
rather than a preference: the game's darkness pass sizes its render texture
from the camera's pixel dimensions and rebuilds it whenever they change, so
uneven panes would rebuild one every frame.

Splitting and merging use slightly different thresholds, so a group sitting
exactly on the boundary cannot tear the screen apart and back together every
few frames.

Turn it off in Options → MULTIPLAYER → Split Screen, or `SplitScreen = false`.
With it off, the old behaviour returns: past the zoom cap a straggler is pulled
back to player one.

## Roadmap / stretch goals

- **Independent cameras anywhere in the same scene** — split-screen without the
  merge, which would retire the leash entirely.
- *Not* on the roadmap: players in different scenes simultaneously. Rooms are
  authored at the world origin and the world state is singleton to its core
  (scene bounds, ambient light, audio listener, transition pipeline, enemy
  targeting) — that is networked-multiplayer architecture, not a mod.

## Building

The build needs Hollow Knight's assemblies, which are **not** redistributable and
are not in this repo. Supply your own:

```bash
mkdir -p refs
cp "$HK/hollow_knight_Data/Managed/"*.dll refs/
dotnet build src/HKCouchCoop.csproj -c Release -o dist
```

…where `$HK` is your Hollow Knight install directory. `refs/` is gitignored, as is
`decomp/`. Do not commit either, and do not ship game DLLs alongside the plugin.

No .NET SDK on the host? Build in a container:

```bash
podman run --rm -v "$PWD":/w:z -w /w mcr.microsoft.com/dotnet/sdk:9.0 \
  dotnet build src/HKCouchCoop.csproj -c Release -o dist
```

## License

The mod is MIT. Hollow Knight is the property of Team Cherry; this repo contains
none of its code or assets.
