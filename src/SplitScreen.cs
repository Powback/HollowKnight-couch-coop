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
                .Add("paneCount", _panes.Count);
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
