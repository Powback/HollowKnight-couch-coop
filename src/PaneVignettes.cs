using System.Collections.Generic;
using UnityEngine;

namespace HKCouchCoop
{
    /// <summary>
    /// One darkness overlay per pane, each visible only in its own pass.
    ///
    /// Hollow Knight's "vignette" is not a lamp — it is a black overlay with a
    /// hole cut around the hero, and only player one carries one. On a single
    /// shared screen that is exactly right. Split across panes it is the whole
    /// problem: every pane camera renders the entire scene, picks up player
    /// one's overlay, and any pane not looking at player one lies inside the
    /// blackened part. Player two's pane measured 0.0 mean brightness with a
    /// single unique colour — not dim, blank.
    ///
    /// Two approaches were tried and rejected against the running game:
    ///   * Switching the clones' overlays on. Each pane then sees ALL of them
    ///     and they darken each other's holes; the screen went from 46%
    ///     brightness to 9%.
    ///   * Giving each overlay its own layer and culling the others per pane.
    ///     Hollow Knight names all 24 user layers, so there are none spare.
    ///
    /// What works needs neither: panes are separate cameras with separate
    /// passes, so the overlays can simply be switched around between passes.
    /// Before each pane renders, exactly one overlay — that pane's Knight —
    /// is left visible.
    /// </summary>
    internal static class PaneVignettes
    {
        private static readonly List<Camera> _cams = new List<Camera>();
        private static readonly List<List<HeroController>> _owners =
            new List<List<HeroController>>();

        private static readonly List<SpriteRenderer> _all = new List<SpriteRenderer>();
        private static readonly List<GameObject> _weEnabled = new List<GameObject>();

        private static bool _hooked;
        private static bool _bound;

        /// <summary>
        /// Tell the hook which camera draws which Knights. Called every frame
        /// while split; cheap, and the lists are rebuilt rather than diffed so
        /// a pane changing hands cannot leave a stale pairing behind.
        /// </summary>
        internal static void Bind(IReadOnlyList<Camera> cams, IReadOnlyList<Pane> panes)
        {
            if (cams == null || panes == null) return;

            _cams.Clear();
            _owners.Clear();
            _all.Clear();

            for (var i = 0; i < panes.Count && i < cams.Count; i++)
            {
                var cam = cams[i];
                if (cam == null) continue;

                var mine = new List<HeroController>();
                foreach (var hero in panes[i].Knights)
                {
                    if (hero == null || hero.vignette == null) continue;
                    mine.Add(hero);

                    // A clone's overlay is switched off at spawn. It has to be
                    // present to be shown in its own pane, so switch it on and
                    // remember that we were the ones who did.
                    var go = hero.vignette.gameObject;
                    if (!go.activeSelf)
                    {
                        go.SetActive(true);
                        if (!_weEnabled.Contains(go)) _weEnabled.Add(go);
                    }
                    _all.Add(hero.vignette);
                }

                _cams.Add(cam);
                _owners.Add(mine);
            }

            if (!_hooked)
            {
                Camera.onPreCull += BeforeCamera;
                _hooked = true;
            }
            _bound = true;
        }

        /// <summary>
        /// Runs before every camera in the scene. For a pane camera, show that
        /// pane's overlay and hide the rest; for anything else — the game's own
        /// camera, the HUD — leave the overlays alone.
        /// </summary>
        private static void BeforeCamera(Camera cam)
        {
            if (!_bound || cam == null) return;

            var pane = _cams.IndexOf(cam);
            if (pane < 0) return;

            var mine = _owners[pane];
            foreach (var v in _all)
            {
                if (v == null) continue;
                var owned = false;
                foreach (var hero in mine)
                {
                    if (hero != null && hero.vignette == v) { owned = true; break; }
                }
                v.enabled = owned;
            }
        }

        /// <summary>
        /// Hand the overlays back: every one visible again, and the clones'
        /// switched off, so the single-screen behaviour is exactly as before.
        /// </summary>
        internal static void Release()
        {
            if (!_bound) return;
            _bound = false;

            if (_hooked)
            {
                Camera.onPreCull -= BeforeCamera;
                _hooked = false;
            }

            foreach (var v in _all)
                if (v != null) v.enabled = true;

            foreach (var go in _weEnabled)
                if (go != null) go.SetActive(false);

            _weEnabled.Clear();
            _cams.Clear();
            _owners.Clear();
            _all.Clear();
        }
    }
}
