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
        internal readonly ConfigEntry<bool> IndependentHealth;
        internal readonly ConfigEntry<bool> ShadeRevive;
        internal readonly ConfigEntry<bool> JoinWithStart;
        internal readonly ConfigEntry<int> PlayerOnePadIndex;
        internal readonly ConfigEntry<KeyCode> JoinKey;
        internal readonly ConfigEntry<KeyCode> LeaveKey;
        internal readonly ConfigEntry<int> MaxPlayers;
        internal readonly ConfigEntry<float> LeashDistance;
        internal readonly ConfigEntry<bool> IgnoreVersionCheck;

        internal readonly ConfigEntry<int> ReviveHealthPercent;
        internal readonly ConfigEntry<float> LeaveHoldSeconds;
        internal readonly ConfigEntry<bool> FreezeExtrasInCutscenes;
        internal readonly ConfigEntry<bool> PlayerTints;
        internal readonly ConfigEntry<bool> CameraZoom;
        internal readonly ConfigEntry<float> MaxZoomFactor;
        internal readonly ConfigEntry<float> ZoomMargin;
        internal readonly ConfigEntry<float> ZoomSpeed;
        internal readonly ConfigEntry<float> FollowSpeed;

        internal Config(ConfigFile f)
        {
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
            PlayerOnePadIndex = f.Bind("General", "PlayerOnePadIndex", 0,
                "Which attached pad belongs to player one and always pauses normally. 0 is right " +
                "for a Steam Deck (built-in controls enumerate first). -1 means player one is on " +
                "keyboard and every pad may Start-join.");
            JoinKey = f.Bind("General", "JoinKey", KeyCode.F6,
                "Adds the next player, using the first unclaimed gamepad.");
            LeaveKey = f.Bind("General", "LeaveKey", KeyCode.F7,
                "Removes the most recently joined player.");
            MaxPlayers = f.Bind("General", "MaxPlayers", 4,
                "Total players including player one (2-4). Each extra player needs its own gamepad.");
            LeashDistance = f.Bind("General", "LeashDistance", 30f,
                "Pull an extra player back to player one past this distance in world units. 0 disables.");
            IgnoreVersionCheck = f.Bind("General", "IgnoreVersionCheck", false,
                "Load even if the game version does not match the one this build targets.");

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

    [BepInPlugin(Guid, "Hollow Knight Couch Co-op", "0.6.2")]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal const string Guid = "com.powback.hkcouchcoop";

        /// <summary>Game build this was written against and verified on.</summary>
        private const string TargetGameVersion = "1.5.12620";

        internal static Plugin Instance;
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

        private void UpdateCore()
        {
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

            StartJoin.Tick();
            CoopManager.Tick();
            NativeHud.Tick();
            CoopHealthHud.Tick();
        }

        private void OnDestroy()
        {
            if (!_enabled) return;
            CoopManager.Shutdown();
            _harmony?.UnpatchSelf();
        }
    }
}
