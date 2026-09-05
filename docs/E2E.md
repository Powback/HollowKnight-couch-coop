# End-to-end testing

Everything up to v0.6.x was verified by a person launching the game and looking
at it, and only v0.01 was ever tested that way at all. This is the rig that
replaced that: it launches Hollow Knight, loads a save, plugs in three virtual
controllers, presses Start, walks the Knights around, and asserts on the mod's
own live state — with no human involved.

```bash
~/Projects/PowOS/bin/powos mods e2e hollowknight
```

Exit 0 means every case passed. 1 means a case failed, 2 means the harness or
the environment failed, 3 means nothing was actually observed.

The harness itself is generic and lives in PowOS
(`lib/mods/e2e/`, contract in `lib/mods/e2e/CONTRACT.md`). What is specific to
this mod is one config file, one scenario, and the state channel below.

## Quick reference

```bash
powos mods e2e hollowknight --probe          # check prerequisites, launch nothing
powos mods e2e hollowknight                  # the real run
powos mods e2e hollowknight --keep-running   # leave the game up to poke at it
powos mods e2e prove hollowknight            # prove every case FAILS when broken
powos mods e2e selftest                      # test the harness
```

Evidence — screenshots, state snapshots, `verdict.json` — lands in
`~/.local/state/powos/mods/e2e/hollowknight-<timestamp>/`.

Prerequisites, all already true on this machine: Steam installed with Hollow
Knight, a save in any slot, BepInEx with the plugin installed
(`./build.sh --install`), and write access to `/dev/uinput` (an ACL grants it
here; no sudo needed).

## The state channel

The piece that had to be built. A couch co-op mod's whole claim is "a second
Knight exists, is driven by a second controller, and moves independently", and
none of that is visible from outside the process. A log line proves a code path
ran. A screenshot proves pixels changed. Neither can distinguish "player two
spawned and moved" from "player two spawned and stood still while player one
moved" — which is precisely the bug this architecture produces.

So the mod serves its own state.

`src/DebugState.cs` registers routes on `CoopKit.DebugServer`, a small
loopback HTTP/JSON server in the kit (reusable by any BepInEx mod):

| Route | What it gives you |
|---|---|
| `/state` | scene, game state, paused, player count, and per player: position, health, soul, downed, bound controller (name, GUID, index) |
| `/devices` | every controller InControl can see — name, GUID, `passive`, `attached`, `anyButtonPressed`, `claimed` |
| `/cmd?do=…` | `loadsave&slot=N`, `join`, `leave`, `leaveall` |
| `/ping` | identity and version |

It also writes `BepInEx/hkcoop-state.json` every half second, as a fallback for
when the socket is unreachable and as a post-mortem after a crash.

### It is off unless you turn it on

The channel exposes internal state and can drive the session, so:

* `[Debug] DebugServer` defaults to **false** — a normal install never opens a
  socket, and there is no way to enable it by accident.
* It binds `127.0.0.1` only, never a wildcard.
* It is deliberately **not** in the in-game options menu.
* The harness switches it on for one run by editing the config and restoring
  the file afterwards; `HKCC_DEBUG_PORT` overrides the config for a single
  launch without touching it at all.

### Why HTTP, through Wine

Hollow Knight is a Windows build under Proton. A `TcpListener` inside Wine is a
real host socket, so a Linux script can talk to a Windows game on
`127.0.0.1:27600`. This was the load-bearing unknown and it is now verified.

Handlers run on the **game thread**, not the socket thread — the server queues
each request and `Plugin.Update` runs it. Touching Unity objects from a
background thread crashes the process.

### Getting into the game hands-free

`/cmd?do=loadsave&slot=N` calls `GameManager.LoadGameFromUI`, the same method
the save-slot button calls. Driving the title menus with a virtual pad would
work until someone reorders a menu row; this does not.

Note the slot numbering: Hollow Knight names slot 0 `user.dat` and slot N
`userN.dat`, so the first save a player creates is **slot 1**. Loading a slot
with no file is accepted, finds nothing, and quietly returns to the main menu —
indistinguishable from the load hanging. The scenario scans the save directory
and picks the lowest slot that actually exists.

## The configuration under test: a real couch

Three virtual pads, and **pad 1 is player one** — the controller the game was
already using, which always pauses and never joins. Pad 2 joins as player two,
pad 3 as player three.

That choice is the point. Putting player one on the keyboard is easier to
arrange, because then every pad is a joiner and no pad has to be reserved — and
it silently skips the half that actually breaks: whether **player one's own
controller still drives player one once a clone exists**. The first version of
this rig made exactly that mistake.

Nothing configures this any more. There was a `PlayerOnePadIndex` setting; the
v0.7.0 assignment matrix replaced it and it was removed in v0.7.10 after being
found dead — bound, documented, read by nothing. Player one's pad is now
whichever device has actually been driving player one, so the rig gets the
couch layout by having pad 1 move player one before any join, which case 5
already does. An explicit assignment can still be made on the MULTIPLAYER
screen when a ghost device confuses the observation.

## What the cases check

`~/Projects/PowOS/lib/mods/e2e/scenarios/hollowknight.py`:

1. the mod is loaded and answering
2. a save is loaded and the game is in gameplay
3. the virtual controllers reached the game
4. each virtual pad is a distinct device inside the game
5. **player one is driven by his own pad** — the baseline, before any clone
6. pressing Start on pad 2 spawns player two
7. **player two moves on pad 2, and player one does not**
8. **player one STILL moves on his own pad after player two joined**
9. a third pad joins as player three
10. each Knight has its own health pool
11. holding Start removes a player

Cases 7 and 8 are the claim, and they are deliberately symmetric. Checking only
that player two moved passes when *both* Knights move together — the signature
of a clone sharing player one's input handler. Checking only player two also
misses the opposite failure: joining rebinds input (the joining pad is excluded
from player one's action set so it cannot drive him or the pause menu), and
excluding the wrong device leaves **player one** deaf to his own pad. From the
couch that is the more obvious bug, because player one was working a second
earlier.

Case 5 exists so case 8 means something. Without a baseline, "player one
stopped moving after player two joined" is indistinguishable from "player one
never moved on that pad at all", and those have different causes.

Every hold samples the whole 1.5 seconds rather than the endpoints, because two
samples cannot tell "walked right, then got yanked back" from "never moved",
and both happen here — the leash pulls a straggler toward player one, and a
freshly spawned clone settles under gravity.

### The cases are proved to fail

`powos mods e2e prove hollowknight` runs the real scenario against a fake game
with no engine behind it: once healthy, then once per known fault
(`shared_input`, `p1_dead_after_join`, `p1_never_moves`, `moves_left`,
`same_device`, `no_input`, `pads_share_device`, `never_leaves`,
`pad_never_joins`, `clone_has_no_transform`, …). Every case must
pass when healthy and go red under the fault it claims to detect; a case that
stays green is named as decoration. It takes seconds instead of a five-minute
launch, which is the only reason it gets run.

## Gotchas, all of them paid for

**The game window must be focused.** Unity does not poll input while the
application is in the background. Every pad goes dead at once — they still
enumerate, no button ever registers — and it looks exactly like a broken mod.
One run lost to this. `E2E_FOCUS_WINDOW=1` focuses the window before the tests,
and the pad-identity case retries after refocusing before it blames the mod.
The consequence is that **runs are not headless**: they need a session whose
compositor honours activation requests.

**InControl renames every pad.** Whatever a uinput device is called, the game
reports it as "Xbox Controller". Comparing controllers by name reported players
two and three as sharing one pad when they were not — my bug, not the mod's.
Identify by GUID, or behaviourally: hold a button and ask the game which device
index lights up.

**Steam Input publishes device twins.** On v0.6.8 each virtual pad appeared as
*two* in-game devices (pad 1 → indices [0,1], pad 2 → [2,3]) — measured by the
button-press probe, not guessed. The mod excludes a joined pad from player one's
action set; with twins it excludes one and the other keeps driving player one.
v0.6.9 ("defeat Steam Input device twins for keyboard-declared player one")
addressed this, and the probe now measures 1:1. If per-player input misbehaves
with *real* controllers, read `/devices` first — the twin count is right there.

**Steam's launch can wedge, and it looks exactly like a broken mod.** Twice in
one session `steam -applaunch` was accepted, no game process ever appeared, and
the rig reported `ERR launch / the game did not become testable in time` after
180s. The evidence is only in Steam's own logs — `GameAction ... LaunchApp
changed task to X` in `~/.local/share/Steam/logs/console_log.txt`, with the
depot side in `content_log.txt`. It stalled at `DownloadingDepots` once (with
the depot check already finished cleanly, `No Error`) and at
`SynchronizingCloud` once. The cloud one is probably self-inflicted: this rig
backs up `user*.dat*` before a run and restores them after, so Steam Cloud sees
the saves change and then revert. Recovery, which works: `steam -shutdown`,
wait for the process to go, relaunch (`steam -silent`), wait for
`steamwebhelper`. Before blaming the mod for a launch error, read the
GameAction lines.

**Steam must be running**; the harness starts it if not, and launches through
`steam -applaunch 367520` so the run uses the same Proton and launch options a
player uses. Hollow Knight needs `WINEDLLOVERRIDES="winhttp=n,b"` in its launch
options for BepInEx's doorstop, which is already set here.

**Never SIGKILL the game.** Teardown is SIGTERM and a wait; a survivor is
reported rather than killed, because the Proton/nvidia stack leaks memory that
only a reboot returns.

**Saves are backed up and restored.** Loading a save and walking around writes
to it (autosave on room transitions). The harness copies `user*.dat*` first and
puts them back afterwards, including when the run fails.

## Known state

**12 of 12 passing** against HKCouchCoop **v0.7.12**, 2026-09-05.

That run is the whole argument for this rig. Everything past v0.7.2 had been
written, reviewed, built and deployed without ever executing; the first three
attempts found, in order:

* **A wedged Steam launch.** `LaunchApp` stuck at `DownloadingDepots` with the
  depot check already finished cleanly. Nothing to do with the mod, and
  indistinguishable from a broken mod without Steam's own logs.
* **Start-to-join could never work for an unknown pad** (fixed in v0.7.11). A
  spare pad's Start is deliberately left feeding player one's action set, so
  the press made InControl report *that* pad as player one's, and the mod then
  refused the join and paused instead. Invisible under keyboard-player-one,
  which is what every earlier run used.
* **Two defects in this rig**, both of which blamed the mod: comparing x across
  a room transition, and pressing Start during one.
* **AutoZoom and the Screen leash had never worked at all** (fixed in v0.7.12).
  Hollow Knight's world camera is perspective — tk2dCamera drives it as
  `fieldOfView / ZoomFactor` and calls `ResetProjectionMatrix` — so the
  `cam.orthographicSize` the mod wrote its zoom into was an inert leftover
  reading 480. That number also dwarfed every real distance, which pinned the
  leash's "the camera cannot frame this" test to permanently false. Two
  features, dead in every released build, and nothing said so. Found by adding
  a camera block to the state channel and *measuring* rather than reading the
  decompile, which implements both projection paths and settles nothing.

The v0.7.2 numbers this section used to carry (6 of 9, with player two never
moving) are superseded. They were taken with keyboard-player-one and predate
the three-pad rewrite.

The rig also learned to stand on still ground: holds are retaken if the room
changed under them, Start presses wait for `GameState.PLAYING`, measurements
wait for every Knight to stop drifting, and repositioning walks to the middle
of the room using the channel's `sceneWidth` rather than guessing a direction
and a duration — three earlier attempts at that guessed wrong and merely
changed which wall got hit.

Confirmed against the running game for the first time: camera zoom,
Start-to-join,
hold-Start-to-leave, per-Knight health pools, player one keeping his own pad
once a clone exists, a third pad joining, and 1:1 device mapping with no Steam
Input twins.

