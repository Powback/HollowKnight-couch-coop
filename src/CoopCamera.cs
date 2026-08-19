using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace HKCouchCoop
{
    /// <summary>
    /// Shared-camera framing for any number of Knights.
    ///
    /// Vanilla LateUpdate positions the camera on player one every frame. We run
    /// after it and overwrite that with a frame of our own.
    ///
    /// Two rules keep this from fighting the game:
    ///  - We smooth against *our own* previous position, never the one vanilla
    ///    just wrote — lerping from vanilla's value re-snaps toward player one
    ///    every frame and oscillates forever.
    ///  - While any CameraLockArea is active (boss arenas, scripted rooms) we
    ///    stand down entirely and let vanilla drive: the lock's whole point is
    ///    to constrain the view, and the players are confined there anyway.
    /// </summary>
    [HarmonyPatch(typeof(CameraController), "LateUpdate")]
    internal static class CoopCamera
    {
        private static float _baseSize = -1f;
        private static Vector2 _smoothed;
        private static bool _hasSmoothed;
        private static bool _weZoomed;   // only restore a size WE changed, once

        /// <summary>Drop smoothing state so the next frame snaps rather than sweeps.</summary>
        internal static void Reset() => _hasSmoothed = false;

        private static void Postfix(CameraController __instance)
        {
            var cam = __instance.cam;
            if (cam == null) return;

            if (!CoopManager.Active)
            {
                // Restore exactly once after co-op ends; then leave the camera
                // alone so vanilla zoom effects are never fought.
                if (_weZoomed)
                {
                    if (_baseSize > 0f) cam.orthographicSize = _baseSize;
                    _weZoomed = false;
                }
                _hasSmoothed = false;
                return;
            }

            // A lock zone means the game is deliberately constraining the view.
            if (__instance.lockZoneList != null && __instance.lockZoneList.Count > 0)
            {
                _hasSmoothed = false;   // re-snap when the lock releases
                return;
            }

            var heroes = CoopManager.AllHeroes.ToList();
            if (heroes.Count < 2) return;

            if (_baseSize < 0f) _baseSize = cam.orthographicSize;

            var bounds = Enclose(heroes);

            if (Plugin.Cfg.CameraZoom.Value) ApplyZoom(cam, bounds);

            var current = __instance.transform.position;
            var target = __instance.KeepWithinSceneBounds(
                new Vector3(bounds.center.x, bounds.center.y, current.z));

            if (!_hasSmoothed)
            {
                _smoothed = target;
                _hasSmoothed = true;
            }
            else
            {
                _smoothed = Vector2.Lerp(
                    _smoothed, target, Time.deltaTime * Plugin.Cfg.FollowSpeed.Value);
            }

            __instance.transform.position = new Vector3(_smoothed.x, _smoothed.y, current.z);
        }

        /// <summary>Smallest box containing every Knight.</summary>
        private static Bounds Enclose(List<HeroController> heroes)
        {
            var b = new Bounds(heroes[0].transform.position, Vector3.zero);
            for (var i = 1; i < heroes.Count; i++)
                b.Encapsulate(heroes[i].transform.position);
            return b;
        }

        private static void ApplyZoom(Camera cam, Bounds bounds)
        {
            var margin = Plugin.Cfg.ZoomMargin.Value;
            var halfH = (bounds.size.y * 0.5f) + margin;
            var halfW = ((bounds.size.x * 0.5f) + margin) / Mathf.Max(cam.aspect, 0.01f);

            var needed = Mathf.Max(halfH, halfW, _baseSize);
            var maxSize = _baseSize * Plugin.Cfg.MaxZoomFactor.Value;

            var next = Mathf.Lerp(
                cam.orthographicSize,
                Mathf.Clamp(needed, _baseSize, maxSize),
                Time.deltaTime * Plugin.Cfg.ZoomSpeed.Value);

            if (!Mathf.Approximately(next, cam.orthographicSize))
            {
                cam.orthographicSize = next;
                _weZoomed = true;
            }
        }
    }
}
