using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using CoopKit;
using HarmonyLib;
using UnityEngine;

namespace HKCouchCoop
{
    internal sealed class Config
    {
        internal readonly ConfigEntry<string> PadAssignments;
        internal readonly ConfigEntry<bool> IndependentHealth;
        internal readonly ConfigEntry<bool> ShadeRevive;
        internal readonly ConfigEntry<bool> JoinWithStart;
        internal readonly ConfigEntry<KeyCode> JoinKey;
        internal readonly ConfigEntry<KeyCode> LeaveKey;
        internal readonly ConfigEntry<int> MaxPlayers;
        internal readonly ConfigEntry<float> LeashDistance;
        internal readonly ConfigEntry<bool> IgnoreVersionCheck;
        internal readonly ConfigEntry<bool> DebugServer;
        internal readonly ConfigEntry<int> DebugServerPort;

        internal readonly ConfigEntry<int> ReviveHealthPercent;
        internal readonly ConfigEntry<float> LeaveHoldSeconds;
        internal readonly ConfigEntry<bool> FreezeExtrasInCutscenes;
        internal readonly ConfigEntry<bool> PlayerTints;
        internal readonly ConfigEntry<bool> CameraZoom;
        internal readonly ConfigEntry<bool> FriendlyFire;
        internal readonly ConfigEntry<bool> SplitScreen;
        internal readonly ConfigEntry<bool> SplitRotate;
        internal readonly ConfigEntry<float> SplitMergeMargin;
        internal readonly ConfigEntry<float> MaxZoomFactor;
        internal readonly ConfigEntry<float> ZoomMargin;
        internal readonly ConfigEntry<float> ZoomSpeed;
        internal readonly ConfigEntry<float> FollowSpeed;

        internal Config(ConfigFile f)
        {
            PadAssignments = f.Bind("General", "PadAssignments", "",
                "Explicit device roles, managed from the in-game menu (Options > Game). " +
                "Format: 'DeviceName#ordinal:Role;...' with roles Auto/None/P1/P2/P3/P4.");
            IndependentHealth = f.Bind("General", "IndependentHealth", true,
                "Each extra player has their own masks (player one keeps the real save's). " +
                "Off = one shared pool: anyone's hit costs the team, any death is a team death.");
            ShadeRevive = f.Bind("General", "ShadeRevive", true,
                "When an extra player dies, their real Shade rises where they fell; teammates " +
                "defeat it to revive them at half masks. Off = a dead player is out until they " +
                "rejoin with Start. Requires IndependentHealth.");
            JoinWithStart = f.Bind("General", "JoinWithStart", true,
                "Press Start on a spare pad to join (the pause is swallowed for that press); " +
                "hold Start about a second to leave. Nobody's input is reconfigured — the pause " +
                "is intercepted at the game's single pause entry point instead.");
            JoinKey = f.Bind("General", "JoinKey", KeyCode.F6,
                "Adds the next player, using the first unclaimed gamepad.");
            LeaveKey = f.Bind("General", "LeaveKey", KeyCode.F7,
                "Removes the most recently joined player.");
            MaxPlayers = f.Bind("General", "MaxPlayers", 4,
                "Total players including player one (2-4). Each extra player needs its own gamepad.");
            LeashDistance = f.Bind("General", "LeashDistance", -1f,
                "-1 = Screen mode: zoom out first, teleport stragglers only when even max zoom " +
                "cannot frame everyone. Positive = fixed distance in world units. 0 disables.");
            IgnoreVersionCheck = f.Bind("General", "IgnoreVersionCheck", false,
                "Load even if the game version does not match the one this build targets.");

            // Off by default, and deliberately not in the in-game menu: this is
            // a testing tool that exposes and can drive game state. Enabling it
            // has to be a decision someone makes on purpose. The e2e harness
            // sets HKCC_DEBUG_PORT for one run instead of touching this.
            DebugServer = f.Bind("Debug", "DebugServer", false,
                "Serve live co-op state as JSON on 127.0.0.1 for automated testing, and " +
                "accept commands that spawn/remove players. Loopback only, never the network. " +
                "Leave this off for normal play. The e2e harness enables it per-run with the " +
                "HKCC_DEBUG_PORT environment variable, which overrides this setting.");
            DebugServerPort = f.Bind("Debug", "DebugServerPort", 27600,
                "Loopback port for the debug state channel when DebugServer is on.");

            ReviveHealthPercent = f.Bind("General", "ReviveHealthPercent", 50,
                "Masks a revived player comes back with, as a percent of max health (10-100).");
            LeaveHoldSeconds = f.Bind("General", "LeaveHoldSeconds", 1.2f,
                "How long a joined player holds Start to leave. Raise it if players drop out by accident.");
            FreezeExtrasInCutscenes = f.Bind("General", "FreezeExtrasInCutscenes", true,
                "Freeze extra players whenever the game takes control from player one (dialogue, " +
                "cutscenes) so nobody wanders through story moments. Off = extras stay free.");
            PlayerTints = f.Bind("General", "PlayerTints", true,
                "Give each extra player a soft color cast (blue, ember, green) so everyone " +
                "stays identifiable. Off = all Knights look identical.");
            FriendlyFire = f.Bind("General", "FriendlyFire", false,
                "Let Knights hurt each other. Full damage, including the killing blow — an " +
                "extra who dies leaves a Shade for the others to fight, and player one dying " +
                "is a real game-over. Off by default: it changes the game a lot.");
            SplitRotate = f.Bind("Camera", "SplitRotate", false,
                "Let the split follow where the players actually are, instead of only cutting " +
                "straight down or straight across: two players get a divider at any angle, " +
                "three or four get wedges that pivot as they move. Off by default because it " +
                "reaches the screen a different way: pane cameras render into textures and a " +
                "compositor paints the regions. It drew a black screen until v0.8.1 — the " +
                "triangle fan wound away from the camera under GL.LoadOrtho, so Unlit/Texture " +
                "back-face culled every triangle and discarded the fill in silence. Both " +
                "windings are emitted now. Verified drawing at 7.1 brightness against 10.5 for " +
                "the whole view.");
            SplitScreen = f.Bind("Camera", "SplitScreen", true,
                "Divide the screen when the Knights spread further than one view can hold, " +
                "and merge it back when they regroup. One pane per Knight, up to four.");
            SplitMergeMargin = f.Bind("Camera", "SplitMergeMargin", 0.15f,
                "Deadband around the split threshold, as a fraction. Splitting and merging " +
                "use different thresholds so a marginal group cannot tear the screen in half " +
                "and back together every few frames.");
            CameraZoom = f.Bind("Camera", "AutoZoom", true,
                "Widen the view to keep every player in frame.");
            MaxZoomFactor = f.Bind("Camera", "MaxZoomFactor", 1.6f,
                "Largest allowed zoom-out, as a multiple of the game's normal view size.");
            ZoomMargin = f.Bind("Camera", "ZoomMargin", 6f,
                "Extra world units kept around the group when zooming.");
            ZoomSpeed = f.Bind("Camera", "ZoomSpeed", 3f, "How quickly the zoom reacts.");
            FollowSpeed = f.Bind("Camera", "FollowSpeed", 8f, "How quickly the camera tracks the group.");
        }
    }

    [BepInPlugin(Guid, "Hollow Knight Couch Co-op", "0.9.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal const string Guid = "com.powback.hkcouchcoop";

        /// <summary>Game build this was written against and verified on.</summary>
        private const string TargetGameVersion = "1.5.12620";

        internal static Plugin Instance;

        /// <summary>The version in the BepInPlugin attribute, read back at runtime
        /// rather than duplicated in a constant — a second copy of a version number
        /// is a second thing to forget to bump.</summary>
        internal static string Version =>
            Instance != null ? Instance.Info.Metadata.Version.ToString() : "unknown";
        internal static ManualLogSource Log;
        internal static Config Cfg;

        private Harmony _harmony;
        private bool _enabled;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            Cfg = new Config(base.Config);

            if (!Reflect.Verify(out var missing))
            {
                Log.LogError(
                    $"Could not find '{missing}' in this build of Hollow Knight. " +
                    "The mod will not load. This usually means the game updated — " +
                    "check for a newer release of HKCouchCoop.");
                return;
            }

            if (!Harness.VersionGate(Application.version, TargetGameVersion,
                    Cfg.IgnoreVersionCheck.Value, m => Log.LogError(m), m => Log.LogWarning(m)))
                return;

            _harmony = new Harmony(Guid);
            Harness.PatchAllIsolated(_harmony, typeof(Plugin).Assembly, m => Log.LogError(m));

            CoopManager.Init();
            DebugState.Init();
            _enabled = true;

            Log.LogInfo(Cfg.JoinWithStart.Value
                ? $"Ready. Press Start on a spare pad to join (hold Start to leave). Keyboard: {Cfg.JoinKey.Value} joins, {Cfg.LeaveKey.Value} removes."
                : $"Ready. Press {Cfg.JoinKey.Value} to add a player, {Cfg.LeaveKey.Value} to remove one.");
        }

        private readonly ThrottledLog _updateErrors =
            new ThrottledLog(10, m => Log.LogError(m));

        private void Update()
        {
            if (!_enabled) return;
            try { UpdateCore(); }
            catch (System.Exception e)
            {
                _updateErrors.Log(Time.unscaledTimeAsDouble, $"Update loop error (throttled): {e}");
            }
        }

        private readonly ThrottledLog _driftErrors =
            new ThrottledLog(30, m => Log.LogError(m));

        /// <summary>
        /// Frame-boundary check on both identity mechanisms.
        ///
        /// Every scope in this mod — the singleton masquerade and the health
        /// pool swap alike — opens in a Harmony prefix and closes in the
        /// matching finalizer, so by the time a MonoBehaviour Update runs,
        /// none may still be open. One that is, leaked.
        ///
        /// Both leaks are silent and permanent rather than wrong for a frame,
        /// and both are worth repairing rather than only reporting: the
        /// singleton feeds the death path, scene transitions and the game's
        /// global input handler, and a leaked pool swap leaves the shared
        /// PlayerData holding a clone's health and soul — which the next
        /// autosave writes to the save file.
        ///
        /// These are detectors, not mechanisms: they should never fire. If one
        /// does, the fix belongs at the leaking scope.
        /// </summary>
        private void AssertScopesClosed()
        {
            if (!CoopManager.Active) return;

            if (HealthPool.AnyOpen)
            {
                var n = HealthPool.ForceEndAll();
                _driftErrors.Log(Time.unscaledTimeAsDouble,
                    $"Pool swap leak (throttled): {n} health/soul scope(s) were still open at a "
                    + "frame boundary, leaving shared PlayerData holding a clone's pools. "
                    + "Closed them; a swap finalizer is not running.");
            }

            var p1 = CoopManager.PlayerOne;
            if (p1 == null) return;

            // Backing field, not the property: the property resolves a null
            // singleton with FindObjectOfType, which would mask the very
            // drift being looked for.
            var stray = Reflect.HeroInstance;
            if (ReferenceEquals(stray, p1)) return;

            // Repair before reporting. Naming the stray needs its gameObject,
            // and a destroyed one throws on that — which would leave the
            // singleton broken for the sake of a log line. `== null` here is
            // Unity's operator, so it covers destroyed as well as unset.
            Reflect.HeroInstance = p1;
            _driftErrors.Log(Time.unscaledTimeAsDouble,
                "Singleton drift (throttled): HeroController._instance was left pointing at "
                + $"'{(stray == null ? "a destroyed or unset hero" : stray.gameObject.name)}' "
                + "outside any ownership scope. Player one restored; "
                + "a masquerade finalizer is not running.");
        }

        private void UpdateCore()
        {
            AssertScopesClosed();

            if (Input.GetKeyDown(Cfg.JoinKey.Value))
            {
                var before = CoopManager.PlayerCount;
                CoopManager.Join();
                var after = CoopManager.PlayerCount;

                NativeHud.Notify(after > before
                    ? $"Player {after} joined  —  {after} players"
                    : (CoopManager.LastJoinRejection ?? "Could not join"));
                if (after > before) CoopCamera.Reset();
            }

            if (Input.GetKeyDown(Cfg.LeaveKey.Value) && CoopManager.Active)
            {
                CoopManager.LeaveLast();
                NativeHud.Notify($"{CoopManager.PlayerCount} players");
                CoopCamera.Reset();
            }

            InputAssign.Tick();
            StartJoin.Tick();
            DebugState.Tick();
            CoopManager.Tick();
            FriendlyFire.SampleInput();
            FriendlyFire.Tick();
            NativeHud.Tick();
            CoopHealthHud.Tick();
        }

        private void OnDestroy()
        {
            if (!_enabled) return;
            DebugState.Shutdown();
            CoopManager.Shutdown();
            _harmony?.UnpatchSelf();
        }
    }
}
