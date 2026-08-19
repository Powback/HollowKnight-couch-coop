using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace HKCouchCoop
{
    /// <summary>
    /// Health display for extra players: rows of the game's OWN mask sprites,
    /// mirrored to the top-right of the HUD — the reflection of player one's
    /// row in the top-left. Each row is tinted with its player's color cast;
    /// lifeblood masks show in lifeblood blue.
    ///
    /// Built by cloning the vanilla "Health N" objects from the HUD canvas and
    /// stripping their PlayMaker FSMs — the sprites, material and sizing are
    /// the real HUD's, only the *driving* is ours (from each player's pool).
    /// If the canvas layout ever changes shape, the builder logs a one-time
    /// hierarchy dump so the pattern can be corrected, and shows nothing
    /// rather than something wrong.
    /// </summary>
    internal static class CoopHealthHud
    {
        private static readonly Color Lifeblood = new Color(0.5f, 0.83f, 1f);

        private sealed class RowUi
        {
            internal CoopPlayer Player;
            internal GameObject Root;
            internal readonly List<tk2dSprite> Masks = new List<tk2dSprite>();
            internal int ShownHealth = -1, ShownBlue = -1, ShownDowned = -1;
        }

        private static readonly List<RowUi> Rows = new List<RowUi>();
        private static bool _dumped;
        private static bool _broken;

        internal static void Invalidate()
        {
            foreach (var row in Rows)
                if (row.Root != null) Object.Destroy(row.Root);
            Rows.Clear();
            _broken = false;
        }

        internal static void Tick()
        {
            if (_broken) return;

            // In shared-health mode the per-player pools are unused — showing
            // them would display stale numbers as if they were real.
            if (!CoopManager.Active || !Plugin.Cfg.IndependentHealth.Value)
            {
                if (Rows.Count > 0) Invalidate();
                return;
            }

            var players = CoopManager.ExtraPlayers.ToList();

            // Rebuild when the roster or max health changed shape.
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

        private static void Build(List<CoopPlayer> players, int slots)
        {
            var cameras = GameCameras.instance;
            if (cameras == null || cameras.hudCanvas == null) return;

            // The vanilla mask row: objects named "Health 1".."Health 11".
            var pattern = new Regex(@"^Health \d+$");
            var vanilla = cameras.hudCanvas.GetComponentsInChildren<Transform>(includeInactive: true)
                .Where(t => pattern.IsMatch(t.name) && t.GetComponent<tk2dSprite>() != null)
                .OrderBy(t => int.Parse(t.name.Substring(7)))
                .ToList();

            if (vanilla.Count < 2)
            {
                DumpOnce(cameras.hudCanvas);
                _broken = true;   // stay silent rather than draw something wrong
                return;
            }

            var first = vanilla[0];
            var step = vanilla[1].localPosition - vanilla[0].localPosition;
            var parent = first.parent;

            // Mirror across the canvas midline: the canvas is centered, so the
            // reflected row starts at -x and runs the opposite direction.
            var rowStart = new Vector3(-first.localPosition.x, first.localPosition.y, first.localPosition.z);
            var rowStep = new Vector3(-step.x, step.y, step.z);

            for (var p = 0; p < players.Count; p++)
            {
                var row = new RowUi { Player = players[p] };
                row.Root = new GameObject($"HKCouchCoop_Health_P{players[p].Number}");
                // Inactive while building: a clone of a vanilla mask carries a
                // live PlayMaker FSM whose Awake would run before a deferred
                // Destroy lands. Under an inactive parent nothing wakes, and
                // DestroyImmediate removes the drivers before activation.
                row.Root.SetActive(false);
                row.Root.transform.SetParent(parent, worldPositionStays: false);
                row.Root.transform.localPosition = Vector3.zero;
                row.Root.transform.localScale = Vector3.one;

                // Stack additional players' rows downward.
                var rowOffset = new Vector3(0f, -(step.magnitude * 1.1f) * p, 0f);

                for (var i = 0; i < slots; i++)
                {
                    var mask = Object.Instantiate(first.gameObject, row.Root.transform, worldPositionStays: false);
                    mask.name = $"Mask {i + 1}";
                    mask.transform.localPosition = rowStart + rowStep * i + rowOffset;
                    mask.transform.localScale = first.localScale;

                    // The real sprite, minus the vanilla driver.
                    foreach (var fsm in mask.GetComponentsInChildren<PlayMakerFSM>(true))
                        Object.DestroyImmediate(fsm);
                    foreach (var anim in mask.GetComponentsInChildren<tk2dSpriteAnimator>(true))
                        Object.DestroyImmediate(anim);

                    var sprite = mask.GetComponent<tk2dSprite>();
                    row.Masks.Add(sprite);
                    mask.SetActive(true);
                }

                row.Root.SetActive(true);
                Rows.Add(row);
            }

            Plugin.Log.LogInfo($"Built mirrored health rows for {players.Count} extra player(s).");
        }

        private static void Refresh(RowUi row)
        {
            var p = row.Player;
            var downed = p.Downed ? 1 : 0;
            if (p.Health == row.ShownHealth && p.HealthBlue == row.ShownBlue && downed == row.ShownDowned)
                return;

            row.ShownHealth = p.Health;
            row.ShownBlue = p.HealthBlue;
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
        }

        /// <summary>One-time layout dump so a changed HUD can be adapted next session.</summary>
        private static void DumpOnce(GameObject hudCanvas)
        {
            if (_dumped) return;
            _dumped = true;

            var sb = new StringBuilder("HUD canvas layout (health row not found — adapt CoopHealthHud):\n");
            void Walk(Transform t, int depth)
            {
                if (depth > 3) return;
                sb.Append(new string(' ', depth * 2)).Append(t.name)
                  .Append(t.GetComponent<tk2dSprite>() != null ? "  [tk2dSprite]" : "")
                  .Append('\n');
                foreach (Transform c in t) Walk(c, depth + 1);
            }
            Walk(hudCanvas.transform, 0);
            Plugin.Log.LogInfo(sb.ToString());
        }
    }
}
