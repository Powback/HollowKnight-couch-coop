using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using InControl;
using UnityEngine;

namespace HKCouchCoop
{
    /// <summary>
    /// Press Start on a spare pad to join; hold Start about a second to leave.
    ///
    /// Start is the pause button, which is what makes this delicate. The first
    /// attempt pre-muted every unclaimed pad so Start would be "free" — and
    /// muted the wrong pad on the Steam Deck, killing input entirely. This
    /// version never touches anyone's input configuration. Instead it sits on
    /// <see cref="GameManager.PauseGameToggle"/> — the single method every pause
    /// goes through — asks which physical device pressed Start, and if that
    /// device is a spare pad, swallows the pause and joins the pad instead.
    ///
    /// Pads that have joined are already excluded from player one's action set,
    /// so their Start cannot reach PauseGameToggle at all; holding it to leave
    /// is handled by polling the device directly in <see cref="Tick"/>.
    /// </summary>
    internal static class StartJoin
    {
        private static float LeaveHold => Plugin.Cfg.LeaveHoldSeconds.Value;

        private static readonly Dictionary<InputDevice, float> HeldSince =
            new Dictionary<InputDevice, float>();

        /// <summary>Device-level "Start-like" controls across pad styles.</summary>
        private static readonly InputControlType[] StartControls =
        {
            InputControlType.Command,   // unified Start/Options/Menu/Plus
            InputControlType.Start,
            InputControlType.Options,
            InputControlType.Menu,
            InputControlType.Plus,
        };

        internal static bool StartPressed(InputDevice d) =>
            StartControls.Any(c =>
            {
                var control = d.GetControl(c);
                return control != null && control.WasPressed;
            });

        internal static bool StartHeld(InputDevice d) =>
            StartControls.Any(c =>
            {
                var control = d.GetControl(c);
                return control != null && control.IsPressed;
            });

        /// <summary>
        /// The pad reserved for player one, whose Start always pauses. Index into
        /// the attached-device list; on a Steam Deck the built-in controller
        /// enumerates first, so the default of 0 is right there. -1 reserves
        /// nothing (player one on keyboard — every pad is a joiner).
        /// </summary>
        private static InputDevice ReservedPad() => InputAssign.P1Device();

        /// <summary>
        /// A Start press that should become a join rather than a pause, or null.
        /// </summary>
        internal static InputDevice JoinCandidate()
        {
            if (!Plugin.Cfg.JoinWithStart.Value) return null;
            if (GameManager.instance == null
                || GameManager.instance.gameState != GlobalEnums.GameState.PLAYING) return null;

            var reserved = ReservedPad();

            return InputManager.Devices?.FirstOrDefault(d =>
                d != null && d.IsAttached
                && d != reserved                                   // player one's own pad pauses
                && InputAssign.RoleOf(d) != PadRole.None
                && InputAssign.RoleOf(d) != PadRole.P1
                && !PadInput.Claimed.Contains(d)
                && StartPressed(d));
        }

        /// <summary>Hold-to-leave for joined pads. Driven from Plugin.Update.</summary>
        internal static void Tick()
        {
            if (!Plugin.Cfg.JoinWithStart.Value || !CoopManager.Active) return;

            foreach (var device in PadInput.Claimed.ToList())
            {
                if (device == null || !device.IsAttached) { HeldSince.Remove(device); continue; }

                if (!StartHeld(device)) { HeldSince.Remove(device); continue; }

                if (!HeldSince.ContainsKey(device))
                {
                    HeldSince[device] = Time.unscaledTime;
                }
                else if (Time.unscaledTime - HeldSince[device] >= LeaveHold)
                {
                    HeldSince.Remove(device);
                    CoopManager.LeaveByDevice(device);
                    NativeHud.Notify($"{CoopManager.PlayerCount} players");
                    CoopCamera.Reset();
                }
            }
        }

        private static IEnumerator Nothing() { yield break; }

        [HarmonyPatch(typeof(GameManager), nameof(GameManager.PauseGameToggle))]
        internal static class PauseIntercept
        {
            private static bool Prefix(ref IEnumerator __result)
            {
                var pad = Guard.Run(() => JoinCandidate(), "JoinCandidate");
                if (pad == null) return true;    // a real pause — run vanilla

                __result = Nothing();            // swallow the pause…

                var before = CoopManager.PlayerCount;
                CoopManager.Join(pad);           // …and make it a join
                if (CoopManager.PlayerCount > before)
                {
                    NativeHud.Notify(
                        $"Player {CoopManager.PlayerCount} joined ({pad.Name}) — hold Start to leave", 4f);
                    CoopCamera.Reset();
                }
                return false;
            }
        }
    }
}
