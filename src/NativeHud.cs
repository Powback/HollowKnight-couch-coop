using System.Linq;
using TMPro;
using UnityEngine;

namespace HKCouchCoop
{
    /// <summary>
    /// On-screen text drawn with the game's own HUD canvas and font, rather than
    /// an IMGUI overlay.
    ///
    /// The element is built once, parented under <c>GameCameras.hudCanvas</c>,
    /// and reuses the font asset and material of an existing HUD label — which
    /// is what makes it look like part of the game instead of a debug overlay.
    /// </summary>
    internal static class NativeHud
    {
        private static TextMeshProUGUI _label;
        private static float _noticeUntil;
        private static string _notice;

        private static bool Ready => _label != null;

        internal static void Notify(string message, float seconds = 2.5f)
        {
            _notice = message;
            _noticeUntil = Time.unscaledTime + seconds;
        }

        /// <summary>Rebuild against the current scene's HUD. Safe to call repeatedly.</summary>
        internal static void Invalidate()
        {
            if (_label != null) Object.Destroy(_label.gameObject);
            _label = null;
        }

        internal static void Tick()
        {
            if (!Ready && !TryBuild()) return;

            var text = CurrentText();
            var visible = !string.IsNullOrEmpty(text);

            if (_label.gameObject.activeSelf != visible) _label.gameObject.SetActive(visible);
            if (visible && _label.text != text) _label.text = text;
        }

        private static string CurrentText()
        {
            if (_notice != null && Time.unscaledTime < _noticeUntil)
                return _notice;

            return null;
        }

        private static bool TryBuild()
        {
            var cameras = GameCameras.instance;
            if (cameras == null || cameras.hudCanvas == null) return false;

            // Borrow the look of an existing HUD label so we inherit the game's
            // font asset, material and outline rather than approximating them.
            var template = cameras.hudCanvas
                .GetComponentsInChildren<TextMeshProUGUI>(includeInactive: true)
                .FirstOrDefault(t => t.font != null);

            // No template means TMP is not ready yet, and adding a
            // TextMeshProUGUI now throws inside LoadDefaultSettings —
            // TMP_Settings.instance is null and the exception surfaces as a
            // NullReferenceException from our Update, once per attempt. A live
            // label on the HUD canvas IS the proof that TMP has initialised.
            //
            // Checked BEFORE anything is created: the object used to be built
            // first, so every failed attempt also leaked a GameObject onto the
            // canvas.
            if (template == null) return false;

            var go = new GameObject("HKCouchCoop_Hud");
            go.transform.SetParent(cameras.hudCanvas.transform, worldPositionStays: false);

            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -90f);
            rect.sizeDelta = new Vector2(900f, 60f);

            var label = go.AddComponent<TextMeshProUGUI>();
            label.font = template.font;
            label.fontSharedMaterial = template.fontSharedMaterial;
            label.colorGradientPreset = template.colorGradientPreset;
            label.fontSize = 36f;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;
            label.raycastTarget = false;
            label.text = string.Empty;

            go.SetActive(false);
            _label = label;
            return true;
        }
    }
}
