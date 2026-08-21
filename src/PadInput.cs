using System.Collections.Generic;
using System.Linq;
using InControl;
using UnityEngine;

namespace HKCouchCoop
{
    /// <summary>
    /// An independent input source for one extra player.
    ///
    /// Two facts make this work without touching vanilla movement code:
    ///  1. HeroController reads input via an *instance* field (`inputHandler`),
    ///     never a global — so extra Knights with their own handlers is enough.
    ///  2. InControl's PlayerActionSet has a settable `Device`, so each action
    ///     set can be pinned to one specific gamepad.
    ///
    /// The InputHandler component is created on an *inactive* GameObject so
    /// Unity never runs its Awake/Start. We only need it as a carrier for
    /// `inputActions`; letting it initialise would double-subscribe it to
    /// InputManager's device events and fight the real handler.
    /// </summary>
    internal sealed class PadInput
    {
        internal InputHandler Handler { get; private set; }
        internal HeroActions Actions { get; private set; }
        internal InputDevice Device { get; private set; }

        private GameObject _carrier;

        /// <summary>Gamepads already handed to a player, so each join gets a fresh one.</summary>
        internal static readonly List<InputDevice> Claimed = new List<InputDevice>();

        /// <summary>A pad nobody is using yet, or null if every pad is taken.</summary>
        internal static InputDevice NextFreeDevice()
        {
            var p1Device = GameManager.instance != null && GameManager.instance.inputHandler != null
                ? GameManager.instance.inputHandler.gameController
                : null;

            return InputManager.Devices?
                .FirstOrDefault(d => d != null && d.IsAttached
                                     && !Claimed.Contains(d)
                                     && d != p1Device)
                   // Player one may be on keyboard, in which case the only pad
                   // present is still free to claim.
                   ?? InputManager.Devices?
                       .FirstOrDefault(d => d != null && d.IsAttached && !Claimed.Contains(d));
        }

        internal static PadInput Create(InputDevice device)
        {
            if (device == null) return null;

            var actions = BuildActions(device);

            var carrier = new GameObject($"HKCouchCoop_Input_{device.Name}");
            carrier.SetActive(false);            // keeps InputHandler.Awake from running
            Object.DontDestroyOnLoad(carrier);

            var handler = carrier.AddComponent<InputHandler>();
            handler.inputActions = actions;

            Claimed.Add(device);

            return new PadInput
            {
                Handler = handler,
                Actions = actions,
                Device = device,
                _carrier = carrier,
            };
        }

        /// <summary>
        /// Controller-only bindings. We deliberately add no keyboard bindings:
        /// those are not device-scoped in InControl, so player one's keyboard
        /// would drive every extra Knight as well.
        /// </summary>
        private static HeroActions BuildActions(InputDevice device)
        {
            // The PlayerActionSet constructor registers itself with InputManager,
            // so this set is polled every frame from here on.
            var a = new HeroActions();

            a.left.AddDefaultBinding(InputControlType.LeftStickLeft);
            a.left.AddDefaultBinding(InputControlType.DPadLeft);
            a.right.AddDefaultBinding(InputControlType.LeftStickRight);
            a.right.AddDefaultBinding(InputControlType.DPadRight);
            a.up.AddDefaultBinding(InputControlType.LeftStickUp);
            a.up.AddDefaultBinding(InputControlType.DPadUp);
            a.down.AddDefaultBinding(InputControlType.LeftStickDown);
            a.down.AddDefaultBinding(InputControlType.DPadDown);

            a.rs_up.AddDefaultBinding(InputControlType.RightStickUp);
            a.rs_down.AddDefaultBinding(InputControlType.RightStickDown);
            a.rs_left.AddDefaultBinding(InputControlType.RightStickLeft);
            a.rs_right.AddDefaultBinding(InputControlType.RightStickRight);

            // The game's own configured controller layout, so a joining pad
            // behaves exactly like the settings screen says. Falls back to the
            // game's defaults when unavailable.
            var m = CurrentMapping();
            // Menu accept/cancel, matching the game's own convention — without
            // these an extra driving their own dialogue has no submit/cancel.
            a.menuSubmit.AddDefaultBinding(InputControlType.Action1);
            a.menuCancel.AddDefaultBinding(InputControlType.Action2);

            a.jump.AddDefaultBinding(m?.jump ?? InputControlType.Action1);
            a.attack.AddDefaultBinding(m?.attack ?? InputControlType.Action3);
            a.cast.AddDefaultBinding(m?.cast ?? InputControlType.Action2);
            a.focus.AddDefaultBinding(m?.cast ?? InputControlType.Action2);   // focus = cast held
            a.dash.AddDefaultBinding(m?.dash ?? InputControlType.RightTrigger);
            a.superDash.AddDefaultBinding(m?.superDash ?? InputControlType.LeftTrigger);
            a.dreamNail.AddDefaultBinding(m?.dreamNail ?? InputControlType.Action4);
            a.quickMap.AddDefaultBinding(m?.quickMap ?? InputControlType.LeftBumper);
            a.quickCast.AddDefaultBinding(m?.quickCast ?? InputControlType.RightBumper);

            // Pin every binding to this pad only, or all pads would drive all Knights.
            a.Device = device;
            a.IncludeDevices.Clear();
            a.IncludeDevices.Add(device);

            return a;
        }

        /// <summary>The controller mapping the game itself is configured with.</summary>
        private static ControllerMapping CurrentMapping()
        {
            try
            {
                var handler = GameManager.instance != null
                    ? GameManager.instance.inputHandler : null;
                if (handler == null) return null;
                var gs = HarmonyLib.AccessTools.Field(typeof(InputHandler), "gs")?.GetValue(handler);
                return gs != null
                    ? HarmonyLib.AccessTools.Field(gs.GetType(), "controllerMapping")?.GetValue(gs) as ControllerMapping
                    : null;
            }
            catch { return null; }
        }

        internal void Destroy()
        {
            if (Device != null) Claimed.Remove(Device);
            Actions?.Destroy();          // detaches from InputManager
            if (_carrier != null) Object.Destroy(_carrier);
            Actions = null;
            Handler = null;
            Device = null;
            _carrier = null;
        }
    }
}
