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

## Economy and items (the shared bank)

One save = one wallet = one inventory, by design. Any Knight's touch collects
geo; buying and selling at any shop draws from and feeds the same pool no
matter who initiated it. Soul orbs and kill-geo credit the Knight who earned
them (0.6.2); the Gathering Swarm fetch visual biases toward player one
(cosmetic). Item pickups grant to the shared save regardless of collector —
the pickup animation may play on the wrong body (polish item).

**Charms are deliberately a team build, not per-player.** Charm effects are
read from the shared save partly by scopeable instance code and partly by
FSMs that cannot be scoped — any per-player or exclusivity setting would
half-work invisibly (speed lost, FSM effects kept). A setting that lies is
worse than none; the loadout is a shared resource negotiated at the bench.
Revisit only if play shows a genuine need.

## Config surface

Menu (Options → Game, 12 native rows): health mode, fallen players,
join-with-Start, max players, leash, revive masks, player one's pad,
hold-to-leave, cutscene freeze, zoom range, player colors, group zoom.
Config file additionally: keyboard keys, exact zoom margins/speeds, and the
version-gate override — numbers that make bad option rows. Correctness fixes
(damage routing, recoil attribution, soul credit) have no toggle on purpose.

**Presets**: defaults = casual couch (per-player health, shade revival, half-
mask revives). Hardcore = Shared Pool (any death is the team's). Kids/chaos =
Revive Masks: Full, Leash: Short.

## Known quirks (deliberately deferred, not forgotten)

- Benches: one seat per bench (single seating state machine) — but ANY Knight
  can be the sitter (interaction ownership routes the whole sequence to
  whoever pressed Up), and a rest refills the whole party's pools.
- Lifeblood charms grant blue masks to player one only.
- Extras have no soul orb display yet (pools work; UI needs the HUD dump from
  a play session).
- (fixed in 0.6.1) ability listeners — quick cast, superdash, dream nail —
  now answer to their own Knight; previously they read player one's buttons.
- The shade fight may drift outside a camera lock zone's view.
