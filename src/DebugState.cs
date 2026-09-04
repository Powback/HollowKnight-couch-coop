using System.Collections.Generic;
using System.IO;
using CoopKit;
using InControl;
using UnityEngine;
using USceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HKCouchCoop
{
    /// <summary>
    /// The mod's state channel: a loopback JSON endpoint that reports what the
    /// co-op session currently *is*, and can drive it.
    ///
    /// This exists because a couch co-op mod cannot be tested any other way.
    /// The whole feature is "a second Knight exists, is driven by a second pad,
    /// and moves independently" — and none of that is observable from outside
    /// the process. A log line proves a code path ran; a screenshot proves
    /// pixels changed. Neither can answer "is player two at a different
    /// position than player one", which is the actual claim. Every version
    /// after 0.01 went untested for exactly this reason.
    ///
    /// It is the same idea as the DevTools endpoint that makes a browser-UI
    /// game scriptable, and deliberately the same shape: ask over a socket,
    /// get JSON back.
    ///
    /// SAFETY
    /// ------
    /// This reports internal state and, through <c>/cmd</c>, changes it. It is
    /// therefore:
    ///   * <b>off unless switched on</b> — the default config value is false,
    ///     so a released build with an untouched config never opens a socket;
    ///   * <b>loopback only</b> — bound to 127.0.0.1, never a wildcard, so it
    ///     is not reachable from the network even when enabled;
    ///   * <b>opt-in per run</b> via the <c>HKCC_DEBUG_PORT</c> environment
    ///     variable, which is how the test harness turns it on without editing
    ///     the player's config file.
    /// </summary>
    internal static class DebugState
    {
        private static DebugServer _server;

        internal static bool Running => _server != null;

        internal static void Init()
        {
            var port = ResolvePort();
            if (port <= 0) return;

            _server = new DebugServer(m => Plugin.Log.LogInfo(m))
                .Route("/state", _ => Snapshot())
                .Route("/devices", _ => Devices())
                .Route("/cmd", Command)
                .Route("/ping", _ => Json.Object()
                    .Add("ok", true)
                    .Add("mod", "HKCouchCoop")
                    .Add("version", Plugin.Version)
                    .Close());

            // The file is the belt to the socket's braces: if Wine, a firewall
            // or a port clash keeps the socket unreachable, the harness can
            // still read state, and a crash leaves the last snapshot on disk.
            var snapshotPath = Path.Combine(Paths(), "hkcoop-state.json");
            _server.Snapshot(snapshotPath, "/state");

            if (_server.Start(port))
            {
                Plugin.Log.LogWarning(
                    $"DEBUG STATE CHANNEL ENABLED on 127.0.0.1:{_server.Port} — " +
                    "this exposes and can drive game state. It is for automated " +
                    "testing; turn it off for normal play.");
                Plugin.Log.LogInfo($"State snapshot file: {snapshotPath}");
            }
            else
            {
                _server = null;
            }
        }

        internal static void Tick()
        {
            _server?.Pump(Time.unscaledTimeAsDouble);
        }

        internal static void Shutdown()
        {
            _server?.Stop();
            _server = null;
        }

        /// <summary>
        /// Env var wins over config so a harness can enable this for one run
        /// without writing to the player's config file — and without leaving
        /// it enabled afterwards.
        /// </summary>
        private static int ResolvePort()
        {
            var env = System.Environment.GetEnvironmentVariable("HKCC_DEBUG_PORT");
            if (!string.IsNullOrEmpty(env) && int.TryParse(env, out var p) && p > 0)
                return p;
            return Plugin.Cfg.DebugServer.Value ? Plugin.Cfg.DebugServerPort.Value : 0;
        }

        private static string Paths()
        {
            try { return BepInEx.Paths.BepInExRootPath; }
            catch { return "."; }
        }

        // ── state ─────────────────────────────────────────────────────────

        private static string Snapshot()
        {
            var gm = GameManager.instance;
            var pd = PlayerData.instance;
            var p1 = CoopManager.PlayerOne;

            var players = new List<string>();
            players.Add(PlayerJson(1, p1, pd));
            foreach (var e in CoopManager.ExtraPlayers)
                players.Add(ExtraJson(e));

            return Json.Object()
                .Add("ok", true)
                .Add("mod", "HKCouchCoop")
                .Add("version", Plugin.Version)
                .Add("gameVersion", Application.version)
                .Add("time", Time.unscaledTime)
                .Add("frame", Time.frameCount)
                .Add("scene", SceneName())
                .Add("gameState", gm != null ? gm.gameState.ToString() : null)
                .Add("paused", gm != null && gm.gameState == GlobalEnums.GameState.PAUSED)
                .Add("inGameplay", gm != null && gm.gameState == GlobalEnums.GameState.PLAYING)
                .Add("saveLoaded", p1 != null)
                .Add("playerCount", CoopManager.PlayerCount)
                .Add("extraCount", CoopManager.PlayerCount - 1)
                .Add("lastJoinRejection", CoopManager.LastJoinRejection)
                .AddRaw("players", Json.Array(players))
                .AddRaw("devices", DevicesArray())
                .Close();
        }

        private static string SceneName()
        {
            try { return USceneManager.GetActiveScene().name; }
            catch { return null; }
        }

        private static string PlayerJson(int number, HeroController hero, PlayerData pd)
        {
            var j = Json.Object().Add("n", number).Add("isPlayerOne", true);
            AddTransform(j, hero);
            j.Add("alive", hero != null)
             .Add("downed", false)
             .Add("health", pd != null ? pd.health : -1)
             .Add("healthBlue", pd != null ? pd.healthBlue : -1)
             .Add("maxHealth", pd != null ? pd.CurrentMaxHealth : -1)
             .Add("soul", pd != null ? pd.MPCharge : -1)
             .Add("device", (string)null)
             .Add("deviceGuid", (string)null)
             .Add("deviceIndex", -1);
            return j.Close();
        }

        private static string ExtraJson(CoopPlayer e)
        {
            var j = Json.Object().Add("n", e.Number).Add("isPlayerOne", false);
            AddTransform(j, e.Hero);
            j.Add("alive", e.Hero != null && !e.Downed)
             .Add("downed", e.Downed)
             .Add("health", e.Health)
             .Add("healthBlue", e.HealthBlue)
             .Add("maxHealth", PlayerData.instance != null
                 ? PlayerData.instance.CurrentMaxHealth : -1)
             .Add("soul", e.Soul)
             .Add("device", e.Input != null && e.Input.Device != null
                 ? e.Input.Device.Name : null)
             .Add("deviceGuid", e.Input != null && e.Input.Device != null
                 ? e.Input.Device.GUID.ToString() : null)
             .Add("deviceIndex", DeviceIndex(e.Input != null ? e.Input.Device : null));
            return j.Close();
        }

        private static void AddTransform(Json j, HeroController hero)
        {
            if (hero == null)
            {
                j.AddRaw("pos", "null");
                return;
            }
            var p = hero.transform.position;
            j.AddRaw("pos", Json.Object().Add("x", p.x).Add("y", p.y).Add("z", p.z).Close());
        }

        // ── devices ───────────────────────────────────────────────────────

        /// <summary>
        /// What InControl believes is plugged in. The first thing to check when
        /// a harness's virtual pads seem to do nothing: a pad the game never
        /// enumerated cannot join, and that failure is otherwise silent.
        /// </summary>
        private static string DevicesArray()
        {
            var list = new List<string>();
            try
            {
                var devices = InputManager.Devices;
                if (devices != null)
                {
                    for (var i = 0; i < devices.Count; i++)
                    {
                        var d = devices[i];
                        if (d == null) continue;
                        // Name alone cannot identify a pad: InControl renames
                        // every device after the profile it matched, so four
                        // different controllers are all "Xbox Controller".
                        // GUID is what tells them apart, and Passive is what
                        // explains why there are more devices than pads —
                        // InControl publishes a duplicate for each pad that
                        // two of its device managers both see.
                        list.Add(Json.Object()
                            .Add("index", i)
                            .Add("name", d.Name)
                            .Add("guid", d.GUID.ToString())
                            .Add("meta", d.Meta)
                            .Add("sortOrder", d.SortOrder)
                            .Add("deviceClass", d.DeviceClass.ToString())
                            .Add("deviceStyle", d.DeviceStyle.ToString())
                            .Add("passive", d.Passive)
                            .Add("attached", d.IsAttached)
                            .Add("anyButtonPressed", d.AnyButtonIsPressed)
                            .Add("claimed", PadInput.Claimed.Contains(d))
                            .Close());
                    }
                }
            }
            catch { /* reported as an empty list rather than a 500 */ }
            return Json.Array(list);
        }

        private static string LoadSave(System.Collections.Generic.IDictionary<string, string> q)
        {
            var gm = GameManager.instance;
            if (gm == null)
                return Json.Object().Add("ok", false)
                    .Add("error", "no GameManager yet — the game is still starting").Close();

            string raw;
            var slot = 0;
            if (q.TryGetValue("slot", out raw)) int.TryParse(raw, out slot);

            if (CoopManager.PlayerOne != null)
                return Json.Object().Add("ok", true).Add("did", "loadsave")
                    .Add("note", "a save is already loaded")
                    .Add("scene", SceneName()).Close();

            gm.LoadGameFromUI(slot);
            return Json.Object().Add("ok", true).Add("did", "loadsave")
                .Add("slot", slot)
                .Add("note", "load dispatched; poll /state until inGameplay is true")
                .Close();
        }

        private static int DeviceIndex(InputDevice device)
        {
            if (device == null) return -1;
            try
            {
                var devices = InputManager.Devices;
                if (devices != null)
                    for (var i = 0; i < devices.Count; i++)
                        if (ReferenceEquals(devices[i], device)) return i;
            }
            catch { }
            return -1;
        }

        private static string Devices()
        {
            return Json.Object()
                .Add("ok", true)
                .AddRaw("devices", DevicesArray())
                .Close();
        }

        // ── commands ──────────────────────────────────────────────────────

        /// <summary>
        /// Drive the session directly. This is for isolating a failure, not for
        /// replacing input: a scenario that only ever joins through /cmd never
        /// tests the Start-to-join path a player actually uses. Use the pads
        /// first; use this to tell "spawning is broken" apart from "the pad
        /// never reached the game".
        /// </summary>
        private static string Command(System.Collections.Generic.IDictionary<string, string> q)
        {
            string what;
            if (!q.TryGetValue("do", out what) || string.IsNullOrEmpty(what))
                return Json.Object().Add("ok", false).Add("error", "no 'do' parameter")
                    .Add("accepts", "loadsave|join|leave|leaveall").Close();

            var before = CoopManager.PlayerCount;
            switch (what.ToLowerInvariant())
            {
                case "loadsave":
                    // Hands-free entry into gameplay. Everything this mod does
                    // requires a loaded save, and the only other way in is
                    // driving the title menus blind — which tests the menus,
                    // not the mod, and breaks whenever a menu moves. This is
                    // the exact method the save-slot button calls.
                    return LoadSave(q);
                case "join":
                    CoopManager.Join();
                    break;
                case "leave":
                    CoopManager.LeaveLast();
                    break;
                case "leaveall":
                    CoopManager.DespawnAll();
                    break;
                default:
                    return Json.Object().Add("ok", false)
                        .Add("error", "unknown command '" + what + "'")
                        .Add("accepts", "loadsave|join|leave|leaveall").Close();
            }

            return Json.Object()
                .Add("ok", true)
                .Add("did", what)
                .Add("playerCountBefore", before)
                .Add("playerCountAfter", CoopManager.PlayerCount)
                .Add("rejection", CoopManager.LastJoinRejection)
                .Close();
        }
    }
}
