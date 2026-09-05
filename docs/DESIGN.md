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
- Start-to-join can be disabled (`JoinWithStart=false`). Player one's pad is
  observed from their own action set, or assigned explicitly on the
  MULTIPLAYER screen; there is no pad-index setting any more.

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

## Singleton discipline

The mod's whole architecture is "point `HeroController._instance` at another
Knight for the duration of a call". Three rules keep that from leaking, and
every one of them was a bug first.

**Never ask the singleton who player one is.** Use `CoopManager.PlayerOne`,
captured on Join before any clone exists. The singleton is *supposed* to lie
during an ownership scope; code that asks mid-scope and then acts — dispatching
a death, placing a shade, framing the camera, re-pointing dead geo — acts on
the wrong Knight. `HeroController.instance` appears in mod code only inside
comments now, and `Reflect.HeroInstance` only where the *acting* Knight is
genuinely what is wanted (rumble routing).

**Re-assert the host's caches; you cannot scope them.** A masquerade is scoped
by time, a cache by lifetime, so one bad read outlives every scope that could
explain it. `GameManager.hero_ctrl` is the one that matters — its caller hands
it straight to `inputHandler.AttachHeroController` — and `GameManager.heroLight`
resolves by tag, which clones also carry. Both are re-asserted to player one
after every `SetupHeroRefs`.

**A null singleton is not inert.** `HeroController.SilentInstance` answers null
by running `FindObjectOfType<HeroController>()` *and* `DontDestroyOnLoad` on
whatever it finds. Clone spawning needs a null singleton — `Awake` destroys any
hero that is not it — so that window is scoped to `Awake`'s body, which
provably reads no singleton, rather than to the whole `Instantiate`, which runs
every `Awake` and `OnEnable` in the cloned hierarchy. Vanilla components read
the singleton there; `TrackTriggerObjects` subscribes to its `heroInPosition`
event.

Masquerades also do not survive a `yield`: only the synchronous part of a call
is covered, and deferred work resumes with the real singleton back. A drift
detector in `Plugin.Update` re-asserts the singleton at the frame boundary,
where no scope may legitimately be open. It should never fire; if it logs, the
fix belongs at the leaking scope, not at the detector.

## Pool discipline

Independent health and soul use the other half of the same idea: for the
duration of one Knight's own state-touching call, the shared `PlayerData`
fields hold that Knight's pools, and what vanilla computed is written back
afterwards. `PlayerData` is the save file, so the same two limits cost more
here than they do for the singleton.

**A leaked scope reaches the disk.** A masquerade left open makes the game act
on the wrong Knight; a pool swap left open leaves `PlayerData` holding a
clone's health and soul, and the next autosave writes them. `PoolSwap.Begin` is
therefore all-or-nothing — snapshot, apply with rollback, and only then claim
the reentrancy depth, so a throw partway through cannot mark a player as
swapped without swapping them (which used to turn every later scope into a
passthrough and put them back on the shared pool for the session, silently).
`End` restores the shared object in a `finally`: losing a pool write costs a
player some health, leaving the shared object holding it costs the save.

**It does not cross a `yield`.** Of the wrapped methods only `TakeDamage`
defers anything, and its three coroutines are accounted for: `Die` and
`DieFromHazard` are intercepted for extras by `DeathPatches`, and `StartRecoil`
touches no pool. Anything added to that list needs the same check.

The frame-boundary detector in `Plugin.Update` covers both mechanisms and
repairs both. Like the singleton one, it should never fire.

## Split-screen

Panes are cameras. The first driver drove the single world camera N times with
`cam.Render()` from a LateUpdate postfix; it threw nothing, framed every pane
correctly, reported a flawless layout, and drew a black world behind an intact
HUD — LateUpdate runs before the frame's own render pass, and a camera left
disabled contributes nothing to it, so those passes were cleared before
reaching the screen. The e2e suite called that 13/13, because every case read
the layout the mod decided and none could see a pixel. It now screenshots the
world split and whole and compares brightness.

So the game's own camera keeps pane one, retaining tk2dCamera's projection, the
image effects and the fade and shake FSMs; extra panes get plain cameras that
copy its culling mask, clear flags, clip planes and transparency sort, and
derive their field of view from the pane's shape rather than the screen's. Unity
draws enabled cameras with viewport rects at the right time, so there is no race
with the frame loop to lose.

There are two rendering paths. The default one makes each pane a camera
viewport, which is why its cuts are only `|` and `—`: a viewport is an
axis-aligned rectangle. `SplitRotate` swaps to a second path where every camera
renders the WHOLE screen into its own RenderTexture and the screen is then
painted from those textures through polygons — so a boundary can be any line.
Each Knight owns a Voronoi cell (everywhere closer to them than to any other
Knight), which for two players is a single divider at whatever angle they stand
and for three or four is wedges that pivot as they move. Cells are computed in
aspect-corrected space, or they look skewed, since a pixel across is not the
same distance as a pixel up.

It borrows a shader — `Unlit/Texture` and fallbacks — because a BepInEx plugin
cannot compile ShaderLab at runtime, and reports itself unavailable rather than
half-working if none is present. It is off by default and takes over completely
or not at all: the viewport path is verified, this one changes how the game
reaches the screen, and doing that on reasoning alone previously produced a
black world behind an intact HUD.

Two things the DEFAULT path deliberately is not, so nobody reads it expecting
them.
The split is axis-aligned — vertical or horizontal, never diagonal — because a
pane is a camera viewport rect and that is an axis-aligned rectangle; a
rotating divider needs per-pane RenderTextures and a masking shader, which is
the compositing path this design avoided. And it is not animated: panes appear
and disappear in one frame. `SeamWidth` appears in the original plan and was
never built.

Three constraints are load-bearing:

- **Uniform panes.** `DarknessCameraEffect` sizes its RenderTexture from
  `mainCamera.pixelWidth/Height` and destroys and recreates it whenever those
  change. Mixed pane sizes would rebuild a RenderTexture every pass of every
  frame.
- **Hysteresis both ways.** One threshold sits exactly where a marginal group
  oscillates.
- **Panes divide the letterbox**, never the whole screen: `ForceCameraAspect`
  owns that rect.

Lock zones (boss arenas) do NOT stop a split. For one view they win — the
players are confined there anyway and the framing is authored — but a boss
fight with a player off-screen is exactly the case split-screen exists for, so
panes clamp to the lock's own limits (`xLockMin`/`xLockMax`/`yLockMin`/
`yLockMax`, which are different fields from the scene bounds) rather than
standing down. The arena still bounds what each pane shows. The risk taken
knowingly: this overrides framing the game deliberately authored, and a boss
FSM that assumes a particular view may behave oddly. `SplitScreen = false`
restores the authored framing. The screen leash still stands down inside locks,
as it always did.

The screen-mode leash stands down when split-screen is on. Both fire on the
identical "the view cannot hold the group" condition, so while the leash was
newly working it yanked stragglers together a frame before the screen would
have divided and the split could never engage.

A pane keeps its own Knight in frame. Panes honour the room's camera limits
like vanilla does — that is why a view never drifts into the void — but
`sceneHeight` bounds the CAMERA, not the hero, so a Knight can legitimately
stand above where the camera may look. For the shared view, clamping there is a
fair compromise between players. For a pane that exists to show one Knight it
is a pane showing empty room while the player is outside it, which is how an
earlier run produced a black-looking pane that was in fact a camera behaving
correctly. So the clamp applies unless it would push that pane's Knight out of
frame, and then the true position wins: a sliver of void beyond the room beats
none of the player. Normal framing is untouched, because the rule only bends in
the case that produced the empty pane.

Darkness needs no pass per pane. It is a cutout rendered by its own camera
into a texture published globally with `_DarknessCameraVP`, the matrix that
projects world positions into it — so any world point inside that camera's
frustum resolves correctly, whichever pane it is drawn in. Left alone that
camera inherits the game camera's framing, which under a split is only the
first pane, and every other pane samples outside the cutout and comes back
unlit. It is widened to cover the whole group instead (postfix on
`DarknessCameraEffect.EnsureSetup`, which runs just before the matrix is
published), deliberately past the configured zoom ceiling: that ceiling limits
what the VIEW may stretch to, and nobody looks at this camera.

Not visually confirmed: the runs happen in lit Crossroads rooms, where there is
little darkness to get wrong. The reasoning is from the projection matrix, and
the patch is proven not to throw or regress the suite.

Failure policy: the driver holds the camera's automatic render off, and a throw
there would repeat a black screen forever behind `Guard`. So it counts
consecutive failures, stands the whole feature down after three, hands the
camera back, and says so on the debug channel; the stand-down clears with the
session.

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
