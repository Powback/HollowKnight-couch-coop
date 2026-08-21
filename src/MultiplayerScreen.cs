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

        private static bool BuildEntryButton(UIManager uim)
        {
            // HK's option-list entries are MenuButton (a Selectable that handles
            // submit itself), not Unity Buttons with onClick handlers — probing
            // for onClick targets found nothing, which is why no entry appeared.
            var buttons = uim.optionsMenuScreen.GetComponentsInChildren<MenuButton>(true)
                .OrderByDescending(b => b.transform.localPosition.y).ToList();
            if (buttons.Count == 0) return false;

            var template = buttons[0];
            var step = buttons.Count >= 2
                ? buttons[0].transform.localPosition - buttons[1].transform.localPosition
                : new Vector3(0f, 60f, 0f);

            _entryButton = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent);
            _entryButton.name = "HKCC_MultiplayerButton";
            NativeMenu.StripLocalizers(_entryButton);
            foreach (var txt in _entryButton.GetComponentsInChildren<Text>(true)) txt.text = "MULTIPLAYER";
            foreach (var tmp in _entryButton.GetComponentsInChildren<TMPro.TMP_Text>(true)) tmp.text = "MULTIPLAYER";

            // Strip whatever navigation the clone inherited (serialized onClick,
            // event triggers), then attach ours. Unity dispatches ISubmitHandler
            // to EVERY component on the object, so our handler runs alongside
            // MenuButton's own flash/sound without replacing it.
            foreach (var b in _entryButton.GetComponents<Button>())
                b.onClick = new Button.ButtonClickedEvent();
            foreach (var et in _entryButton.GetComponents<UnityEngine.EventSystems.EventTrigger>())
                UnityEngine.Object.DestroyImmediate(et);
            _entryButton.AddComponent<MultiplayerEntry>();

            _entryButton.transform.localPosition =
                buttons[buttons.Count - 1].transform.localPosition - step;

            var selectable = _entryButton.GetComponent<MenuButton>() as Selectable
                             ?? _entryButton.GetComponent<Selectable>();
            if (selectable != null) NativeMenu.AppendToButtonList(uim.optionsMenuScreen, selectable);
            _entryButton.SetActive(true);
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
