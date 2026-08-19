using System.Collections.Generic;
using System.Linq;
using InControl;

namespace HKCouchCoop
{
    internal enum PadRole { Auto = 0, None = 1, P1 = 2, P2 = 3, P3 = 4, P4 = 5 }

    /// <summary>
    /// Explicit input assignment: every detected pad carries a role — Auto
    /// (join freely, old behavior), None (dead to the mod AND to player one:
    /// the deliberate parking spot for Steam Input's ghost twins), P1 (this
    /// pad is player one's), or a fixed player slot P2–P4.
    ///
    /// Identity persists by device name + ordinal among same-named devices,
    /// serialized into one config string, so assignments survive restarts.
    /// The keyboard is structurally player one's: InControl keyboard bindings
    /// cannot be scoped per action set.
    /// </summary>
    internal static class InputAssign
    {
        private static Dictionary<string, PadRole> _map;

        internal static string IdOf(InputDevice device)
        {
            if (device == null) return null;
            var ordinal = 0;
            foreach (var d in InputManager.Devices)
            {
                if (d == null || !d.IsAttached) continue;
                if (ReferenceEquals(d, device)) break;
                if (d.Name == device.Name) ordinal++;
            }
            return $"{device.Name}#{ordinal}";
        }

        internal static InputDevice Resolve(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var cut = id.LastIndexOf('#');
            if (cut < 0) return null;
            var name = id.Substring(0, cut);
            if (!int.TryParse(id.Substring(cut + 1), out var ordinal)) return null;

            var seen = 0;
            foreach (var d in InputManager.Devices)
            {
                if (d == null || !d.IsAttached || d.Name != name) continue;
                if (seen == ordinal) return d;
                seen++;
            }
            return null;
        }

        private static Dictionary<string, PadRole> Map()
        {
            if (_map != null) return _map;
            _map = new Dictionary<string, PadRole>();
            foreach (var pair in (Plugin.Cfg.PadAssignments.Value ?? "").Split(';'))
            {
                var cut = pair.LastIndexOf(':');
                if (cut <= 0) continue;
                if (System.Enum.TryParse(pair.Substring(cut + 1), out PadRole role))
                    _map[pair.Substring(0, cut)] = role;
            }
            return _map;
        }

        private static void Save()
        {
            Plugin.Cfg.PadAssignments.Value =
                string.Join(";", Map().Where(kv => kv.Value != PadRole.Auto)
                    .Select(kv => $"{kv.Key}:{kv.Value}"));
        }

        internal static PadRole RoleOf(InputDevice device)
        {
            var id = IdOf(device);
            return id != null && Map().TryGetValue(id, out var role) ? role : PadRole.Auto;
        }

        internal static void SetRole(string id, PadRole role)
        {
            Map()[id] = role;
            Save();
            CoopManager.OnAssignmentsChanged();
        }

        internal static PadRole RoleOfId(string id)
            => Map().TryGetValue(id, out var role) ? role : PadRole.Auto;

        /// <summary>The pad assigned to player one, if any (else keyboard-P1 mode).</summary>
        internal static InputDevice P1Device()
        {
            foreach (var kv in Map())
                if (kv.Value == PadRole.P1) return Resolve(kv.Key);
            return null;
        }

        /// <summary>Attached pads, stable order, for the menu.</summary>
        internal static List<string> AttachedIds() =>
            InputManager.Devices.Where(d => d != null && d.IsAttached)
                .Select(IdOf).Where(id => id != null).ToList();

        /// <summary>Slot number (2-4) a role maps to, or 0.</summary>
        internal static int SlotOf(PadRole role) => role switch
        {
            PadRole.P2 => 2, PadRole.P3 => 3, PadRole.P4 => 4, _ => 0,
        };
    }
}
