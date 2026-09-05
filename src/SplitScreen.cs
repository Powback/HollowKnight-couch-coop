using System.Collections.Generic;
using System.Linq;
using CoopKit;
using UnityEngine;

namespace HKCouchCoop
{
    /// <summary>One pane of a split view: where it draws, and who it frames.</summary>
    internal sealed class Pane
    {
        /// <summary>Viewport rect in screen fractions, inside the letterbox.</summary>
        internal Rect Viewport;

        /// <summary>The Knights this pane is responsible for showing.</summary>
        internal readonly List<HeroController> Knights = new List<HeroController>();

        internal Vector2 Centre
        {
            get
            {
                if (Knights.Count == 0) return Vector2.zero;
                var b = new Bounds(Knights[0].transform.position, Vector3.zero);
                for (var i = 1; i < Knights.Count; i++)
                    b.Encapsulate(Knights[i].transform.position);
                return b.center;
            }
        }
    }

    /// <summary>
    /// Dynamic split-screen layout: how the screen is divided when the Knights
    /// spread further than one view can hold, and when it becomes one view
    /// again.
    ///
    /// Geometry only — nothing here renders. Keeping the decision separate from
    /// the drawing means the layout can be read off the debug channel and
    /// tested before any of the rendering exists, which is the same reason the
    /// camera's framing numbers are published there.
    ///
    /// PANES ARE ALWAYS THE SAME SIZE AS EACH OTHER. That is not an aesthetic
    /// choice. DarknessCameraEffect sizes its RenderTexture from
    /// mainCamera.pixelWidth/Height and destroys and recreates it whenever
    /// those change, so panes of differing sizes would reallocate a
    /// RenderTexture on every pass of every frame. A uniform grid keeps the
    /// pixel dimensions constant across the passes of a frame, and reallocates
    /// only when the layout itself changes.
    /// </summary>
    internal static class SplitScreen
    {
        /// <summary>The layout in force right now. Empty means one whole view.</summary>
        internal static IReadOnlyList<Pane> Current => _panes;

        private static List<Pane> _panes = new List<Pane>();
        private static bool _split;

        /// <summary>True while the screen is divided.</summary>
        internal static bool Active => _split && _panes.Count > 1;

        internal static void Reset()
        {
            _split = false;
            _panes = new List<Pane>();
        }

        // ── rendering ────────────────────────────────────────────────────

        /// <summary>
        /// Tells tk2d to leave the viewport alone.
        ///
        /// tk2dCamera resets unityCamera.rect from its own CameraSettings on
        /// every OnPreCull, which fires once per render pass — so without this
        /// every pane rect would be overwritten before it drew. The game ships
        /// the switch for it: UpdateCameraMatrix skips that reset while any
        /// registered listener reports frozen rendering.
        /// </summary>
        private sealed class FreezeViewport : Tk2dGlobalEvents.IListener
        {
            internal bool Frozen;
            public void ColliderUpdated(GameObject go) { }
            public void TilemapChunkCreated(Transform grandChild) { }
            public bool IsFrozenCameraRendering() => Frozen;
        }

        private static readonly FreezeViewport Freeze = new FreezeViewport();
        private static bool _registered;
        private static bool _driving;      // we turned the camera's auto-render off

        // Driving the camera means holding its automatic render off, which is
        // the one state in this mod that is worse to be stuck in than to be
        // wrong in: a mis-framed pane is a bad frame, a camera left disabled is
        // a black screen. Guard.Run would swallow a throw here and cheerfully
        // repeat it every frame forever, so failures are counted and the whole
        // feature stands itself down rather than leaving the view dark.
        private const int GiveUpAfter = 3;
        private static int _failures;
        private static bool _abandoned;

        /// <summary>True when the driver has given up for this session.</summary>
        internal static bool Abandoned => _abandoned;

        /// <summary>
        /// Give every pane a camera and let Unity draw them.
        ///
        /// The first design drove one camera N times with cam.Render() from
        /// LateUpdate. It threw nothing, framed correctly, reported a perfect
        /// layout — and rendered a black world behind an intact HUD, because
        /// LateUpdate runs BEFORE the frame's own render pass and a camera
        /// left disabled contributes nothing to it. Whatever those manual
        /// passes drew was cleared before anything reached the screen.
        ///
        /// So panes are cameras now, which is what Unity supports: enabled
        /// cameras with viewport rects, drawn by the engine at the proper time
        /// in the proper order. The game's own camera keeps the first pane —
        /// it carries tk2dCamera, the image effects and the fade and shake
        /// FSMs, so the pane player one is in looks exactly like the game
        /// always did. The extra panes get plain cameras that copy its
        /// culling, clear settings and clip planes, and compute their own
        /// perspective from the pane's shape.
        ///
        /// The extras deliberately do NOT carry the image-effect stack. A pane
        /// without the brightness pass looks slightly different; a pane that
        /// reallocates a RenderTexture every frame costs frames. That trade is
        /// revisited when per-pane darkness is built.
        /// </summary>
        internal static void Draw(Camera cam, CameraController cc, Rect letterbox)
        {
            if (cam == null) return;

            if (!Active || _abandoned)
            {
                Release(cam, letterbox);
                return;
            }

            if (!_registered)
            {
                Tk2dGlobalEvents.AddListener(Freeze);
                _registered = true;
            }

            var tk = GameCameras.instance != null ? GameCameras.instance.tk2dCam : null;
            try
            {
                Freeze.Frozen = true;   // tk2d must not reclaim the viewport
                _driving = true;

                // Rotating split: a different way of reaching the screen, so
                // it either takes over completely or does not run at all.
                if (Plugin.Cfg.SplitRotate.Value && _panes.Count >= 2
                    && _panes.Count <= 4 && SplitCompositor.Available)
                {
                    EnsurePaneCameras(cam);
                    if (SplitCompositor.Draw(cam, cc, _panes, _extra))
                    {
                        // Switch off only the cameras this layout does NOT use.
                        // The compositor drives _extra[0..panes-2]; starting at
                        // 1 switched off ones it had just enabled, so panes 2
                        // and 3 painted from a texture nothing had rendered.
                        for (var i = _panes.Count - 1; i < _extra.Count; i++)
                            _extra[i].gameObject.SetActive(false);
                        _failures = 0;
                        return;
                    }
                }
                SplitCompositor.Release(_extra, cam);

                // Pane 0 is the game's own camera, framed and cropped in place.
                cam.rect = _panes[0].Viewport;
                CoopCamera.FramePane(cam, cc, tk, _panes[0], cam.transform.position.z);

                EnsurePaneCameras(cam);
                for (var i = 1; i < _panes.Count; i++)
                {
                    var pc = _extra[i - 1];
                    pc.gameObject.SetActive(true);
                    CoopCamera.ConfigurePaneCamera(pc, cam, cc, _panes[i], i);
                }
                for (var i = _panes.Count - 1; i < _extra.Count; i++)
                    _extra[i].gameObject.SetActive(false);

                _failures = 0;
            }
            catch (System.Exception e)
            {
                if (++_failures >= GiveUpAfter)
                {
                    _abandoned = true;
                    Plugin.Log.LogError(
                        "Split-screen failed to set up " + GiveUpAfter + " frames running and "
                        + "has stood down for this session; the view is whole again. Last error: "
                        + e);
                }
                Release(cam, letterbox);
                throw;
            }
        }

        private static readonly List<Camera> _extra = new List<Camera>();
        private static GameObject _holder;

        /// <summary>One spare camera per pane beyond the first, made once.</summary>
        private static void EnsurePaneCameras(Camera source)
        {
            if (_holder == null)
            {
                _holder = new GameObject("HKCouchCoop Panes");
                Object.DontDestroyOnLoad(_holder);
            }
            while (_extra.Count < _panes.Count - 1)
            {
                var go = new GameObject("Pane " + (_extra.Count + 2));
                go.transform.SetParent(_holder.transform, worldPositionStays: false);
                var c = go.AddComponent<Camera>();
                c.enabled = true;
                _extra.Add(c);
            }
        }

        /// <summary>Hand the camera back to the game, once.</summary>
        private static void Release(Camera cam, Rect letterbox)
        {
            if (!_driving) return;
            _driving = false;
            Freeze.Frozen = false;
            SplitCompositor.Release(_extra, cam);   // hands the screen back
            cam.rect = letterbox;
            cam.enabled = true;
            foreach (var c in _extra)
                if (c != null) c.gameObject.SetActive(false);
            var tk = GameCameras.instance != null ? GameCameras.instance.tk2dCam : null;
            if (tk != null) tk.ZoomFactor = 1f;
        }

        /// <summary>Clear a stand-down, e.g. when a session ends.</summary>
        internal static void ClearAbandoned()
        {
            _abandoned = false;
            _failures = 0;
        }

        /// <summary>
        /// Decide the layout for this frame.
        ///
        /// Splitting and merging use different thresholds on purpose. A single
        /// threshold sits exactly on the boundary where the group is marginal,
        /// and the screen would tear itself in half and back together every
        /// few frames — the same oscillation the leash guards against.
        /// </summary>
        internal static void Evaluate(List<HeroController> heroes, Camera cam, Rect letterbox)
        {
            if (cam == null || heroes == null || heroes.Count < 2
                || !Plugin.Cfg.SplitScreen.Value)
            {
                Reset();
                return;
            }

            if (!CoopCamera.Framing(heroes, cam, out var needed, out var allowed,
                                    out _, out _))
            {
                Reset();
                return;
            }

            var margin = Mathf.Max(Plugin.Cfg.SplitMergeMargin.Value, 0f);
            _split = _split
                ? needed > allowed * (1f - margin)   // stay split until comfortably back
                : needed > allowed * (1f + margin);  // split only once clearly past

            _panes = _split ? Divide(heroes, letterbox) : new List<Pane>();
        }

        /// <summary>
        /// Cut the letterbox into one equal pane per Knight, along whichever
        /// axis they are actually separated on, and hand each pane the Knight
        /// nearest its slot.
        /// </summary>
        private static List<Pane> Divide(List<HeroController> heroes, Rect letterbox)
        {
            var panes = new List<Pane>();
            var n = Mathf.Clamp(heroes.Count, 2, 4);

            // Which way are they spread? The axis with the wider spread is the
            // one worth giving each Knight room along, so it is the axis that
            // gets cut.
            var b = new Bounds(heroes[0].transform.position, Vector3.zero);
            foreach (var h in heroes) b.Encapsulate(h.transform.position);
            var horizontal = b.size.x >= b.size.y;

            if (n == 4)
            {
                // Four Knights get quadrants; a single strip would leave each
                // one a sliver far narrower than anything this game renders.
                var ordered = heroes
                    .OrderByDescending(h => h.transform.position.y)
                    .ThenBy(h => h.transform.position.x)
                    .ToList();
                var top = ordered.Take(2).OrderBy(h => h.transform.position.x).ToList();
                var bottom = ordered.Skip(2).OrderBy(h => h.transform.position.x).ToList();
                var half = new Vector2(letterbox.width * 0.5f, letterbox.height * 0.5f);
                for (var i = 0; i < 4; i++)
                {
                    var col = i % 2;
                    var row = i / 2;                     // 0 = top row
                    var pane = new Pane
                    {
                        Viewport = new Rect(
                            letterbox.x + col * half.x,
                            letterbox.y + (1 - row) * half.y,
                            half.x, half.y),
                    };
                    var src = row == 0 ? top : bottom;
                    if (col < src.Count) pane.Knights.Add(src[col]);
                    panes.Add(pane);
                }
                return panes;
            }

            // Two or three: equal strips across the axis they are spread on,
            // in the order they stand along it, so the panes read left-to-right
            // (or top-to-bottom) the way the players are actually arranged.
            var sorted = horizontal
                ? heroes.OrderBy(h => h.transform.position.x).ToList()
                : heroes.OrderByDescending(h => h.transform.position.y).ToList();

            for (var i = 0; i < n; i++)
            {
                var pane = new Pane();
                pane.Viewport = horizontal
                    ? new Rect(letterbox.x + letterbox.width * i / n, letterbox.y,
                               letterbox.width / n, letterbox.height)
                    : new Rect(letterbox.x, letterbox.y + letterbox.height * (n - 1 - i) / n,
                               letterbox.width, letterbox.height / n);
                if (i < sorted.Count) pane.Knights.Add(sorted[i]);
                panes.Add(pane);
            }
            return panes;
        }

        /// <summary>Layout description for the debug channel.</summary>
        internal static string LayoutJson()
        {
            var j = Json.Object()
                .Add("active", Active)
                .Add("abandoned", _abandoned)
                .Add("paneCount", _panes.Count)
                .Add("rotating", SplitCompositor.Active)
                .Add("rotateNormalX", SplitCompositor.Normal.x)
                .Add("rotateNormalY", SplitCompositor.Normal.y)
                // In rotating mode the pane rects below are NOT what is drawn —
                // the screen is cut into regions around these sites instead. Say
                // so, so nothing reads a rectangle that describes nothing.
                .Add("paneRectsApply", !SplitCompositor.Active)
                .Add("regionAspect", SplitCompositor.Aspect);

            if (SplitCompositor.Active)
            {
                var sites = new List<string>();
                foreach (var st in SplitCompositor.RegionSites)
                    sites.Add(Json.Object().Add("x", st.x).Add("y", st.y).Close());
                j.AddRaw("regionSites", Json.Array(sites));
            }

            var panes = new List<string>();
            foreach (var p in _panes)
            {
                panes.Add(Json.Object()
                    .Add("x", p.Viewport.x).Add("y", p.Viewport.y)
                    .Add("w", p.Viewport.width).Add("h", p.Viewport.height)
                    .AddRaw("knights", Json.Array(
                        p.Knights.Select(k => Json.Object()
                            .Add("name", k != null ? k.gameObject.name : null).Close())
                            .ToList()))
                    .Close());
            }
            return j.AddRaw("panes", Json.Array(panes)).Close();
        }
    }
}
