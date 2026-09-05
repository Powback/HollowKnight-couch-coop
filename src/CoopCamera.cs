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
        // The un-zoomed vertical field of view, from tk2d's settings. This is
        // the real handle on how much world the camera shows.
        //
        // Everything here used to be written in orthographic size, which was
        // measured in the running game to be inert: Hollow Knight's world
        // camera is PERSPECTIVE (tk2dProjection=Perspective, orthographic
        // false), tk2dCamera drives it as fieldOfView / ZoomFactor and calls
        // ResetProjectionMatrix, and cam.orthographicSize reads a leftover
        // serialized 480 that the renderer never looks at. Writing zoom there
        // did nothing at all, and because 480 dwarfed every real distance it
        // also pinned RequiredSize and MaxAllowedSize to the same number — so
        // the screen leash's "camera cannot frame this" test was never true
        // either. Both features were silently dead in every released version.
        private static Vector2 _smoothed;
        private static bool _hasSmoothed;
        private static bool _weZoomed;   // only restore a zoom WE changed, once

        /// <summary>Drop smoothing state so the next frame snaps rather than sweeps.</summary>
        internal static void Reset() => _hasSmoothed = false;

        private static tk2dCamera Tk2d =>
            GameCameras.instance != null ? GameCameras.instance.tk2dCam : null;

        /// <summary>
        /// The un-zoomed vertical fov, read live rather than cached.
        ///
        /// Live because ForceCameraAspect rewrites CameraSettings.fieldOfView
        /// on every resolution change, so a value captured once is wrong the
        /// moment the window is resized — and it anchors every number below.
        /// Safe because our own zoom never touches this: tk2d renders
        /// fieldOfView = CameraSettings.fieldOfView / ZoomFactor, and we move
        /// only the divisor, so settings fov is by construction the view with
        /// no zoom of ours applied.
        /// </summary>
        private static float BaseFov
        {
            get
            {
                var tk = Tk2d;
                return tk != null ? tk.CameraSettings.fieldOfView : -1f;
            }
        }

        /// <summary>The z-plane the Knights live on, which the view is measured at.</summary>
        private static float PlaneZ(List<HeroController> heroes)
        {
            if (heroes == null || heroes.Count == 0) return 0f;
            var z = 0f;
            foreach (var h in heroes) z += h.transform.position.z;
            return z / heroes.Count;
        }

        /// <summary>
        /// How much world, vertically, a given field of view shows at that
        /// plane. On a perspective camera this is the only honest measure of
        /// "how far out are we zoomed".
        /// </summary>
        internal static float HalfHeightAt(Camera cam, float fovDegrees, float planeZ)
        {
            var dist = Mathf.Abs(cam.transform.position.z - planeZ);
            return dist * Mathf.Tan(fovDegrees * 0.5f * Mathf.Deg2Rad);
        }

        /// <summary>World half-height needed to frame these Knights, with margin.</summary>
        internal static float RequiredHalfHeight(List<HeroController> heroes, float aspect)
        {
            var b = Enclose(heroes);
            var margin = Plugin.Cfg.ZoomMargin.Value;
            var halfH = (b.size.y * 0.5f) + margin;
            var halfW = ((b.size.x * 0.5f) + margin) / Mathf.Max(aspect, 0.01f);
            return Mathf.Max(halfH, halfW);
        }

        /// <summary>
        /// The furthest this camera may widen: the configured factor, ceilinged
        /// by what the ROOM can show — zoom out until the whole scene fits and
        /// no further.
        /// </summary>
        internal static float MaxAllowedHalfHeight(Camera cam, float planeZ)
        {
            var baseHalf = BaseHalfHeight(cam, planeZ);
            if (baseHalf <= 0f) return 0f;

            // Never below the game's own view. A MaxZoomFactor under 1 would
            // otherwise put the ceiling beneath the floor, and Mathf.Clamp
            // with min above max returns the max — so the camera would zoom
            // IN, showing less than vanilla. Widening is the only thing this
            // feature is allowed to do.
            var byFactor = baseHalf * Mathf.Max(Plugin.Cfg.MaxZoomFactor.Value, 1f);
            var gm = GameManager.instance;
            if (gm == null) return byFactor;

            var sceneFit = Mathf.Max(
                gm.sceneHeight * 0.5f,
                gm.sceneWidth * 0.5f / Mathf.Max(cam.aspect, 0.01f));
            return Mathf.Max(Mathf.Min(byFactor, Mathf.Max(sceneFit, baseHalf)), baseHalf);
        }

        /// <summary>What the view shows when we are not zooming it at all.</summary>
        internal static float BaseHalfHeight(Camera cam, float planeZ) =>
            BaseFov > 0f ? HalfHeightAt(cam, BaseFov, planeZ) : 0f;

        /// <summary>Half-height the camera is showing right now.</summary>
        internal static float CurrentHalfHeight(Camera cam, float planeZ) =>
            HalfHeightAt(cam, cam.fieldOfView, planeZ);

        /// <summary>
        /// Everything the framing decision is made from, in world half-heights.
        ///
        /// Exposed rather than kept private because a zoom test cannot
        /// otherwise tell "the camera refused to widen" from "the Knights
        /// never got far enough apart to need it" — and those have opposite
        /// meanings. `needed` above `baseHalf` is the group asking for more
        /// view than the game's own; `current` above `baseHalf` is the camera
        /// having given it.
        /// </summary>
        internal static bool Framing(List<HeroController> heroes, Camera cam,
                                     out float needed, out float allowed,
                                     out float baseHalf, out float current)
        {
            needed = allowed = baseHalf = current = 0f;
            if (cam == null || heroes == null || heroes.Count == 0 || BaseFov <= 0f) return false;
            var planeZ = PlaneZ(heroes);
            needed = RequiredHalfHeight(heroes, cam.aspect);
            allowed = MaxAllowedHalfHeight(cam, planeZ);
            baseHalf = BaseHalfHeight(cam, planeZ);
            current = CurrentHalfHeight(cam, planeZ);
            return allowed > 0f;
        }

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
                    var tk = Tk2d;
                    if (tk != null) tk.ZoomFactor = 1f;   // 1 = the game's own view
                    _weZoomed = false;
                }
                _hasSmoothed = false;
                return;
            }

            // Nothing to initialise here any more. The un-zoomed fov used to
            // be captured on first use, and a lock zone that returned before
            // that left it unset — which made "max allowed zoom" nonsense and
            // teleported player two onto player one every frame of a boss
            // fight. BaseFov now reads tk2d's settings live, so no early
            // return can leave it uninitialised and no resize can stale it.

            // A lock zone means the game is deliberately constraining the view.
            if (__instance.lockZoneList != null && __instance.lockZoneList.Count > 0)
            {
                _hasSmoothed = false;   // re-snap when the lock releases
                return;
            }

            var heroes = CoopManager.FramableHeroes.ToList();
            if (heroes.Count < 2)
            {
                // Only player one is framable (mid-transition, or everyone
                // parked): leave the camera to vanilla and re-snap when the
                // group is whole again.
                _hasSmoothed = false;
                return;
            }

            var bounds = Enclose(heroes);

            if (Plugin.Cfg.CameraZoom.Value) ApplyZoom(cam, heroes);

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

        /// <summary>
        /// Widen the view to hold the group.
        ///
        /// tk2dCamera computes fieldOfView as (settings fov / ZoomFactor) every
        /// OnPreCull, so ZoomFactor is the only knob that survives the frame —
        /// writing cam.fieldOfView directly is overwritten, and writing
        /// cam.orthographicSize does nothing at all on a perspective camera.
        /// Widening the view means a ZoomFactor BELOW one.
        /// </summary>
        private static void ApplyZoom(Camera cam, List<HeroController> heroes)
        {
            var tk = Tk2d;
            if (tk == null || BaseFov <= 0f) return;

            var planeZ = PlaneZ(heroes);
            var baseHalf = BaseHalfHeight(cam, planeZ);
            if (baseHalf <= 0f) return;

            var needed = Mathf.Clamp(
                RequiredHalfHeight(heroes, cam.aspect),
                baseHalf,
                MaxAllowedHalfHeight(cam, planeZ));

            var dist = Mathf.Abs(cam.transform.position.z - planeZ);
            if (dist <= 0.01f) return;

            var targetFov = 2f * Mathf.Atan(needed / dist) * Mathf.Rad2Deg;
            if (targetFov <= 0.01f) return;

            var targetZoom = BaseFov / targetFov;
            var next = Mathf.Lerp(
                tk.ZoomFactor, targetZoom, Time.deltaTime * Plugin.Cfg.ZoomSpeed.Value);

            if (!Mathf.Approximately(next, tk.ZoomFactor))
            {
                tk.ZoomFactor = next;
                _weZoomed = true;
            }
        }
    }
}
