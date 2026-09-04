using System.Collections;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace HKCouchCoop
{
    /// <summary>
    /// Death routing. The death coroutine runs on the Knight that died, and
    /// everything downstream (GameManager.PlayerDead, the respawn reload)
    /// assumes that Knight is THE hero — so a clone must never run it.
    ///
    /// With independent health (default): an extra player's death downs THEM —
    /// real death burst, the real Shade rises where they fell, teammates fight
    /// it to revive them. Player one's death is still a real death (they are
    /// the save file); extras despawn first so the game-over runs pure vanilla.
    ///
    /// With shared health (IndependentHealth=false): any death is the team's —
    /// extras despawn and the death is re-dispatched to player one.
    /// </summary>
    internal static class DeathPatches
    {
        private static IEnumerator Nothing() { yield break; }

        private static void RedispatchToP1()
        {
            // Ground truth, not the singleton: this runs from a death path
            // that an extra's FSM may be masquerading through, and invoking
            // Die on the wrong Knight would run the save-file game-over
            // against a clone.
            var p1 = CoopManager.PlayerOne;
            if (p1 == null) return;
            var die = AccessTools.Method(typeof(HeroController), "Die");
            if (die?.Invoke(p1, null) is IEnumerator routine)
                p1.StartCoroutine(routine);
        }

        /// <summary>Shared handling for both death entry points.</summary>
        private static bool HandleDeath(HeroController instance, ref IEnumerator result, bool hazard)
        {
            if (!CoopManager.Active) return true;

            var player = CoopManager.FindExtra(instance);
            if (player == null)
            {
                // Player one dying: clear the extras so the game-over is vanilla.
                Plugin.Log.LogInfo("Player one died — ending co-op session for a clean game-over.");
                CoopManager.DespawnAll();
                return true;
            }

            result = Nothing();

            if (!Plugin.Cfg.IndependentHealth.Value)
            {
                // Shared pool: the team died, via whichever body was hit.
                Plugin.Log.LogInfo("Team death via extra player — handing the death to player one.");
                CoopManager.DespawnAll();
                RedispatchToP1();
                return false;
            }

            // Independent health: only this player goes down.
            if (Plugin.Cfg.ShadeRevive.Value)
            {
                // A hazard death means the body is inside spikes or a pit —
                // the shade rises beside the nearest living Knight instead.
                Vector3? anchor = null;
                if (hazard)
                {
                    var nearest = CoopManager.AllHeroes
                        .OrderBy(h => (h.transform.position - instance.transform.position).sqrMagnitude)
                        .FirstOrDefault(h => !ReferenceEquals(h, instance));
                    if (nearest != null) anchor = nearest.transform.position + new Vector3(1.5f, 0.5f, 0f);
                }
                ShadeRevive.Down(player, anchor);
            }
            else
            {
                // Down-and-out mode: they simply leave and can rejoin with Start.
                Plugin.Log.LogInfo($"Player {player.Number} died — out until they rejoin.");
                NativeHud.Notify($"Player {player.Number} is down — press Start to rejoin");
                CoopManager.LeaveByDevice(player.Input?.Device);
            }
            return false;
        }

        [HarmonyPatch(typeof(HeroController), "Die")]
        internal static class DiePatch
        {
            private static bool Prefix(HeroController __instance, ref IEnumerator __result)
            {
                var r = __result;
                var runVanilla = Guard.Run(() => HandleDeath(__instance, ref r, hazard: false), true, "Death");
                __result = r;
                return runVanilla;
            }
        }

        [HarmonyPatch(typeof(HeroController), "DieFromHazard")]
        internal static class DieFromHazardPatch
        {
            private static bool Prefix(HeroController __instance, ref IEnumerator __result)
            {
                var r = __result;
                var runVanilla = Guard.Run(() => HandleDeath(__instance, ref r, hazard: true), true, "HazardDeath");
                __result = r;
                return runVanilla;
            }
        }
    }
}
