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

            // Whatever this frame decides, the pane driver runs afterwards —
            // and it is the thing that hands the camera back. Several paths
            // below return early (co-op ended, a lock zone, the group not
            // whole), and any one of them reached mid-split used to leave
            // cam.enabled false, which is a black screen rather than a
            // mis-framed one.
            var tkCam = Tk2d;
            var letterbox = tkCam != null ? tkCam.CameraSettings.rect : new Rect(0f, 0f, 1f, 1f);
            try { Frame(__instance, cam, letterbox); }
            finally
            {
                Guard.Run(() => SplitScreen.Draw(cam, __instance, letterbox), "Split draw");
            }
        }

        private static void Frame(CameraController __instance, Camera cam, Rect letterbox)
        {
            if (!CoopManager.Active)
            {
                SplitScreen.Reset();
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

            // Panes divide the letterbox, never the whole screen:
            // ForceCameraAspect owns that rect and replacing it would undo the
            // game's own aspect handling.
            var heroes = CoopManager.FramableHeroes.ToList();
            SplitScreen.Evaluate(heroes, cam, letterbox);

            // A lock zone constrains the view deliberately, and for ONE view
            // that wins — the players are confined there anyway. It does not
            // win over a split: a boss fight with a player off-screen is
            // unplayable, which is the case split-screen exists for. Panes
            // clamp to the lock instead (see ClampForView), so the arena still
            // bounds what each one shows.
            if (__instance.lockZoneList != null && __instance.lockZoneList.Count > 0)
            {
                _hasSmoothed = false;   // re-snap when the lock releases
                return;                 // Draw still runs, from the finally
            }

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

        /// <summary>
        /// Aim the camera at one pane's Knights, for that pane's shape.
        ///
        /// A pane is not the same shape as the screen, so its aspect is not
        /// the camera's usual one — framing it with the full-screen aspect
        /// would cut off exactly the axis the split was made along. The pane's
        /// own width and height give the aspect to frame against.
        /// </summary>
        internal static void FramePane(Camera cam, CameraController cc, tk2dCamera tk,
                                       Pane pane, float camZ)
        {
            if (cam == null || pane == null || pane.Knights.Count == 0) return;

            var centre = pane.Centre;
            var target = new Vector3(centre.x, centre.y, camZ);
            cam.transform.position = ClampForView(cc, target);

            if (tk == null || BaseFov <= 0f) return;

            var planeZ = PlaneZ(pane.Knights);
            var baseHalf = BaseHalfHeight(cam, planeZ);
            if (baseHalf <= 0f) return;

            // The pane's aspect, not the screen's: a half-width pane is half as
            // wide for the same height, so it needs to zoom out further to hold
            // the same horizontal spread.
            var fov = PaneFov(cam, pane, camZ);
            if (fov > 0.01f) tk.ZoomFactor = BaseFov / fov;
        }

        /// <summary>
        /// Vertical field of view that holds one pane's Knights.
        ///
        /// A pane is not the shape of the screen, so it is framed against its
        /// OWN aspect — a half-width pane is half as wide for the same height
        /// and must open up further to hold the same horizontal spread.
        /// Framing it with the full-screen aspect would crop away exactly the
        /// axis the split was made along.
        /// </summary>
        /// <summary>
        /// Keep a pane's view where the game allows one.
        ///
        /// Inside a CameraLockArea the game is deliberately constraining the
        /// view, and its limits live in different fields from the scene's —
        /// KeepWithinSceneBounds clamps to the ROOM, which inside an arena is
        /// far wider than the lock permits. Splitting inside locks was a
        /// deliberate choice (a boss fight with a player off-screen is
        /// unplayable), so panes go where the lock allows rather than standing
        /// down entirely.
        /// </summary>
        internal static Vector3 ClampForView(CameraController cc, Vector3 target)
        {
            if (cc == null) return target;
            if (cc.lockZoneList != null && cc.lockZoneList.Count > 0)
                return new Vector3(
                    Mathf.Clamp(target.x, cc.xLockMin, Mathf.Max(cc.xLockMin, cc.xLockMax)),
                    Mathf.Clamp(target.y, cc.yLockMin, Mathf.Max(cc.yLockMin, cc.yLockMax)),
                    target.z);
            return cc.KeepWithinSceneBounds(target);
        }

        internal static float PaneFov(Camera reference, Pane pane, float camZ)
        {
            if (pane == null) return -1f;
            var aspect = pane.Viewport.height > 0f && Screen.height > 0
                ? (Screen.width * pane.Viewport.width) / (Screen.height * pane.Viewport.height)
                : (reference != null ? reference.aspect : 1.78f);
            return FovFor(pane.Knights, reference, camZ, aspect, clamp: true);
        }

        /// <summary>
        /// Vertical fov that holds these Knights at this aspect.
        ///
        /// <paramref name="clamp"/> keeps the result inside the configured
        /// zoom range, which is right for a view a player looks at. It is
        /// wrong for a view that only has to COVER something — the darkness
        /// cutout has to reach every pane whatever the zoom rules say, or the
        /// panes outside it come back unlit.
        /// </summary>
        internal static float FovFor(List<HeroController> heroes, Camera reference,
                                     float camZ, float aspect, bool clamp)
        {
            if (reference == null || heroes == null || heroes.Count == 0) return -1f;
            var planeZ = PlaneZ(heroes);
            var baseHalf = BaseHalfHeight(reference, planeZ);
            if (baseHalf <= 0f) return -1f;

            var needed = RequiredHalfHeight(heroes, Mathf.Max(aspect, 0.01f));
            needed = clamp
                ? Mathf.Clamp(needed, baseHalf, MaxAllowedHalfHeight(reference, planeZ))
                : Mathf.Max(needed, baseHalf);

            var dist = Mathf.Abs(camZ - planeZ);
            if (dist <= 0.01f) return -1f;
            return Mathf.Min(2f * Mathf.Atan(needed / dist) * Mathf.Rad2Deg, 175f);
        }

        /// <summary>
        /// Point a spare camera at one pane, borrowing everything about how
        /// the game's own camera sees the world.
        ///
        /// Everything copied here is what makes the pane show the same world:
        /// which layers it can see, how it clears, where its clip planes are,
        /// and how it sorts transparency — a 2D game gets that last one wrong
        /// very visibly. Its own fov comes from the pane's shape, and its
        /// depth puts it after the game's camera so the draw order is defined
        /// rather than incidental.
        /// </summary>
        internal static void ConfigurePaneCamera(Camera pane, Camera source,
                                                 CameraController cc, Pane p, int index)
        {
            if (pane == null || source == null || p == null || p.Knights.Count == 0) return;

            pane.rect = p.Viewport;
            pane.cullingMask = source.cullingMask;
            pane.clearFlags = source.clearFlags;
            pane.backgroundColor = source.backgroundColor;
            pane.nearClipPlane = source.nearClipPlane;
            pane.farClipPlane = source.farClipPlane;
            pane.orthographic = false;
            pane.transparencySortMode = source.transparencySortMode;
            pane.transparencySortAxis = source.transparencySortAxis;
            pane.allowHDR = source.allowHDR;
            pane.allowMSAA = source.allowMSAA;
            pane.depth = source.depth + index;
            pane.transform.rotation = source.transform.rotation;

            var camZ = source.transform.position.z;
            var centre = p.Centre;
            var target = new Vector3(centre.x, centre.y, camZ);
            pane.transform.position = ClampForView(cc, target);

            var fov = PaneFov(source, p, camZ);
            if (fov > 0.01f) pane.fieldOfView = fov;
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
