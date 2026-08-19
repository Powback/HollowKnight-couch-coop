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
    /// Player one is always the vanilla <see cref="HeroController.instance"/>.
    /// Everyone else is a clone of it.
    /// </summary>
    internal static class CoopManager
    {
        private static readonly List<CoopPlayer> Extras = new List<CoopPlayer>();

        internal static bool Active => Extras.Count > 0;
        internal static int PlayerCount => Extras.Count + 1;

        /// <summary>Every living Knight, player one first. Never null entries.</summary>
        internal static IEnumerable<HeroController> AllHeroes
        {
            get
            {
                var p1 = HeroController.instance;
                if (p1 != null) yield return p1;
                foreach (var e in Extras)
                    if (e.Hero != null) yield return e.Hero;
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
            NativeHud.Notify("The party rests");
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
        }

        internal static void Shutdown()
        {
            USceneManager.activeSceneChanged -= OnSceneChanged;
            InputManager.OnDeviceDetached -= OnDeviceDetached;
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
            var p1 = HeroController.instance;
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

            var input = PadInput.Create(device);
            if (input == null) return;

            var number = PlayerCount + 1;
            var hero = SpawnClone(p1, number, p1.transform.position + new Vector3(1.5f * (number - 1), 0f, 0f));
            if (hero == null)
            {
                input.Destroy();
                return;
            }

            Reflect.SetInputHandler(hero, input.Handler);

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

        /// <summary>Puts a slain shade's owner back on the field where it fell.</summary>
        internal static void ReviveAt(CoopPlayer player, Vector3 position)
        {
            var p1 = HeroController.instance;
            if (p1 == null || !Extras.Contains(player)) return;

            var hero = SpawnClone(p1, player.Number, position);
            if (hero == null) { RemoveDowned(player); return; }

            Reflect.SetInputHandler(hero, player.Input.Handler);

            var pd = PlayerData.instance;
            CoopInput.RosterVersion++;
            player.Hero = hero;
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
            var saved = Reflect.HeroInstance;
            GameObject cloneGo;
            try
            {
                Reflect.HeroInstance = null;
                cloneGo = Object.Instantiate(p1.gameObject, position, p1.transform.rotation);
            }
            finally
            {
                Reflect.HeroInstance = saved;
            }

            cloneGo.name = $"Knight (Player {number})";
            Object.DontDestroyOnLoad(cloneGo);
            Tint(cloneGo, number);

            // One screen, one darkness overlay: scenes push darkness levels to
            // the singleton hero only, so a clone's vignette copy would sit in
            // a stale state forever (and double-darken if joined in the dark).
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

            // The HUD lives on the old scene's canvas; rebuild against the new one.
            NativeHud.Invalidate();
            CoopHealthHud.Invalidate();
        }

        private static bool _extrasFrozen;

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
                && !GameManager.instance.isPaused;

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

            var p1 = HeroController.instance;
            if (p1 == null) return;

            SyncCutsceneFreeze(p1);

            if (_sceneChangePending)
            {
                if (p1.isHeroInPosition)
                {
                    GatherToP1("scene change");
                    CoopCamera.Reset();   // snap to the new room, don't sweep across it
                    _sceneChangePending = false;
                }
                return;
            }

            // Leash: an extra player who falls behind, dies into a pit, or gets
            // left in a sealed room is pulled back rather than softlocking.
            var leash = Plugin.Cfg.LeashDistance.Value;
            if (leash <= 0f) return;

            foreach (var e in Extras)
            {
                if (e.Hero == null) continue;
                var gap = Vector2.Distance(p1.transform.position, e.Hero.transform.position);
                if (gap > leash) SnapTo(e.Hero, p1, e, $"leash ({gap:F0} > {leash:F0})");
            }
        }

        internal static void GatherToP1(string reason)
        {
            var p1 = HeroController.instance;
            if (p1 == null) return;

            foreach (var e in Extras)
            {
                if (e.Hero != null)
                {
                    SnapTo(e.Hero, p1, e, reason);
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
            Plugin.Log.LogDebug($"{hero.gameObject.name} pulled to player one: {reason}");
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
            if (!p1.ExcludeDevices.Contains(device)) p1.ExcludeDevices.Add(device);
            ExcludedFromP1.Add(device);
        }

        private static void RestoreToP1(InputDevice device)
        {
            P1Actions?.ExcludeDevices.Remove(device);
            ExcludedFromP1.Remove(device);
        }
    }
}
