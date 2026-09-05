using System.Collections.Generic;
using UnityEngine;
using CoopKit;

namespace HKCouchCoop
{
    /// <summary>
    /// A split that follows the line between the players, at any angle.
    ///
    /// The axis-aligned split cannot do this and never will: a pane there is a
    /// camera viewport rect, and a viewport is an axis-aligned rectangle, so
    /// the only cuts available are | and —. To put the divider at an angle the
    /// panes have to stop being viewports. Each camera renders the WHOLE screen
    /// into its own texture, and the screen is then painted from those textures
    /// through polygons — so the boundary can be any line at all.
    ///
    /// The divider runs through the middle of the screen, square to the
    /// direction from one Knight to the other. Stand apart horizontally and it
    /// is vertical; stand one above the other and it is horizontal; stand
    /// diagonally and it tilts to match, which is the whole point.
    ///
    /// Kept behind its own setting and off by default. The axis-aligned path is
    /// verified working; this one changes how the game reaches the screen at
    /// all, and the last time that was done on reasoning alone it produced a
    /// black world behind an intact HUD.
    /// </summary>
    internal static class SplitCompositor
    {
        private static readonly List<RenderTexture> Targets = new List<RenderTexture>();
        private static readonly List<Vector2> Sites = new List<Vector2>();
        private static float _aspect = 1.7778f;
        private static Material _blit;
        private static GameObject _holder;
        private static Camera _painterCam;
        private static Painter _painter;
        private static int _width, _height;

        /// <summary>True when this mode is the one drawing the screen.</summary>
        internal static bool Active { get; private set; }

        /// <summary>The divider's screen-space normal, for diagnostics.</summary>
        internal static Vector2 Normal { get; private set; } = Vector2.right;

        /// <summary>
        /// Where each Knight sits in the aspect-corrected frame the regions are
        /// cut from. Published because in this mode the panes' viewport rects
        /// describe NOTHING that is on screen — the regions do — and a channel
        /// that reports the wrong shape is how a black screen once passed for
        /// working.
        /// </summary>
        internal static IReadOnlyList<Vector2> RegionSites => Sites;

        internal static float Aspect => _aspect;

        internal static bool Available => Shader() != null;

        private static Shader _shader;
        private static Shader Shader()
        {
            if (_shader != null) return _shader;
            // No custom shaders: a BepInEx plugin cannot compile ShaderLab at
            // runtime, so this borrows one the game already has loaded. Tried
            // in order of how exactly they do "draw this texture, unlit".
            foreach (var name in new[] { "Unlit/Texture", "Sprites/Default",
                                         "Hidden/Internal-GUITexture", "UI/Default" })
            {
                var s = UnityEngine.Shader.Find(name);
                if (s != null) { _shader = s; break; }
            }
            return _shader;
        }

        internal static void Release(IReadOnlyList<Camera> extras = null, Camera main = null)
        {
            Active = false;

            // The compositor's camera CLEARS the screen. Leaving it enabled
            // with nothing painting over it is a black screen, so the whole
            // object goes away, not just the painter.
            if (_holder != null) _holder.SetActive(false);
            if (_painter != null) _painter.enabled = false;

            // Hand every camera back to the screen BEFORE the textures go.
            // They were rendering into these; left pointing at a destroyed one
            // they draw nowhere, and the viewport split shows empty panes.
            // Order matters — releasing a texture a live camera still targets
            // is how you get a camera rendering into nothing.
            if (main != null) main.targetTexture = null;
            if (extras != null)
                foreach (var c in extras)
                    if (c != null) c.targetTexture = null;
            foreach (var rt in Targets)
            {
                if (rt == null) continue;
                rt.Release();
                Object.Destroy(rt);
            }
            Targets.Clear();
            _width = _height = 0;
        }

        /// <summary>Point the cameras at textures and paint the screen from them.</summary>
        internal static bool Draw(Camera main, CameraController cc,
                                  IReadOnlyList<Pane> panes, IReadOnlyList<Camera> extras)
        {
            if (main == null || panes == null || panes.Count < 2 || panes.Count > 4)
                return false;
            if (Shader() == null) return false;

            var n = panes.Count;
            EnsureTargets(n);
            if (Targets.Count < n) return false;

            // Every camera frames its own Knight across the FULL screen, not a
            // slice of it: the region decides what is seen, so the framing must
            // not be cropped as well or neighbouring regions would disagree
            // about scale and tear at the boundary.
            var camZ = main.transform.position.z;
            var full = new Rect(0f, 0f, 1f, 1f);

            main.rect = full;
            main.targetTexture = Targets[0];
            CoopCamera.FramePaneFullScreen(main, cc, panes[0], camZ);

            for (var i = 1; i < n; i++)
            {
                var cam = extras != null && extras.Count >= i ? extras[i - 1] : null;
                if (cam == null) return false;
                cam.gameObject.SetActive(true);
                CoopCamera.ConfigurePaneCamera(cam, main, cc, panes[i], i);
                cam.rect = full;                 // after Configure, which crops
                cam.targetTexture = Targets[i];

                // Re-frame against the FULL screen. ConfigurePaneCamera sizes
                // the view for a cropped viewport, which is right for the
                // viewport split and wrong here: these cameras render the whole
                // screen and a region selects part of it, so a pane framed for
                // a half-width slice would be at a different scale from its
                // neighbour and the two would visibly disagree along the
                // boundary — the exact tearing this path is meant to avoid.
                var paneFov = CoopCamera.FovFor(
                    panes[i].Knights, main, camZ, main.aspect, clamp: true);
                if (paneFov > 0.01f) cam.fieldOfView = paneFov;
            }

            BuildSites(panes, main);

            EnsurePainter();
            SyncDepth();
            _holder.SetActive(true);
            _painter.enabled = true;
            Active = true;
            return true;
        }

        /// <summary>
        /// Where each Knight sits on screen, for deciding who owns what.
        ///
        /// Their world positions around the group's centre, scaled to sit
        /// comfortably inside the frame and corrected for the screen being
        /// wider than it is tall — without that correction the regions look
        /// skewed, because a pixel step across is not the same distance as a
        /// pixel step up.
        /// </summary>
        private static void BuildSites(IReadOnlyList<Pane> panes, Camera main)
        {
            _aspect = main.aspect > 0.01f ? main.aspect : 1.7778f;
            Sites.Clear();

            var centre = Vector2.zero;
            foreach (var p in panes) centre += p.Centre;
            centre /= panes.Count;

            var reach = 0f;
            foreach (var p in panes)
                reach = Mathf.Max(reach, (p.Centre - centre).magnitude);

            // 0.35 keeps every site inside the frame with room to spare, so a
            // region never collapses to nothing at the edge. Knights standing
            // on top of each other would divide by nothing and fling the sites
            // to infinity, so the scale is capped rather than trusted.
            var k = reach > 0.01f ? Mathf.Min(0.35f / reach, 1000f) : 0f;
            foreach (var p in panes)
            {
                var o = (p.Centre - centre) * k;
                Sites.Add(new Vector2((0.5f + o.x) * _aspect, 0.5f + o.y));
            }

            if (panes.Count == 2)
            {
                var along = Sites[1] - Sites[0];
                Normal = along.sqrMagnitude > 0.0001f ? along.normalized : Vector2.right;
            }
        }

        private static void EnsureTargets(int count)
        {
            var w = Mathf.Max(Screen.width, 2);
            var h = Mathf.Max(Screen.height, 2);
            if (Targets.Count == count && _width == w && _height == h) return;

            foreach (var rt in Targets)
            {
                if (rt == null) continue;
                rt.Release();
                Object.Destroy(rt);
            }
            Targets.Clear();
            for (var i = 0; i < count; i++)
            {
                var rt = new RenderTexture(w, h, 24) { name = "HKCoop Pane " + i };
                rt.Create();
                Targets.Add(rt);
            }
            _width = w;
            _height = h;
        }

        /// <summary>
        /// Sit just under the HUD, every frame.
        ///
        /// This camera CLEARS the screen before painting, so ordering it after
        /// the HUD camera wipes the HUD. Decided per frame rather than once at
        /// creation: the HUD camera may not exist yet the first time this runs,
        /// and a fallback guess that lands above it is a HUD that silently
        /// disappears.
        /// </summary>
        private static void SyncDepth()
        {
            if (_painterCam == null) return;
            var hud = GameCameras.instance != null ? GameCameras.instance.hudCamera : null;
            var want = hud != null ? hud.depth - 1f : 50f;
            if (!Mathf.Approximately(_painterCam.depth, want)) _painterCam.depth = want;
        }

        private static void EnsurePainter()
        {
            if (_painter != null) return;
            _holder = new GameObject("HKCouchCoop Compositor");
            Object.DontDestroyOnLoad(_holder);
            var cam = _holder.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.cullingMask = 0;                 // draws nothing of its own

            _painterCam = cam;
            SyncDepth();
            cam.useOcclusionCulling = false;
            _painter = _holder.AddComponent<Painter>();
            _blit = new Material(Shader()) { hideFlags = HideFlags.HideAndDontSave };
        }

        private static readonly ThrottledLog _passLog =
            new ThrottledLog(10, m => Plugin.Log.LogError(m));

        /// <summary>Paints the screen from the pane textures, once per frame.</summary>
        private sealed class Painter : MonoBehaviour
        {
            private void OnPostRender() => Guard.Run(() =>
            {
                if (!Active || Targets.Count < 2 || _blit == null) return;

                for (var i = 0; i < Targets.Count && i < Sites.Count; i++)
                    DrawCell(Targets[i], i);
            }, "Split composite");

            /// <summary>
            /// Paint one texture over the screen it owns.
            ///
            /// A Knight owns everywhere closer to them than to any other
            /// Knight — a Voronoi cell — which is the screen clipped by one
            /// perpendicular bisector per rival. Two Knights give a single
            /// straight divider at whatever angle they stand; three or four
            /// give wedges that pivot as they move. The result is always
            /// convex, so a triangle fan draws it exactly.
            /// </summary>
            private static void DrawCell(Texture tex, int index)
            {
                var poly = new List<Vector2>
                {
                    new Vector2(0f, 0f), new Vector2(_aspect, 0f),
                    new Vector2(_aspect, 1f), new Vector2(0f, 1f),
                };
                for (var j = 0; j < Sites.Count; j++)
                {
                    if (j == index) continue;
                    var a = Sites[index];
                    var b = Sites[j];
                    var normal = b - a;
                    if (normal.sqrMagnitude < 1e-6f) continue;
                    poly = Clip(poly, (a + b) * 0.5f, normal.normalized);
                    if (poly.Count < 3) return;
                }
                if (poly.Count < 3) return;

                _blit.mainTexture = tex;

                // Unity's own examples set the pass INSIDE the matrix block —
                // PushMatrix, SetPass, LoadOrtho — and this had it outside and
                // before. Painting flat white still came out black, which ruled
                // out the textures and every camera upstream and left the draw
                // itself, so the ordering is the thing to correct.
                GL.PushMatrix();
                var ok = _blit.SetPass(0);
                GL.LoadOrtho();
                if (!ok)
                {
                    // SetPass returning false means the shader has no usable
                    // pass — nothing will draw, and it says so once rather
                    // than painting black forever in silence.
                    _passLog.Log(Time.unscaledTimeAsDouble,
                        "Split compositor: SetPass failed on shader '"
                        + (_blit.shader != null ? _blit.shader.name : "null")
                        + "' — the rotating split cannot paint.");
                    GL.PopMatrix();
                    return;
                }
                GL.Begin(GL.TRIANGLES);
                for (var i = 1; i < poly.Count - 1; i++)
                {
                    // Both windings. Unlit/Texture culls back faces, and under
                    // GL.LoadOrtho the fan came out facing away — so every
                    // triangle was discarded and the screen stayed black while
                    // everything upstream measured healthy: the paint ran, the
                    // texture was real, the polygon had four corners and
                    // SetPass succeeded. Proven by putting this camera above
                    // the HUD, where its clear covered the HUD (so it does
                    // reach the screen) and the fill still drew nothing.
                    //
                    // Emitting the reverse as well costs a handful of
                    // triangles and makes the fill independent of which way
                    // the projection winds.
                    Emit(poly[0]);
                    Emit(poly[i]);
                    Emit(poly[i + 1]);

                    Emit(poly[0]);
                    Emit(poly[i + 1]);
                    Emit(poly[i]);
                }
                GL.End();
                GL.PopMatrix();
            }

            // The texture is a full-screen render, so screen position and
            // texture coordinate are the same number.
            private static void Emit(Vector2 p)
            {
                // Back out of the aspect correction the regions were built in.
                var x = p.x / Mathf.Max(_aspect, 0.0001f);
                GL.TexCoord2(x, p.y);
                GL.Vertex3(x, p.y, 0f);
            }

            /// <summary>Sutherland-Hodgman against a single half-plane.</summary>
            private static List<Vector2> Clip(List<Vector2> input, Vector2 through, Vector2 normal)
            {
                var output = new List<Vector2>();
                for (var i = 0; i < input.Count; i++)
                {
                    var cur = input[i];
                    var nxt = input[(i + 1) % input.Count];
                    var dCur = Vector2.Dot(cur - through, normal);
                    var dNxt = Vector2.Dot(nxt - through, normal);

                    if (dCur <= 0f) output.Add(cur);
                    if ((dCur <= 0f) != (dNxt <= 0f))
                    {
                        var t = dCur / (dCur - dNxt);
                        output.Add(Vector2.Lerp(cur, nxt, t));
                    }
                }
                return output;
            }
        }
    }
}
