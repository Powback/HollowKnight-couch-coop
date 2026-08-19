using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace HKCouchCoop
{
    /// <summary>
    /// Health and soul for extra players, mirrored top-right as the reflection
    /// of player one's top-left row — built entirely from the game's own HUD
    /// objects: cloned "Health N" mask sprites and a cloned soul orb per row,
    /// with their PlayMaker/animator drivers stripped and our pools driving
    /// them instead.
    ///
    /// Geometry never assumes a canvas layout: positions are reflected through
    /// the HUD canvas's measured rect (or its rendered bounds), so "top-right"
    /// is computed from where the real row actually sits. If any structure
    /// lookup misses, the display hides itself and logs a one-time hierarchy
    /// dump — never draws something wrong.
    /// </summary>
    internal static class CoopHealthHud
    {
        private static readonly Color Lifeblood = new Color(0.5f, 0.83f, 1f);

        private sealed class RowUi
        {
            internal CoopPlayer Player;
            internal GameObject Root;
            internal readonly List<tk2dSprite> Masks = new List<tk2dSprite>();
            internal tk2dSprite Orb;
            internal int ShownHealth = -1, ShownBlue = -1, ShownSoul = -1, ShownDowned = -1;
        }

        private sealed class OrbTemplate
        {
            internal tk2dSpriteCollectionData Collection;
            internal readonly List<int> FillSpriteIds = new List<int>();
        }

        private static readonly List<RowUi> Rows = new List<RowUi>();
        private static OrbTemplate _orb;
        private static bool _dumped;
        private static bool _broken;

        internal static void Invalidate()
        {
            foreach (var row in Rows)
                if (row.Root != null) Object.Destroy(row.Root);
            Rows.Clear();
            _orb = null;
            _broken = false;
        }

        internal static void Tick()
        {
            if (_broken) return;

            if (!CoopManager.Active || !Plugin.Cfg.IndependentHealth.Value)
            {
                if (Rows.Count > 0) Invalidate();
                return;
            }

            var players = CoopManager.ExtraPlayers.ToList();
            var pd = PlayerData.instance;
            var slots = pd != null ? pd.CurrentMaxHealth + 2 : 7;   // +2 lifeblood headroom

            if (Rows.Count != players.Count
                || Rows.Any(r => r.Root == null)
                || (Rows.Count > 0 && Rows[0].Masks.Count != slots))
            {
                Invalidate();
                Build(players, slots);
            }

            foreach (var row in Rows) Refresh(row);
        }

        /// <summary>Reflect a world position through the HUD's horizontal center.</summary>
        private static Vector3 MirrorWorld(Transform canvas, Vector3 world)
        {
            var local = canvas.InverseTransformPoint(world);

            float centerX = 0f;
            var rect = canvas.GetComponent<RectTransform>();
            if (rect != null) centerX = rect.rect.center.x;

            local.x = 2f * centerX - local.x;
            return canvas.TransformPoint(local);
        }

        private static void Build(List<CoopPlayer> players, int slots)
        {
            var cameras = GameCameras.instance;
            if (cameras == null || cameras.hudCanvas == null) return;
            var canvas = cameras.hudCanvas.transform;

            var pattern = new Regex(@"^Health \d+$");
            var vanilla = cameras.hudCanvas.GetComponentsInChildren<Transform>(includeInactive: true)
                .Where(t => pattern.IsMatch(t.name) && t.GetComponent<tk2dSprite>() != null)
                .OrderBy(t => int.Parse(t.name.Substring(7)))
                .ToList();

            if (vanilla.Count < 2)
            {
                DumpOnce(cameras.hudCanvas);
                _broken = true;
                return;
            }

            var first = vanilla[0];
            var stepWorld = vanilla[1].position - vanilla[0].position;

            // Row height from what the mask actually measures on screen.
            var firstSprite = first.GetComponent<tk2dSprite>();
            var maskBounds = firstSprite.GetBounds();
            var rowDrop = new Vector3(0f, -(maskBounds.size.y * 1.35f), 0f);

            // The vanilla soul orb, for per-row cloning. Optional: rows work
            // without it if the orb's structure defies identification.
            var orbSource = PrepareOrbTemplate(cameras);

            for (var p = 0; p < players.Count; p++)
            {
                var row = new RowUi { Player = players[p] };
                row.Root = new GameObject($"HKCouchCoop_Health_P{players[p].Number}");
                row.Root.SetActive(false);   // build cold: no cloned Awake may run
                row.Root.transform.SetParent(first.parent, worldPositionStays: false);

                var rowOffset = rowDrop * p;

                for (var i = 0; i < slots; i++)
                {
                    // Mirror each vanilla slot position so spacing and scale are
                    // exactly the real row's, reflected.
                    var srcWorld = first.position + stepWorld * i;
                    var dstWorld = MirrorWorld(canvas, srcWorld) + rowOffset;

                    var mask = Object.Instantiate(first.gameObject, row.Root.transform, worldPositionStays: true);
                    mask.name = $"Mask {i + 1}";
                    mask.transform.position = dstWorld;

                    foreach (var fsm in mask.GetComponentsInChildren<PlayMakerFSM>(true))
                        Object.DestroyImmediate(fsm);
                    foreach (var anim in mask.GetComponentsInChildren<tk2dSpriteAnimator>(true))
                        Object.DestroyImmediate(anim);

                    row.Masks.Add(mask.GetComponent<tk2dSprite>());
                    mask.SetActive(true);
                }

                if (orbSource != null && _orb != null)
                {
                    var orbWorld = MirrorWorld(canvas, orbSource.transform.position) + rowOffset;
                    var orb = Object.Instantiate(orbSource, row.Root.transform, worldPositionStays: true);
                    orb.name = "Soul Orb";
                    orb.transform.position = orbWorld;
                    foreach (var fsm in orb.GetComponentsInChildren<PlayMakerFSM>(true))
                        Object.DestroyImmediate(fsm);
                    foreach (var anim in orb.GetComponentsInChildren<tk2dSpriteAnimator>(true))
                        Object.DestroyImmediate(anim);
                    foreach (var audio in orb.GetComponentsInChildren<AudioSource>(true))
                        Object.DestroyImmediate(audio);
                    row.Orb = orb.GetComponent<tk2dSprite>();
                    orb.SetActive(true);
                }

                row.Root.SetActive(true);
                Rows.Add(row);
            }

            Plugin.Log.LogInfo(
                $"Built mirrored HUD for {players.Count} extra player(s)"
                + (_orb != null ? $" with soul orbs ({_orb.FillSpriteIds.Count} fill frames)." : " (no soul orb — masks only)."));
        }

        /// <summary>
        /// Identify the soul orb's fill animation so cloned orbs can show soul
        /// levels: the animator clip with the most frames on the orb object is
        /// its fill sequence. Self-disables (masks-only) when unidentifiable.
        /// </summary>
        private static GameObject PrepareOrbTemplate(GameCameras cameras)
        {
            var orbFsm = cameras.soulOrbFSM;
            if (orbFsm == null) return null;
            var go = orbFsm.gameObject;

            var animators = go.GetComponentsInChildren<tk2dSpriteAnimator>(true);
            tk2dSpriteAnimationClip best = null;
            foreach (var a in animators)
            {
                if (a.Library == null || a.Library.clips == null) continue;
                foreach (var clip in a.Library.clips)
                {
                    if (clip == null || clip.frames == null || clip.frames.Length < 3) continue;
                    if (best == null || clip.frames.Length > best.frames.Length) best = clip;
                }
            }

            if (best == null)
            {
                // No identifiable fill sequence: rows show masks only (_orb stays
                // null, so Build clones no orb). A static orb would lie about soul.
                Plugin.Log.LogInfo("Soul orb fill clip not identified — extra rows show masks only.");
                return go;
            }

            _orb = new OrbTemplate { Collection = best.frames[0].spriteCollection };
            foreach (var f in best.frames) _orb.FillSpriteIds.Add(f.spriteId);
            return go;
        }

        private static void Refresh(RowUi row)
        {
            var p = row.Player;
            var pd = PlayerData.instance;
            var downed = p.Downed ? 1 : 0;
            if (p.Health == row.ShownHealth && p.HealthBlue == row.ShownBlue
                && p.Soul == row.ShownSoul && downed == row.ShownDowned)
                return;

            row.ShownHealth = p.Health;
            row.ShownBlue = p.HealthBlue;
            row.ShownSoul = p.Soul;
            row.ShownDowned = downed;

            var tint = CoopManager.TintFor(p.Number);

            for (var i = 0; i < row.Masks.Count; i++)
            {
                var sprite = row.Masks[i];
                if (sprite == null) continue;
                bool normal = i < p.Health;
                bool blue = !normal && i < p.Health + p.HealthBlue;
                sprite.gameObject.SetActive(!p.Downed && (normal || blue));
                if (normal) sprite.color = tint;
                else if (blue) sprite.color = Lifeblood;
            }

            if (row.Orb != null)
            {
                row.Orb.gameObject.SetActive(!p.Downed && _orb != null);
                if (_orb != null && !p.Downed)
                {
                    var max = pd != null && pd.maxMP > 0 ? pd.maxMP : 99;
                    var frac = Mathf.Clamp01(p.Soul / (float)max);
                    var idx = Mathf.RoundToInt(frac * (_orb.FillSpriteIds.Count - 1));
                    row.Orb.SetSprite(_orb.Collection, _orb.FillSpriteIds[idx]);
                }
            }
        }

        private static void DumpOnce(GameObject hudCanvas)
        {
            if (_dumped) return;
            _dumped = true;
            var sb = new StringBuilder("HUD canvas layout (health row not found — adapt CoopHealthHud):\n");
            void Walk(Transform t, int depth)
            {
                if (depth > 3) return;
                sb.Append(new string(' ', depth * 2)).Append(t.name)
                  .Append(t.GetComponent<tk2dSprite>() != null ? "  [tk2dSprite]" : "").Append('\n');
                foreach (Transform c in t) Walk(c, depth + 1);
            }
            Walk(hudCanvas.transform, 0);
            Plugin.Log.LogInfo(sb.ToString());
        }
    }
}
