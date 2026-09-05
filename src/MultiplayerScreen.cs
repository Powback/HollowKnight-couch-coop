using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace HKCouchCoop
{
    /// <summary>
    /// A real MULTIPLAYER entry in the Options list, opening its own native
    /// screen — a clone of the Game Options screen with our rows in place of
    /// vanilla's. The clone keeps the original's title layout, back button
    /// (whose serialized wiring returns to Options — with a HideCurrentMenu
    /// patch making that hide OUR screen), fleur animations and controller
    /// navigation. If any structural lookup misses, everything falls back to
    /// the old but functional inject-into-Game-Options behavior.
    /// </summary>
    internal static class MultiplayerScreen
    {
        internal static MenuScreen Screen;
        internal static bool Visible;
        private static bool _failed;
        private static GameObject _entryButton;

        internal static bool Ready => Screen != null;

        /// <summary>
        /// The Options list as it ended up, top to bottom. Menu placement is
        /// the one part of this mod invisible from the state channel, and
        /// "it's in a weird spot" is hard to act on without it.
        /// </summary>
        internal static string[] OptionsOrder = new string[0];

        internal static void Ensure()
        {
            if (Screen != null || _failed) return;
            Guard.Run(() =>
            {
                if (!Build())
                {
                    _failed = true;
                    Plugin.Log.LogWarning(
                        "Multiplayer screen could not be built — settings stay in Game Options.");
                }
            }, "Multiplayer screen build");
        }

        private static bool Build()
        {
            var uim = UIManager.instance;
            if (uim == null || uim.gameOptionsMenuScreen == null || uim.optionsMenuScreen == null)
                return false;

            var src = uim.gameOptionsMenuScreen;

            // ── the screen itself ─────────────────────────────────────────
            var go = UnityEngine.Object.Instantiate(src.gameObject, src.transform.parent);
            go.name = "HKCC_MultiplayerScreen";
            go.SetActive(false);
            Screen = go.GetComponent<MenuScreen>();
            if (Screen == null) { UnityEngine.Object.Destroy(go); return false; }

            foreach (var gmo in go.GetComponentsInChildren<GameMenuOptions>(true))
                UnityEngine.Object.DestroyImmediate(gmo);
            NativeMenu.StripLocalizers(go);

            // Title → MULTIPLAYER (the title group's texts, both kinds).
            if (Screen.title != null)
            {
                foreach (var txt in Screen.title.GetComponentsInChildren<Text>(true)) txt.text = "MULTIPLAYER";
                foreach (var tmp in Screen.title.GetComponentsInChildren<TMPro.TMP_Text>(true)) tmp.text = "MULTIPLAYER";
            }

            // ── rows: reuse the clone's vanilla rows in place ─────────────
            var vanillaRows = go.GetComponentsInChildren<MenuOptionHorizontal>(true)
                .OrderByDescending(o => o.transform.localPosition.y).ToList();
            if (vanillaRows.Count == 0) { UnityEngine.Object.Destroy(go); Screen = null; return false; }

            NativeMenu.BuildIntoScreen(Screen, vanillaRows);

            // ── the entry button in the Options list ──────────────────────
            if (!BuildEntryButton(uim)) Plugin.Log.LogWarning(
                "Multiplayer screen exists but no entry button could be placed — reach it via Game Options hotkey row.");

            Plugin.Log.LogInfo("Multiplayer screen built.");
            return true;
        }

        /// <summary>
        /// A vanilla option entry to copy. Each option is a CONTAINER holding
        /// its button at y=0 — the containers are what the screen lays out.
        /// </summary>
        private static MenuButton OptionTemplate(UIManager uim)
        {
            return uim.optionsMenuScreen.GetComponentsInChildren<MenuButton>(true)
                .Where(b => b.transform.parent != null
                            && b.transform.parent.name.EndsWith("Options", StringComparison.Ordinal))
                .OrderByDescending(b => b.transform.parent.localPosition.y)
                .FirstOrDefault();
        }

        /// <summary>
        /// Clone a whole option container and seat it after the last one.
        ///
        /// Cloning just the button into an existing container put MULTIPLAYER
        /// 64 units under Game Options, inside Game Options' own box, on top of
        /// the rest of the list — reported from play as being "in a weird
        /// spot". It is also why a y-sorted list looked right while the screen
        /// did not: every vanilla button reads y=0, so the sort was comparing
        /// coordinates belonging to different parents.
        /// </summary>
        private static GameObject BuildOptionBox(UIManager uim, MenuButton template)
        {
            var boxes = uim.optionsMenuScreen.GetComponentsInChildren<MenuButton>(true)
                .Where(b => b.transform.parent != null
                            && b.transform.parent.name.EndsWith("Options", StringComparison.Ordinal))
                .Select(b => b.transform.parent)
                .Distinct()
                .OrderByDescending(t => t.localPosition.y)
                .ToList();
            if (boxes.Count == 0) return null;

            var firstBox = boxes[0];
            var lastBox = boxes[boxes.Count - 1];

            var box = UnityEngine.Object.Instantiate(firstBox.gameObject, firstBox.parent);
            box.name = "HKCC_MultiplayerOption";

            // Conditions that show or hide a vanilla entry came with the clone
            // and do not apply to ours.
            foreach (var cond in box.GetComponentsInChildren<MenuButtonListCondition>(true))
                UnityEngine.Object.DestroyImmediate(cond);

            // Distinct coordinates mean the screen positions these itself, so
            // ours goes one step below the last. All equal means a layout group
            // owns them and sibling order decides, so leave the position alone.
            var gap = boxes.Count >= 2
                ? firstBox.localPosition.y - boxes[1].localPosition.y : 0f;
            if (Mathf.Abs(firstBox.localPosition.y - lastBox.localPosition.y) > 0.01f
                && Mathf.Abs(gap) > 0.01f)
            {
                box.transform.localPosition = lastBox.localPosition - new Vector3(0f, gap, 0f);
            }
            else
            {
                box.transform.localPosition = lastBox.localPosition;
                box.transform.SetSiblingIndex(lastBox.GetSiblingIndex() + 1);
            }
            return box;
        }

        /// <summary>
        /// Make a cloned vanilla entry into ours: our label, our handler, and
        /// none of the navigation it inherited.
        ///
        /// Unity dispatches ISubmitHandler to EVERY component on the object, so
        /// our handler runs alongside MenuButton's own flash and sound rather
        /// than replacing them.
        /// </summary>
        private static void Relabel(GameObject entry)
        {
            entry.name = "HKCC_MultiplayerButton";
            NativeMenu.StripLocalizers(entry);
            foreach (var txt in entry.GetComponentsInChildren<Text>(true)) txt.text = "MULTIPLAYER";
            foreach (var tmp in entry.GetComponentsInChildren<TMPro.TMP_Text>(true)) tmp.text = "MULTIPLAYER";

            foreach (var b in entry.GetComponents<Button>())
                b.onClick = new Button.ButtonClickedEvent();
            foreach (var et in entry.GetComponents<UnityEngine.EventSystems.EventTrigger>())
                UnityEngine.Object.DestroyImmediate(et);
            entry.AddComponent<MultiplayerEntry>();
        }

        private static bool BuildEntryButton(UIManager uim)
        {
            // HK's option-list entries are MenuButton (a Selectable that handles
            // submit itself), not Unity Buttons with onClick handlers — probing
            // for onClick targets found nothing, which is why no entry appeared.
            var template = OptionTemplate(uim);
            if (template == null) return false;

            var box = BuildOptionBox(uim, template);
            if (box == null) return false;

            _entryButton = box.GetComponentInChildren<MenuButton>(true)?.gameObject;
            if (_entryButton == null) { UnityEngine.Object.Destroy(box); return false; }
            Relabel(_entryButton);

            // Controller order must match what is drawn: after the options,
            // before whatever closes the screen. Appending instead sent the
            // cursor Game Options -> ... -> Apply -> Multiplayer, so the entry
            // was last to reach even when it was not drawn last.
            var navIndex = uim.optionsMenuScreen.GetComponentsInChildren<MenuButton>(true)
                .Count(b => b.transform.parent != null
                            && b.transform.parent.name.EndsWith("Options", StringComparison.Ordinal));

            var selectable = _entryButton.GetComponent<MenuButton>() as Selectable
                             ?? _entryButton.GetComponent<Selectable>();
            if (selectable != null)
                NativeMenu.InsertIntoButtonList(uim.optionsMenuScreen, selectable, navIndex);
            _entryButton.SetActive(true);

            // Say where it landed. Menu placement is the one thing here that
            // cannot be checked from the state channel, and "it looked wrong"
            // is hard to act on without knowing the order the game built.
            var order = uim.optionsMenuScreen
                .GetComponentsInChildren<MenuButton>(true)
                .OrderByDescending(b => b.transform.localPosition.y)
                .Select(b => b.gameObject.name + "@"
                             + (b.transform.parent != null
                                 ? b.transform.parent.name + "y"
                                   + b.transform.parent.localPosition.y.ToString("F0")
                                 : "?"))
                .ToList();
            OptionsOrder = order.ToArray();
            Plugin.Log.LogInfo("Options menu order: " + string.Join(" | ", order));
            return true;
        }

        /// <summary>Opens our screen when its menu entry is submitted or clicked.</summary>
        internal sealed class MultiplayerEntry : MonoBehaviour,
            UnityEngine.EventSystems.ISubmitHandler,
            UnityEngine.EventSystems.IPointerClickHandler
        {
            public void OnSubmit(UnityEngine.EventSystems.BaseEventData _)
                => Guard.Run(Show, "Show multiplayer screen");

            public void OnPointerClick(UnityEngine.EventSystems.PointerEventData _)
                => Guard.Run(Show, "Show multiplayer screen");
        }

        private static void Show()
        {
            var uim = UIManager.instance;
            if (uim == null || Screen == null) return;
            uim.StartCoroutine(ShowSeq(uim));
        }

        private static IEnumerator ShowSeq(UIManager uim)
        {
            yield return uim.StartCoroutine(uim.HideCurrentMenu());
            Visible = true;
            yield return uim.StartCoroutine(uim.ShowMenu(Screen));
            // Vanilla input handling (cancel = go back to Options) keys off the
            // menu state; Game Options state has exactly that behavior.
            AccessTools.Method(typeof(UIManager), "SetMenuState")?
                .Invoke(uim, new object[] { GlobalEnums.MainMenuState.GAME_OPTIONS_MENU });
        }

        /// <summary>
        /// Whenever the UI hides "the current menu" while OUR screen is the one
        /// showing, ours is the one to hide — vanilla would hide the real Game
        /// Options screen (already hidden) and strand ours on top.
        /// </summary>
        [HarmonyPatch(typeof(UIManager), nameof(UIManager.HideCurrentMenu))]
        internal static class HideCurrentPatch
        {
            private static bool Prefix(UIManager __instance, ref IEnumerator __result)
            {
                if (!Visible || Screen == null) return true;
                Visible = false;
                __result = __instance.HideMenu(Screen);
                return false;
            }
        }
    }

    /// <summary>Build the screen once the Options menu machinery exists.</summary>
    [HarmonyPatch(typeof(UIManager), nameof(UIManager.UIGoToOptionsMenu))]
    internal static class OptionsMenuHook
    {
        private static void Postfix() => MultiplayerScreen.Ensure();
    }
}
