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
                return;

            var created = new List<MenuOptionHorizontal>();
            foreach (var row in BuildRows())
            {
                var clone = CloneRow(template, row);
                if (clone != null) created.Add(clone);
            }

            if (created.Count > 0) Register(screen, created);
            Plugin.Log.LogInfo($"Added {created.Count} multiplayer settings to the options menu.");
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
