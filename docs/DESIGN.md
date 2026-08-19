# HKCouchCoop — Session Design

How a couch evening is supposed to work, every path thought through. This is
the contract the code implements; deviations found in play are bugs.

## Joining and controls

- **Player one** is the vanilla Knight: keyboard, or the pad the game already
  uses. Never reconfigured, never excluded, always able to pause.
- **Joining**: press Start on any spare pad (during gameplay only), or F6 on
  keyboard. Every rejection shows its reason on screen ("No free gamepad",
  "Can only join during gameplay", "Player limit (N) reached").
- Joiners inherit the game's own configured controller layout. Their pad is
  excluded from player one's action set for exactly as long as they play —
  so it cannot move player one, pause, or steer menus — and is released on
  leave. Nothing else about input is ever reconfigured (the v0.2 Deck outage
  taught that lesson).
- **Leaving**: hold Start (configurable seconds), F7, unplugging the pad, or
  quitting to the menu. All four paths tear down identically.
- Start-to-join can be disabled (`JoinWithStart=false`); `PlayerOnePadIndex`
  says which pad is player one's (`0` Deck-style, `-1` keyboard-P1).

## During play

- Camera frames the whole group, zooming to a cap; stands down completely in
  the game's camera-lock zones (boss arenas). Stragglers past the leash are
  pulled to player one; leash distance is a menu setting.
- World interactions (wells, doors, prompts) answer to the Knight standing
  there, not player one across the room.
- Extra Knights' own FSMs (spells, focus, charm effects) execute as their
  Knight via the ownership masquerade — an extra's fireball comes from their
  body, their focus heals their pool.
- One darkness overlay (player one's); the shared lantern lights the group.
- Dialogue/cutscenes freeze everyone together (`FreezeExtrasInCutscenes`).
- Each extra has their own masks and soul (menu: Shared Pool / Per Player),
  shown as mirrored mask rows top-right, tinted per player. Soul from a hit
  credits the Knight who landed it; so does their kills' geo.

## Death — the full matrix

| Event | Outcome |
|---|---|
| Extra dies (independent health, ShadeRevive on) | Real death burst; their actual Shade rises where they fell; teammates defeat it → revived at `ReviveHealthPercent` masks where it broke |
| Extra dies into spikes/pit | Shade rises beside the nearest living Knight instead of inside the hazard |
| Extra's shade left in another room | Shade re-materializes near player one after the transition |
| Extra dies, ShadeRevive off | They're out; one Start press rejoins them |
| Two extras down | Two shades, independently revivable |
| Downed player holds Start / unplugs | They leave; their shade dissolves |
| Player one dies (always) | Real game over: extras despawn first so the death, shade banking and respawn run pure vanilla; everyone rejoins at the bench |
| Shared-pool mode, anyone dies | Team death, routed through player one |
| Revival shade is killed | The player's real banked shade (geo, soulLimited, scene marker) is guarded — neutralized during the death sequence, restored after, force-restored on any teardown including save-and-quit |
| Save file safety | Backed up before first run of any death feature; version gate refuses foreign game builds |

## Config surface

Menu (Options → Game, native rows): health mode, fallen-players mode,
join-with-Start, max players, leash, revive masks, player colors, group zoom.
Config file additionally: keys, pad index, hold-to-leave seconds, cutscene
freeze, zoom tuning, version-gate override.

**Presets**: defaults = casual couch (per-player health, shade revival, half-
mask revives). Hardcore = Shared Pool (any death is the team's). Kids/chaos =
Revive Masks: Full, Leash: Short.

## Known quirks (deliberately deferred, not forgotten)

- Benches: a bench answers the nearest Knight's Up press, but the seating
  sequence targets player one — an extra using a bench may seat player one.
  Fix needs play data on the bench FSM; workaround: player one sits.
- Lifeblood charms grant blue masks to player one only.
- Extras have no soul orb display yet (pools work; UI needs the HUD dump from
  a play session).
- A dozen minor prompt types still read player one's input (tutorial-grade).
- The shade fight may drift outside a camera lock zone's view.
