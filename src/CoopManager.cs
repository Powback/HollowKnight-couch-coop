using System.Collections.Generic;
using System.Linq;
using InControl;
using UnityEngine;
// Hollow Knight defines its own SceneManager, which shadows Unity's.
using USceneManager = UnityEngine.SceneManagement.SceneManager;
using UScene = UnityEngine.SceneManagement.Scene;

namespace HKCouchCoop
{
    internal sealed class CoopPlayer
    {
        /// <summary>
        /// True for the record standing in for player one. He is downed and
        /// revived by a different mechanism (hidden in place, never destroyed —
        /// see <see cref="PlayerOneDown"/>) but carries a shade like anyone
        /// else, so the shade machinery is shared and only the two ends differ.
        /// </summary>
        internal bool IsPlayerOne;

        internal HeroController Hero;      // null while downed
        internal PadInput Input;
        internal int Number;               // 2, 3, 4 — player one is the vanilla hero

        // Private pools for independent health and soul (see HealthPatches).
        internal int Health;
        internal int HealthBlue;
        internal int Soul;
        internal int SoulReserve;

        // Downed state: body gone, the real Shade stands in for them.
        internal bool Downed;
        internal GameObject Shade;
    }

    /// <summary>
    /// Owns the extra players: spawning their Knights, keeping them with the
    /// group across rooms, downing and reviving them, and tearing everything
    /// down cleanly.
    ///
    /// Player one is the vanilla hero, held in <see cref="PlayerOne"/>.
    /// Everyone else is a clone of it.
    /// </summary>
    internal static class CoopManager
    {
        private static readonly List<CoopPlayer> Extras = new List<CoopPlayer>();

        internal static bool Active => Extras.Count > 0;
        internal static int PlayerCount => Extras.Count + 1;

        private static HeroController _playerOne;

        /// <summary>
        /// The real player one, and the mod's ground truth for it.
        ///
        /// Nothing here may ask <c>HeroController.instance</c> who player one
        /// is. That singleton is deliberately made to point at a clone for the
        /// duration of an ownership scope — an extra's FSM tick, their
        /// SceneInit, their hit credit — so any code that asks mid-scope gets
        /// the wrong Knight, and code that then acts on it (redispatching a
        /// death, placing a shade, framing the camera) acts on the wrong one.
        ///
        /// Captured on Join, before the first clone and therefore before any
        /// masquerade can exist, and released with the session.
        /// </summary>
        internal static HeroController PlayerOne
        {
            get
            {
                // Re-derive only outside a session, and from the backing field
                // rather than the property: a null singleton makes the getter
                // run FindObjectOfType and DontDestroyOnLoad as a side effect.
                if (_playerOne == null && !Active) _playerOne = Reflect.HeroInstance;
                return _playerOne;
            }
        }

        /// <summary>Every living Knight, player one first. Never null entries.</summary>
        internal static IEnumerable<HeroController> AllHeroes
        {
            get
            {
                var p1 = PlayerOne;
                if (p1 != null) yield return p1;
                foreach (var e in Extras)
                    if (e.Hero != null) yield return e.Hero;
            }
        }

        /// <summary>
        /// Knights the camera and leash may consider. Player one always counts
        /// — everyone else only once they are actually in the room. Vanilla
        /// parks a hero at y = -2000 while it is not in a gameplay scene, and
        /// during a room transition a clone sits there: framing that point
        /// drags the view to the floor, out of sight of both players.
        /// </summary>
        internal static IEnumerable<HeroController> FramableHeroes
        {
            get
            {
                var p1 = PlayerOne;
                if (p1 == null) yield break;
                yield return p1;

                // Mid-transition nobody is anywhere meaningful yet.
                if (_sceneChangePending) yield break;

                var anchor = p1.transform.position;
                foreach (var e in Extras)
                {
                    var h = e.Hero;
                    if (h == null) continue;

                    // NOT isHeroInPosition. That flag is set by vanilla's entry
                    // sequence, which GameManager runs for hero_ctrl alone —
                    // player one. An extra never goes through it, so the flag
                    // holds whatever it happened to hold, and an extra whose
                    // copy read false was invisible to the camera forever: no
                    // centring, no zoom, no split, with the players plainly
                    // both in the room. What it was really standing in for is
                    // "parked out of the room", so test that directly.
                    if (h.transform.position.y < -1000f) continue;
                    if ((h.transform.position - anchor).sqrMagnitude > 400f * 400f) continue;
                    yield return h;
                }
            }
        }

        internal static bool IsExtra(HeroController hc) =>
            hc != null && Extras.Any(e => ReferenceEquals(e.Hero, hc));

        internal static IEnumerable<CoopPlayer> ExtraPlayers => Extras;

        internal static Color TintFor(int number) =>
            Tints[Mathf.Clamp(number - 2, 0, Tints.Length - 1)];

        internal static HeroController HeroForHandler(InputHandler handler) =>
            handler == null ? null
                : Extras.FirstOrDefault(e => ReferenceEquals(e.Input?.Handler, handler))?.Hero;

        /// <summary>A rest (bench heal) restores the whole party's pools.</summary>
        internal static void PartyRest()
        {
            if (!Active || !Plugin.Cfg.IndependentHealth.Value) return;
            var pd = PlayerData.instance;
            foreach (var e in Extras)
            {
                if (e.Hero == null) continue;
                e.Health = pd != null ? pd.CurrentMaxHealth : e.Health;
            }

            // When the rester is an extra, this postfix runs INSIDE their pool
            // swap — writing player one's health here would be clobbered by the
            // swap's restore. Top player one up next frame, after finalizers.
            if (Plugin.Instance != null)
                Plugin.Instance.StartCoroutine(TopUpPlayerOneNextFrame());

            NativeHud.Notify("The party rests");
        }

        private static System.Collections.IEnumerator TopUpPlayerOneNextFrame()
        {
            yield return null;
            var pd = PlayerData.instance;
            if (pd != null && pd.health < pd.CurrentMaxHealth)
                pd.health = pd.CurrentMaxHealth;
        }

        /// <summary>
        /// The extra Knight whose modal (dialogue, prompt) is live right now:
        /// they claimed the interaction and the conversation holds them. Menu
        /// input belongs to them for exactly that window; pausing overrides.
        /// </summary>
        internal static HeroController DialogueOwner()
        {
            // Extras frozen by OUR cutscene sync have controlReqlinquished set
            // by us, not by a modal of their own — reading it as ownership let
            // an extra's buttons drive player one's menus (their B = Cancel).
            if (_extrasFrozen) return null;

            var owner = FsmOwnership.InteractionOwner;
            if (owner == null) return null;
            if (FindExtra(owner) == null) { FsmOwnership.InteractionOwner = null; return null; }
            if (!owner.controlReqlinquished) return null;
            if (GameManager.instance != null && GameManager.instance.isPaused) return null;
            return owner;
        }

        internal static CoopPlayer FindExtra(HeroController hc) =>
            hc == null ? null : Extras.FirstOrDefault(e => ReferenceEquals(e.Hero, hc));

        private static bool _sceneChangePending;

        /// <summary>Why the last Join call refused — surfaced on the HUD by callers.</summary>
        internal static string LastJoinRejection;

        internal static void Init()
        {
            USceneManager.activeSceneChanged += OnSceneChanged;
            InputManager.OnDeviceDetached += OnDeviceDetached;
            InputManager.OnDeviceAttached += OnDeviceAttached;
        }

        internal static void Shutdown()
        {
            USceneManager.activeSceneChanged -= OnSceneChanged;
            InputManager.OnDeviceDetached -= OnDeviceDetached;
            InputManager.OnDeviceAttached -= OnDeviceAttached;
            DespawnAll();
        }

        /// <summary>A pulled cable must not leave a ghost Knight (or shade) around.</summary>
        private static void OnDeviceDetached(InputDevice device)
        {
            if (!Active) return;
            if (Extras.Any(e => e.Input?.Device == device))
            {
                Plugin.Log.LogInfo($"Controller '{device.Name}' detached — removing its player.");
                LeaveByDevice(device);
            }
        }

        /// <summary>
        /// Adds the next player. Pass a device to claim a specific pad (the
        /// Start-to-join path); null takes the first unclaimed one.
        /// </summary>
        internal static void Join(InputDevice device = null)
        {
            // Captures ground truth on the first join, while no clone and so
            // no masquerade exists to answer this question dishonestly.
            var p1 = PlayerOne;
            if (p1 == null)
            {
                LastJoinRejection = "Load a save first";
                Plugin.Log.LogWarning("Cannot join: no player one yet. Load a save first.");
                return;
            }

            // A clone born mid-cutscene or in a menu inherits a state the game
            // never expects to duplicate. Gameplay only.
            if (GameManager.instance == null
                || GameManager.instance.gameState != GlobalEnums.GameState.PLAYING)
            {
                LastJoinRejection = "Can only join during gameplay";
                Plugin.Log.LogInfo("Join ignored: not in gameplay right now.");
                return;
            }

            var max = Mathf.Clamp(Plugin.Cfg.MaxPlayers.Value, 2, 4);
            if (PlayerCount >= max)
            {
                LastJoinRejection = $"Player limit ({max}) reached";
                Plugin.Log.LogInfo($"Already at the {max}-player limit.");
                return;
            }

            device = device ?? PadInput.NextFreeDevice();
            if (device == null)
            {
                LastJoinRejection = "No free gamepad";
                Plugin.Log.LogWarning("No free gamepad. Plug in another controller and press join again.");
                return;
            }

            // Explicit roles override everything: None never joins, player
            // one's pad never joins, a fixed slot joins AS that slot.
            var role = InputAssign.RoleOf(device);
            if (role == PadRole.None || role == PadRole.P1)
            {
                LastJoinRejection = role == PadRole.None
                    ? "That controller is set to None"
                    : "That's player one's controller";
                Plugin.Log.LogInfo($"Join refused for '{device.Name}': role {role}.");
                return;
            }

            var input = PadInput.Create(device);
            if (input == null) return;

            var slot = InputAssign.SlotOf(role);
            var number = slot != 0 ? slot : NextFreeNumber();
            if (Extras.Any(e => e.Number == number))
            {
                LastJoinRejection = $"Player {number} slot is taken";
                return;
            }
            var hero = SpawnClone(p1, number, p1.transform.position + new Vector3(1.5f * (number - 1), 0f, 0f));
            if (hero == null)
            {
                input.Destroy();
                return;
            }

            Reflect.SetInputHandler(hero, input.Handler);
            ApplyWorldIgnores(hero);

            var pd = PlayerData.instance;
            CoopInput.RosterVersion++;
            Extras.Add(new CoopPlayer
            {
                Hero = hero,
                Input = input,
                Number = number,
                Health = pd != null ? pd.CurrentMaxHealth : 5,
                HealthBlue = 0,
            });
            ExcludeFromP1(device);

            Plugin.Log.LogInfo($"Player {number} joined on '{device.Name}'. Now {PlayerCount} players.");
        }

        private static int NextFreeNumber()
        {
            for (var n = 2; n <= 4; n++)
                if (Extras.All(e => e.Number != n)) return n;
            return PlayerCount + 1;
        }

        /// <summary>Re-apply pinning/exclusions when roles change in the menu.</summary>
        internal static void OnAssignmentsChanged()
        {
            var p1 = P1Actions;
            if (p1 == null) return;

            var p1Pad = InputAssign.P1Device();
            if (p1Pad != null)
            {
                // Player one has a DECLARED pad: pin the action set to it — the
                // same twin-proof mechanism extras use. Keyboard keeps working
                // (keyboard bindings are not device-scoped).
                p1.Device = p1Pad;
                p1.IncludeDevices.Clear();
                p1.IncludeDevices.Add(p1Pad);
                foreach (var d in ExcludedFromP1) p1.ExcludeDevices.Remove(d);
                ExcludedFromP1.Clear();
            }
            else
            {
                p1.Device = null;
                p1.IncludeDevices.Clear();
                if (Active) ExcludeFromP1(null);   // keyboard-P1: refresh exclude-all
            }

            // None-role pads are dead to player one even with no one joined.
            foreach (var d in InputManager.Devices)
            {
                if (d == null || !d.IsAttached) continue;
                if (InputAssign.RoleOf(d) == PadRole.None && !p1.ExcludeDevices.Contains(d))
                    p1.ExcludeDevices.Add(d);
            }
            Plugin.Log.LogInfo("Input assignments applied.");
        }

        /// <summary>Puts a slain shade's owner back on the field where it fell.</summary>
        /// <summary>
        /// The stand-in record for player one, made once. He is not in Extras
        /// and never joins it: everything that walks the roster must keep
        /// seeing exactly the extras, or the join limit, leash and camera all
        /// start counting him twice.
        /// </summary>
        private static CoopPlayer _one;

        internal static CoopPlayer OneRecord =>
            _one ?? (_one = new CoopPlayer { Number = 1, IsPlayerOne = true });

        /// <summary>Is anyone other than <paramref name="who"/> still standing?</summary>
        internal static bool AnyoneElseStanding(CoopPlayer who)
        {
            if (who == null || !who.IsPlayerOne)
            {
                if (PlayerOne != null && !PlayerOneDown.Downed) return true;
            }
            foreach (var e in Extras)
            {
                if (e == who || e.Downed || e.Hero == null) continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Put player one back on his feet where his shade was beaten.
        ///
        /// Separate from <see cref="ReviveAt"/> rather than a branch inside it:
        /// that method respawns a destroyed clone, and player one was never
        /// destroyed. Sharing an entry point would mean every future change to
        /// clone respawning has to remember he is not one.
        /// </summary>
        internal static void RevivePlayerOne(CoopPlayer player, Vector3 position)
        {
            var p1 = PlayerOne;
            if (p1 == null || player == null) return;
            var pd = PlayerData.instance;
            var pct = Mathf.Clamp(Plugin.Cfg.ReviveHealthPercent.Value, 10, 100) / 100f;
            var masks = pd != null ? Mathf.Max(1, Mathf.CeilToInt(pd.CurrentMaxHealth * pct)) : 3;
            PlayerOneDown.Revive(p1, position, masks);
            player.Downed = false;
        }

        internal static void ReviveAt(CoopPlayer player, Vector3 position)
        {
            var p1 = PlayerOne;
            if (p1 == null || !Extras.Contains(player)) return;

            var hero = SpawnClone(p1, player.Number, position);
            if (hero == null) { RemoveDowned(player); return; }

            Reflect.SetInputHandler(hero, player.Input.Handler);

            var pd = PlayerData.instance;
            CoopInput.RosterVersion++;
            player.Hero = hero;
            ApplyWorldIgnores(hero);
            player.Downed = false;
            var pct = Mathf.Clamp(Plugin.Cfg.ReviveHealthPercent.Value, 10, 100) / 100f;
            player.Health = pd != null ? Mathf.Max(1, Mathf.CeilToInt(pd.CurrentMaxHealth * pct)) : 3;
            player.HealthBlue = 0;

            NativeHud.Notify($"Player {player.Number} revived");
            Plugin.Log.LogInfo($"Player {player.Number} revived at {player.Health} masks.");
        }

        /// <summary>A downed player leaving entirely (no revive possible/wanted).</summary>
        internal static void RemoveDowned(CoopPlayer player)
        {
            if (!Extras.Contains(player)) return;
            Extras.Remove(player);
            ShadeRevive.DestroyShade(player);
            if (player.Input?.Device != null) RestoreToP1(player.Input.Device);
            player.Input?.Destroy();
            Plugin.Log.LogInfo($"Player {player.Number} left while downed. Now {PlayerCount} players.");
        }

        /// <summary>Removes the most recently joined player.</summary>
        internal static void LeaveLast()
        {
            if (Extras.Count == 0) return;
            Remove(Extras[Extras.Count - 1]);
        }

        internal static void DespawnAll()
        {
            while (Extras.Count > 0) Remove(Extras[Extras.Count - 1]);
            ShadeRevive.ForceRestoreBank();
            _extrasFrozen = false;   // session state must not leak into the next one
            // A downed player one must never survive the end of a session: the
            // next one would start with an invisible, uncontrollable hero.
            PlayerOneDown.ForceUp(PlayerOne);
            _playerOne = null;       // quit-to-menu builds a new hero; re-derive
            SplitScreen.ClearAbandoned();   // a new session gets a fresh try
        }

        /// <summary>Removes whichever player is holding this pad.</summary>
        internal static void LeaveByDevice(InputDevice device)
        {
            var player = Extras.FirstOrDefault(e => e.Input?.Device == device);
            if (player != null) Remove(player);
        }

        private static void Remove(CoopPlayer player)
        {
            CoopInput.RosterVersion++;
            Extras.Remove(player);
            ShadeRevive.DestroyShade(player);
            if (player.Input?.Device != null) RestoreToP1(player.Input.Device);
            if (player.Hero != null) Object.Destroy(player.Hero.gameObject);
            player.Input?.Destroy();
            Plugin.Log.LogInfo($"Player {player.Number} left. Now {PlayerCount} players.");
        }

        /// <summary>
        /// HeroController.Awake destroys any hero that is not the singleton.
        /// Blanking the backing field lets the clone take the "I am the instance"
        /// branch — running SetupGameRefs/SetupPools exactly as vanilla does —
        /// after which we hand the title back to player one.
        /// </summary>
        private static HeroController SpawnClone(HeroController p1, int number, Vector3 position)
        {
            // HeroController.Awake destroys any hero that is not the
            // singleton, so the singleton has to be null while the clone
            // wakes. That is done inside Awake itself (see HeroAwakePatch),
            // not around this call: a null singleton resolves itself with
            // FindObjectOfType + DontDestroyOnLoad, and Instantiate runs
            // Awake and OnEnable for the whole cloned hierarchy, plenty of
            // which reads the singleton. This window only says which spawn
            // is ours; the restore below is a backstop for the patch's.
            GameObject cloneGo;
            SpawnWindow.Begin(p1);
            try
            {
                cloneGo = Object.Instantiate(p1.gameObject, position, p1.transform.rotation);
            }
            finally
            {
                SpawnWindow.End();
                Reflect.HeroInstance = p1;
            }

            cloneGo.name = $"Knight (Player {number})";
            Object.DontDestroyOnLoad(cloneGo);
            Tint(cloneGo, number);

            // The vignette is a DARKNESS overlay with a hole cut around the
            // hero — not a lamp. That distinction is why the clone's stays off
            // even under split-screen, where "give each Knight its own light"
            // sounds obviously right: every pane camera renders the whole
            // scene, so it picks up BOTH overlays, and each one darkens the
            // other's hole. Tried it; the screen went from 46% brightness to
            // 9%. Per-pane lamps need the vignettes on separate layers with
            // each pane camera culling the others, not simply switching on.
            //
            // Scenes also push darkness levels to the singleton hero only, so
            // a clone's copy would sit stale forever regardless.
            var cloneHc = cloneGo.GetComponent<HeroController>();
            if (cloneHc != null)
            {
                if (cloneHc.vignette != null) cloneHc.vignette.gameObject.SetActive(false);
                if (cloneHc.vignetteFSM != null) cloneHc.vignetteFSM.enabled = false;
            }

            var hero = cloneGo.GetComponent<HeroController>();
            if (hero == null)
            {
                Plugin.Log.LogError("Clone has no HeroController; aborting.");
                Object.Destroy(cloneGo);
            }
            return hero;
        }

        /// <summary>
        /// Four identical Knights are unreadable. A gentle color cast per extra
        /// player keeps everyone identifiable without repainting the sprite.
        /// Re-applied on gathers in case damage flashes overwrite it.
        /// </summary>
        private static readonly Color[] Tints =
        {
            new Color(0.72f, 0.85f, 1f),   // player 2: blue cast
            new Color(1f, 0.78f, 0.72f),   // player 3: ember cast
            new Color(0.76f, 1f, 0.78f),   // player 4: pale green cast
        };

        private static void Tint(GameObject go, int number)
        {
            var sprite = go.GetComponent<tk2dSprite>();
            if (sprite == null) return;
            sprite.color = Plugin.Cfg.PlayerTints.Value
                ? Tints[Mathf.Clamp(number - 2, 0, Tints.Length - 1)]
                : Color.white;
        }

        private static void OnSceneChanged(UScene from, UScene to)
        {
            // Quit-to-menu ends the session. Clones are DontDestroyOnLoad and
            // would otherwise follow into the title screen — and from there
            // into whatever save gets loaded next.
            if (Active && to.name.StartsWith("Menu"))
            {
                Plugin.Log.LogInfo("Menu scene — ending co-op session.");
                DespawnAll();
                NativeHud.Invalidate();
                CoopHealthHud.Invalidate();
                return;
            }

            if (Active) _sceneChangePending = true;

            // Interaction claims belong to the room they were made in.
            FsmOwnership.InteractionOwner = null;

            // The HUD lives on the old scene's canvas; rebuild against the new one.
            NativeHud.Invalidate();
            CoopHealthHud.Invalidate();
        }

        private static bool _extrasFrozen;
        private static int _gatheredAtFrame;
        private static int _waitedFrames;

        /// <summary>
        /// Is this Knight actually within the room, rather than out in the
        /// doorway the transition dropped them at? Rooms are authored from the
        /// world origin, so the bounds are simply 0..sceneWidth/Height, with a
        /// small inset so standing exactly on the boundary does not flicker.
        /// </summary>
        private static bool InsideRoom(HeroController hero)
        {
            var gm = GameManager.instance;
            if (gm == null || hero == null) return true;
            var w = gm.sceneWidth;
            var h = gm.sceneHeight;
            if (w <= 0f || h <= 0f) return true;      // unknown: do not block
            var p = hero.transform.position;
            return p.x > 0.5f && p.x < w - 0.5f && p.y > 0.5f && p.y < h - 0.5f;
        }

        /// <summary>How many times a Knight has been pulled to player one.</summary>
        internal static int Gathers;

        /// <summary>
        /// When the game takes control from player one (dialogue, cutscenes,
        /// scripted moments), the extras must not keep running through the
        /// scene. Mirror the freeze with the same vanilla switch.
        /// </summary>
        private static void SyncCutsceneFreeze(HeroController p1)
        {
            var shouldFreeze = Plugin.Cfg.FreezeExtrasInCutscenes.Value
                && p1.controlReqlinquished
                && GameManager.instance != null
                && !GameManager.instance.isPaused
                // Sitting at a bench relinquishes control too — but a bench is
                // a lounge, not a cutscene; friends stay free.
                && (PlayerData.instance == null || !PlayerData.instance.atBench);

            if (shouldFreeze == _extrasFrozen) return;
            _extrasFrozen = shouldFreeze;

            foreach (var e in Extras)
            {
                if (e.Hero == null) continue;
                if (shouldFreeze) e.Hero.RelinquishControl();
                else e.Hero.RegainControl();
            }
        }

        /// <summary>Driven from Plugin's MonoBehaviour Update.</summary>
        internal static void Tick()
        {
            if (!Active) return;

            var p1 = PlayerOne;
            if (p1 == null) return;

            SyncCutsceneFreeze(p1);

            // Ride along with a living Knight while down, so doors still fire.
            PlayerOneDown.Tick(p1);

            if (HandleSceneChange(p1)) return;

            ApplyLeash(p1);
        }

        /// <summary>
        /// Get everyone through the door together. True while that is still in
        /// progress and nothing else should run.
        /// </summary>
        private static bool HandleSceneChange(HeroController p1)
        {
            if (_sceneChangePending)
            {
                // Wait for vanilla to finish walking player one in, then put
                // everyone on the same doorstep. Extras do not take part in the
                // transition at all — no LeaveScene, no EnterScene — so without
                // this they keep the coordinates they had in the PREVIOUS room,
                // which is how a party ends up spread between a room's top and
                // bottom entrances having walked through one door.
                if (!p1.isHeroInPosition) return true;

                // isHeroInPosition goes true when the entry sequence STARTS,
                // not when it ends: a play session logged it at x = -1.50,
                // outside the room, still in the doorway. Collecting there puts
                // everyone on the threshold and vanilla then walks player one
                // inward alone — the reported bug, a party arriving in two
                // different spots.
                //
                // Waiting for him to STOP was the obvious repair and it was
                // wrong: a player holds the stick through a door, so he never
                // stops, the gather never fires, and the pending flag stays set
                // — which also keeps the camera standing down. Wait for him to
                // be INSIDE THE ROOM instead. That is the actual condition the
                // doorway case violates, and it does not care what the player
                // is holding.
                if (!InsideRoom(p1) && ++_waitedFrames < 90) return true;
                _waitedFrames = 0;

                Plugin.Log.LogInfo(
                    $"Room change: player one inside the room at {p1.transform.position}, "
                    + $"collecting {Extras.Count} extra(s).");
                GatherToP1("scene change");

                // Hand them the flag vanilla would have set if they had been
                // through the entry sequence. Other systems read it, and an
                // extra that never enters a scene never gets it back.
                foreach (var e in Extras)
                    if (e.Hero != null) e.Hero.isHeroInPosition = true;

                CoopCamera.Reset();   // snap to the new room, don't sweep across it
                _gatheredAtFrame = Time.frameCount;
                _waitedFrames = 0;
                _sceneChangePending = false;
                return true;
            }

            CollectStragglers(p1);
            return false;
        }

        /// <summary>
            // Vanilla can still be walking player one further into the room
            // for a few frames after it says he has arrived, which would leave
            // the party standing on the doorstep behind him. So for a short
        /// window afterwards, collect anyone who has been left BEHIND — and only
        /// them.
        ///
        /// Not an unconditional re-gather. GatherToP1 teleports, so doing it
        /// every frame of the window would overwrite a player's own movement
        /// for the whole window: press right after a door and not move. Only a
        /// Knight further away than the doorstep gap gets pulled, so anyone
        /// already with the group keeps their input.
        /// </summary>
        private static void CollectStragglers(HeroController p1)
        {
            if (_gatheredAtFrame > 0)
            {
                var since = Time.frameCount - _gatheredAtFrame;
                if (since >= 120)
                {
                    _gatheredAtFrame = 0;
                    CoopCamera.Reset();   // once, at the end, so it can smooth again
                }
                else
                {
                    foreach (var e in Extras)
                    {
                        if (e.Hero == null) continue;
                        var gap = Vector2.Distance(
                            p1.transform.position, e.Hero.transform.position);
                        // Twenty-five units: genuinely stranded, not merely
                        // apart. This window used to compensate for gathering
                        // too early, so it was tightened to three — and with
                        // the gather now waiting for player one to actually
                        // stop, that turned it into a leash: for two seconds
                        // after every door, walking three units from player one
                        // teleported you back. Reported from play as "p2
                        // teleports to p1 for no reason".
                        //
                        // The gather itself is the mechanism now. This is only
                        // a net for a Knight left somewhere it could never walk
                        // back from.
                        // Ten units: further than a door's walk-in, closer than
                        // anywhere a player would deliberately wander in the
                        // couple of seconds after arriving. Three was a leash
                        // (it fired constantly); twenty-five never fired for the
                        // doorway case it exists for.
                        if (gap > 10f) SnapTo(e.Hero, p1, e, "left behind at the door");
                    }
                }
            }
        }

        /// <summary>
        /// Leash: pull a straggler back rather than softlocking — but zoom
        /// comes FIRST. "Screen" mode (-1) only teleports once the group
        /// cannot be framed even at maximum zoom (with hysteresis so the
        /// camera gets its chance); fixed distances remain for preference;
        /// 0 disables entirely.
        /// </summary>
        private static void ApplyLeash(HeroController p1)
        {
            var leash = Plugin.Cfg.LeashDistance.Value;
            if (leash == 0f) return;

            if (leash < 0f)
            {
                // Screen mode exists to stop players losing each other when
                // the camera cannot stretch far enough. Split-screen solves
                // that properly, so when it is on, this stands down entirely
                // rather than racing it: both trigger on exactly the same
                // "the view cannot hold the group" condition, and whichever
                // ran first would win — with the leash winning meaning the
                // stragglers get yanked together a frame before the screen
                // would have divided, so the split could never be seen.
                // Fixed distances ("Near"/"Far") are a stated preference and
                // keep working.
                if (Plugin.Cfg.SplitScreen.Value) return;

                var cam = GameCameras.instance != null && GameCameras.instance.tk2dCam != null
                    ? GameCameras.instance.tk2dCam.GetComponent<Camera>() : null;
                cam = cam != null ? cam
                    : (PlayerOne != null ? Camera.main : null);
                if (cam == null) return;

                var heroes = FramableHeroes.ToList();
                if (heroes.Count < 2) return;

                // Inside a camera lock zone the co-op camera stands down — the
                // leash must too, or a boss arena becomes a teleport storm.
                var cc = GameCameras.instance != null ? GameCameras.instance.cameraController : null;
                if (cc != null && cc.lockZoneList != null && cc.lockZoneList.Count > 0) return;

                // World half-heights, not orthographic sizes: this camera is
                // perspective and its orthographicSize is an inert 480, which
                // used to make both numbers 480 and this test never true.
                if (!CoopCamera.Framing(heroes, cam, out var needed, out var allowed, out _, out _)) return;
                if (needed <= allowed * 1.15f) return;   // camera can (nearly) frame it

                foreach (var e in Extras)
                {
                    if (e.Hero == null) continue;
                    SnapTo(e.Hero, p1, e, $"screen leash (need {needed:F0} > {allowed:F0})");
                }
                return;
            }

            foreach (var e in Extras)
            {
                if (e.Hero == null) continue;
                var gap = Vector2.Distance(p1.transform.position, e.Hero.transform.position);
                if (gap > leash) SnapTo(e.Hero, p1, e, $"leash ({gap:F0} > {leash:F0})");
            }
        }

        /// <summary>
        /// Objects marked IgnoreHeroCollision only ignore the singleton's
        /// colliders — extras would still bump into props player one walks
        /// through. Extend every such object's ignore set to this Knight.
        /// </summary>
        internal static void ApplyWorldIgnores(HeroController hero)
        {
            if (hero == null) return;
            var heroCols = hero.GetComponents<Collider2D>();
            foreach (var ihc in Object.FindObjectsByType<IgnoreHeroCollision>(FindObjectsSortMode.None))
            {
                var col = ihc.GetComponent<Collider2D>();
                if (col == null) continue;
                foreach (var hc in heroCols)
                    Physics2D.IgnoreCollision(col, hc, ignore: true);
            }
        }

        internal static void GatherToP1(string reason)
        {
            var p1 = PlayerOne;
            if (p1 == null) return;

            foreach (var e in Extras)
            {
                if (e.Hero != null)
                {
                    SnapTo(e.Hero, p1, e, reason);
                    ApplyWorldIgnores(e.Hero);   // new room, new ignore set
                }
                else if (e.Downed)
                {
                    // Shades are scene objects; a room change destroyed the old
                    // one. The shade follows its team — re-materialize it near
                    // player one so revival stays possible.
                    ShadeRevive.DestroyShade(e);
                    ShadeRevive.SpawnShade(e, p1.transform.position + new Vector3(2f, 0.5f, 0f));
                }
            }
        }

        private static void SnapTo(HeroController hero, HeroController target, CoopPlayer player, string reason)
        {
            Tint(hero.gameObject, player.Number);
            hero.transform.position = target.transform.position + new Vector3(1.0f, 0.5f, 0f);
            var rb = hero.GetComponent<Rigidbody2D>();
            if (rb != null) rb.linearVelocity = Vector2.zero;
            // Info, not Debug: BepInEx ships with Debug filtered out, so the
            // one line that says whether a room change actually collected the
            // party was invisible in every log a player could send back.
            Gathers++;
            Plugin.Log.LogInfo($"{hero.gameObject.name} pulled to player one: {reason}");
        }

        private static HeroActions P1Actions =>
            GameManager.instance != null && GameManager.instance.inputHandler != null
                ? GameManager.instance.inputHandler.inputActions
                : null;

        private static readonly List<InputDevice> ExcludedFromP1 = new List<InputDevice>();

        private static void ExcludeFromP1(InputDevice device)
        {
            var p1 = P1Actions;
            if (p1 == null || device == null) return;

            // Pin ONLY on an explicit menu assignment. Pinning to a GUESSED
            // device is how player one loses inputs entirely when the guess is a
            // ghost twin that reports presence but feeds nothing. With a merely
            // observed pad, excluding the joiner's pad is enough and cannot
            // strand anyone.
            var p1Pad = InputAssign.AssignedP1Device();
            if (p1Pad != null)
            {
                // Player one declared this pad: PIN their action set to it.
                // Pinning beats excluding — it is immune to Steam Input's ghost
                // twins AND cannot strand player one, which muting every pad did
                // whenever we wrongly assumed keyboard.
                p1.Device = p1Pad;
                p1.IncludeDevices.Clear();
                p1.IncludeDevices.Add(p1Pad);
                foreach (var d in ExcludedFromP1) p1.ExcludeDevices.Remove(d);
                ExcludedFromP1.Clear();
                return;
            }

            if (device != null && InputAssign.P1Device() != null)
            {
                // Player one is on an observed pad: exclude just the joiner's.
                if (!p1.ExcludeDevices.Contains(device)) p1.ExcludeDevices.Add(device);
                ExcludedFromP1.Add(device);
                return;
            }

            if (device != null)
            {
                // Player one is genuinely on keyboard: while anyone is joined,
                // player one needs no pad at all — so exclude every attached
                // device. This is what defeats Steam Input's device twins (the
                // physical pad appears as both a raw device and a virtual
                // "Xbox Controller"; excluding only the joined twin leaves the
                // other one driving player one). Start-join still works: it
                // polls devices directly, never player one's action set.
                foreach (var d in InputManager.Devices)
                {
                    if (d == null || !d.IsAttached) continue;
                    if (!p1.ExcludeDevices.Contains(d)) p1.ExcludeDevices.Add(d);
                    if (!ExcludedFromP1.Contains(d)) ExcludedFromP1.Add(d);
                }
                return;
            }

            if (device == null) return;
            if (!p1.ExcludeDevices.Contains(device)) p1.ExcludeDevices.Add(device);
            ExcludedFromP1.Add(device);
        }

        private static void RestoreToP1(InputDevice device)
        {
            // Keyboard-declared player one: exclusions clear only when the last
            // extra leaves (any earlier and a duplicate twin would leak back).
            if (Extras.Count > 0) return;   // still players in — keep the pin/exclusions

            var actions = P1Actions;
            if (actions != null)
            {
                foreach (var d in ExcludedFromP1) actions.ExcludeDevices.Remove(d);
                // Un-pin: player one goes back to accepting any device solo.
                actions.Device = null;
                actions.IncludeDevices.Clear();
            }
            ExcludedFromP1.Clear();
        }

        /// <summary>A pad plugged in mid-session: keyboard-declared player one
        /// ignores it too (it may be a twin, or player three's — either way
        /// Start-join reads the device directly).</summary>
        private static void OnDeviceAttached(InputDevice device)
        {
            if (!Active || InputAssign.P1Device() != null) return;
            var p1 = P1Actions;
            if (p1 == null || device == null) return;
            if (!p1.ExcludeDevices.Contains(device)) p1.ExcludeDevices.Add(device);
            if (!ExcludedFromP1.Contains(device)) ExcludedFromP1.Add(device);
        }
    }
}
