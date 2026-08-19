using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace HKCouchCoop
{
    /// <summary>
    /// Multiplayer settings inside Hollow Knight's real Game Options screen.
    ///
    /// Each row is cloned from an existing <see cref="MenuOptionHorizontal"/> on
    /// that screen, inheriting the game's fonts, arrow graphics, hover/select
    /// sounds, layout and controller navigation — none of which is worth
    /// reimplementing, and all of which is the difference between "native" and
    /// "bolted on". Rows apply on scroll and write straight to the config.
    /// </summary>
    internal static class NativeMenu
    {
        private const string RowPrefix = "HKCouchCoop_";

        private sealed class Row
        {
            internal string Label;
            internal string[] Options;
            internal Func<int> Get;
            internal Action<int> Set;
        }

        private static readonly Dictionary<MenuOptionHorizontal, Row> Ours =
            new Dictionary<MenuOptionHorizontal, Row>();

        // Device-assignment rows: a fixed pool created once (so controller
        // navigation registers once), relabeled to the live device list on
        // every menu open. Row semantics: move each input between roles —
        // Auto | None | P1 | P2 | P3 | P4. None is the parking spot for Steam
        // Input ghost twins. The keyboard is structurally player one's.
        private const int PadRowPool = 6;
        private static readonly List<MenuOptionHorizontal> PadRows =
            new List<MenuOptionHorizontal>();
        private static readonly string[] RoleNames = { "Auto", "None", "P1", "P2", "P3", "P4" };

        private static List<Row> BuildRows()
        {
            var c = Plugin.Cfg;
            return new List<Row>
            {
                new Row
                {
                    Label = "Multiplayer Health",
                    Options = new[] { "Shared Pool", "Per Player" },
                    Get = () => c.IndependentHealth.Value ? 1 : 0,
                    Set = i => c.IndependentHealth.Value = i == 1,
                },
                new Row
                {
                    Label = "Fallen Players",
                    Options = new[] { "Rejoin Only", "Shade Revival" },
                    Get = () => c.ShadeRevive.Value ? 1 : 0,
                    Set = i => c.ShadeRevive.Value = i == 1,
                },
                new Row
                {
                    Label = "Join With Start",
                    Options = new[] { "Off", "On" },
                    Get = () => c.JoinWithStart.Value ? 1 : 0,
                    Set = i => c.JoinWithStart.Value = i == 1,
                },
                new Row
                {
                    Label = "Max Players",
                    Options = new[] { "2", "3", "4" },
                    Get = () => Mathf.Clamp(c.MaxPlayers.Value, 2, 4) - 2,
                    Set = i => c.MaxPlayers.Value = i + 2,
                },
                new Row
                {
                    Label = "Leash",
                    Options = new[] { "Off", "Screen", "Near", "Far" },
                    Get = () =>
                    {
                        var v = c.LeashDistance.Value;
                        return v == 0 ? 0 : v < 0 ? 1 : v < 35 ? 2 : 3;
                    },
                    Set = i => c.LeashDistance.Value = new[] { 0f, -1f, 20f, 50f }[Mathf.Clamp(i, 0, 3)],
                },
                new Row
                {
                    Label = "Revive Masks",
                    Options = new[] { "Quarter", "Half", "Most", "Full" },
                    Get = () =>
                    {
                        var v = c.ReviveHealthPercent.Value;
                        return v <= 30 ? 0 : v <= 55 ? 1 : v <= 80 ? 2 : 3;
                    },
                    Set = i => c.ReviveHealthPercent.Value = new[] { 25, 50, 75, 100 }[Mathf.Clamp(i, 0, 3)],
                },
                new Row
                {
                    Label = "Hold To Leave",
                    Options = new[] { "Quick", "Normal", "Long" },
                    Get = () =>
                    {
                        var v = c.LeaveHoldSeconds.Value;
                        return v < 0.9f ? 0 : v < 1.6f ? 1 : 2;
                    },
                    Set = i => c.LeaveHoldSeconds.Value = new[] { 0.6f, 1.2f, 2.2f }[Mathf.Clamp(i, 0, 2)],
                },
                new Row
                {
                    Label = "Cutscene Freeze",
                    Options = new[] { "Off", "On" },
                    Get = () => c.FreezeExtrasInCutscenes.Value ? 1 : 0,
                    Set = i => c.FreezeExtrasInCutscenes.Value = i == 1,
                },
                new Row
                {
                    Label = "Zoom Range",
                    Options = new[] { "Tight", "Normal", "Wide", "Stage" },
                    Get = () =>
                    {
                        var v = c.MaxZoomFactor.Value;
                        return v < 1.45f ? 0 : v < 1.9f ? 1 : v < 3f ? 2 : 3;
                    },
                    Set = i => c.MaxZoomFactor.Value = new[] { 1.3f, 1.6f, 2.4f, 99f }[Mathf.Clamp(i, 0, 3)],
                },
                new Row
                {
                    Label = "Player Colors",
                    Options = new[] { "Off", "On" },
                    Get = () => c.PlayerTints.Value ? 1 : 0,
                    Set = i => c.PlayerTints.Value = i == 1,
                },
                new Row
                {
                    Label = "Group Camera Zoom",
                    Options = new[] { "Off", "On" },
                    Get = () => c.CameraZoom.Value ? 1 : 0,
                    Set = i => c.CameraZoom.Value = i == 1,
                },
            };
        }

        internal static void Inject(MenuScreen screen)
        {
            if (screen == null) return;

            foreach (var dead in Ours.Keys.Where(k => k == null).ToList())
                Ours.Remove(dead);

            var template = screen.GetComponentsInChildren<MenuOptionHorizontal>(includeInactive: true)
                .FirstOrDefault(o => !o.name.StartsWith(RowPrefix));
            if (template == null)
            {
                Plugin.Log.LogWarning("No MenuOptionHorizontal to clone; co-op settings stay config-file-only.");
                return;
            }

            // ConfigureNavigation can run repeatedly; ask the screen itself
            // whether our rows already exist rather than trusting a flag.
            if (template.transform.parent.Cast<Transform>().Any(t => t.name.StartsWith(RowPrefix)))
            {
                RefreshPadRows();   // device list may have changed since last open
                return;
            }

            // Row spacing measured from the screen's own rows: cloned rows keep
            // the template's local position, so without a layout group they
            // would all stack on one spot.
            var siblings = template.transform.parent
                .GetComponentsInChildren<MenuOptionHorizontal>(includeInactive: true)
                .Where(o => !o.name.StartsWith(RowPrefix))
                .Select(o => o.transform.localPosition.y)
                .Distinct().OrderByDescending(y => y).ToList();
            float spacing;
            if (siblings.Count >= 2)
            {
                spacing = Mathf.Abs(siblings[0] - siblings[1]);
            }
            else
            {
                var rt = template.GetComponent<RectTransform>();
                spacing = rt != null ? rt.rect.height * 1.15f : 60f;
            }
            var baseY = siblings.Count > 0 ? siblings.Min() : template.transform.localPosition.y;

            // Our rows sit tighter than vanilla's so twelve fit beneath the
            // screen's own entries without scrolling.
            spacing *= 0.85f;

            var created = new List<MenuOptionHorizontal>();
            var index = 0;
            foreach (var row in BuildRows())
            {
                var clone = CloneRow(template, row);
                if (clone == null) continue;
                var lp = template.transform.localPosition;
                clone.transform.localPosition = new Vector3(lp.x, baseY - spacing * (++index), lp.z);
                created.Add(clone);
            }

            // Device-assignment pool, below the static rows.
            for (var k = 0; k < PadRowPool; k++)
            {
                var padRow = CloneRow(template, new Row
                {
                    Label = "Input",
                    Options = RoleNames,
                    Get = () => 0,
                    Set = _ => { },
                });
                if (padRow == null) continue;
                var lp = template.transform.localPosition;
                padRow.transform.localPosition = new Vector3(lp.x, baseY - spacing * (++index), lp.z);
                padRow.name = RowPrefix + "Pad_" + k;
                PadRows.Add(padRow);
                created.Add(padRow);
            }
            RefreshPadRows();

            if (created.Count > 0) Register(screen, created);
            Plugin.Log.LogInfo($"Added {created.Count} multiplayer settings to the options menu.");
        }

        private static void RefreshPadRows()
        {
            PadRows.RemoveAll(r => r == null);
            if (PadRows.Count == 0) return;

            var ids = InputAssign.AttachedIds();

            for (var k = 0; k < PadRows.Count; k++)
            {
                var option = PadRows[k];

                if (k == 0)
                {
                    // Keyboard: fixed to player one.
                    SetLabel(option.gameObject, option, "K · Keyboard");
                    var fixedRow = new Row
                    {
                        Label = "K · Keyboard",
                        Options = new[] { "P1" },
                        Get = () => 0,
                        Set = _ => { },
                    };
                    Ours[option] = fixedRow;
                    option.optionList = fixedRow.Options;
                    option.SetOptionTo(0);
                    option.gameObject.SetActive(true);
                    continue;
                }

                var deviceIndex = k - 1;
                if (deviceIndex >= ids.Count)
                {
                    option.gameObject.SetActive(false);
                    continue;
                }

                var id = ids[deviceIndex];
                var shortName = id.Length > 22 ? id.Substring(0, 22) : id;
                SetLabel(option.gameObject, option, $"G{deviceIndex + 1} · {shortName}");
                var row = new Row
                {
                    Label = id,
                    Options = RoleNames,
                    Get = () => (int)InputAssign.RoleOfId(id),
                    Set = i => InputAssign.SetRole(id, (PadRole)Mathf.Clamp(i, 0, 5)),
                };
                Ours[option] = row;
                option.optionList = row.Options;
                option.SetOptionTo(Mathf.Clamp(row.Get(), 0, RoleNames.Length - 1));
                option.gameObject.SetActive(true);
            }
        }

        private static MenuOptionHorizontal CloneRow(MenuOptionHorizontal template, Row row)
        {
            try
            {
                var go = UnityEngine.Object.Instantiate(
                    template.gameObject, template.transform.parent, worldPositionStays: false);
                go.name = RowPrefix + row.Label.Replace(" ", "");
                go.SetActive(true);

                var option = go.GetComponent<MenuOptionHorizontal>();

                // Never let a cloned row write to the game's own settings.
                option.menuSetting = null;
                option.refreshListOnChange = null;
                option.localizeText = false;
                option.applySettingOn = MenuOptionHorizontal.ApplyOnType.Scroll;

                option.optionList = row.Options;
                option.selectedOptionIndex = Mathf.Clamp(row.Get(), 0, row.Options.Length - 1);

                SetLabel(go, option, row.Label);
                Ours[option] = row;

                option.SetOptionTo(option.selectedOptionIndex);
                return option;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Could not build menu row '{row.Label}': {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// The row's name label is a sibling Text of the value text. Strip
        /// localisation everywhere — on the value text too, or a failed lookup
        /// overwrites our strings.
        /// </summary>
        private static void SetLabel(GameObject go, MenuOptionHorizontal option, string label)
        {
            foreach (var text in go.GetComponentsInChildren<Text>(includeInactive: true))
            {
                foreach (var comp in text.GetComponents<MonoBehaviour>())
                {
                    if (comp != null && comp.GetType().Name.IndexOf("Localiz",
                            StringComparison.OrdinalIgnoreCase) >= 0)
                        UnityEngine.Object.Destroy(comp);
                }
                if (text != option.optionText) text.text = label;
            }
        }

        /// <summary>
        /// Adds the rows to the screen's MenuButtonList so controller navigation
        /// reaches them. The entry list is a private serialized array of a
        /// private nested type, so this goes through reflection.
        /// </summary>
        private static void Register(MenuScreen screen, List<MenuOptionHorizontal> rows)
        {
            var list = screen.GetComponentInChildren<MenuButtonList>(includeInactive: true);
            if (list == null) { Plugin.Log.LogWarning("No MenuButtonList; rows may not be navigable."); return; }

            var entriesField = AccessTools.Field(typeof(MenuButtonList), "entries");
            var entryType = typeof(MenuButtonList).GetNestedType("Entry", BindingFlags.NonPublic);
            if (entriesField == null || entryType == null)
            {
                Plugin.Log.LogWarning("MenuButtonList layout changed; rows may not be navigable.");
                return;
            }

            var selectableField = AccessTools.Field(entryType, "selectable");
            var existing = (Array)entriesField.GetValue(list);
            var oldLength = existing?.Length ?? 0;

            var merged = Array.CreateInstance(entryType, oldLength + rows.Count);
            if (existing != null) Array.Copy(existing, merged, oldLength);

            for (var i = 0; i < rows.Count; i++)
            {
                var entry = Activator.CreateInstance(entryType);
                selectableField?.SetValue(entry, rows[i]);
                merged.SetValue(entry, oldLength + i);
            }

            entriesField.SetValue(list, merged);
            list.SetupActive();
        }

        internal static void OnChanged(MenuOptionHorizontal option)
        {
            if (!Ours.TryGetValue(option, out var row)) return;
            try { row.Set(option.selectedOptionIndex); }
            catch (Exception e) { Plugin.Log.LogError($"Could not apply '{row.Label}': {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(GameMenuOptions), "ConfigureNavigation")]
    internal static class GameOptionsInjector
    {
        private static void Postfix(GameMenuOptions __instance)
            => NativeMenu.Inject(__instance.gameOptionsMenuScreen);
    }

    /// <summary>UpdateSetting is the single point a row's change flows through.</summary>
    [HarmonyPatch]
    internal static class MenuWriteBack
    {
        private static MethodBase TargetMethod()
            => AccessTools.Method(typeof(MenuOptionHorizontal), "UpdateSetting");

        private static void Postfix(MenuOptionHorizontal __instance)
            => NativeMenu.OnChanged(__instance);
    }
}
