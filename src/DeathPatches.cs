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
            if (player == null) return HandlePlayerOneDeath(instance, ref result, hazard);

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
                EndRunIfEveryoneIsDown();
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

        /// <summary>
        /// A downed party cannot revive itself: every shade needs someone alive
        /// to beat it, so the last Knight falling leaves everyone standing over
        /// shades forever. That is the "unless everyone dies" case — hand the
        /// real death to player one so the game does what it always would have.
        /// </summary>
        private static void EndRunIfEveryoneIsDown()
        {
            if (CoopManager.AnyoneElseStanding(null)) return;
            Plugin.Log.LogInfo("Every player is down — handing the real death to player one.");
            CoopManager.DespawnAll();
            RedispatchToP1();
        }

        /// <summary>
        /// Player one died. He goes down like anyone else if the party can
        /// still revive him; otherwise this is the real, save-writing death.
        /// </summary>
        private static bool HandlePlayerOneDeath(
            HeroController p1, ref IEnumerator result, bool hazard)
        {
            if (TryDownPlayerOne(p1, hazard)) { result = Nothing(); return false; }

            Plugin.Log.LogInfo("Player one died with no one left standing — vanilla game-over.");
            PlayerOneDown.ForceUp(p1);
            CoopManager.DespawnAll();
            return true;
        }

        /// <summary>
        /// Player one falls like anyone else while a teammate is still up: his
        /// shade rises where he died and beating it brings him back. He only
        /// takes the real, save-writing death when the whole party is down —
        /// otherwise one player's mistake would end everyone's run.
        ///
        /// Requires per-player health and shade revival; on a shared pool a
        /// death IS the team's death, and there is nobody left to do the
        /// reviving.
        /// </summary>
        private static bool TryDownPlayerOne(HeroController p1, bool hazard)
        {
            // Each decline says so. A silent false here becomes a full
            // vanilla game-over that ends everyone's run, and three separate
            // investigations were spent asking which of these it was — the
            // same trap HeroController.TakeDamage sets by refusing without a
            // word.
            if (!Plugin.Cfg.IndependentHealth.Value || !Plugin.Cfg.ShadeRevive.Value)
            {
                Plugin.Log.LogInfo(
                    "Player one's death runs vanilla: needs IndependentHealth "
                    + $"(is {Plugin.Cfg.IndependentHealth.Value}) and ShadeRevive "
                    + $"(is {Plugin.Cfg.ShadeRevive.Value}).");
                return false;
            }
            if (PlayerOneDown.Downed)
            {
                Plugin.Log.LogWarning(
                    "Player one died while already down — the previous down was "
                    + "never cleared, so this one runs vanilla.");
                return false;
            }

            var one = CoopManager.OneRecord;
            one.Hero = p1;
            if (!CoopManager.AnyoneElseStanding(one))
            {
                Plugin.Log.LogInfo(
                    $"Player one's death runs vanilla: nobody else is standing "
                    + $"({CoopManager.PlayerCount - 1} extra(s) on the roster).");
                return false;
            }

            // A hazard death leaves the body in spikes or a pit, so the shade
            // rises beside a living Knight instead of somewhere unreachable.
            Vector3? anchor = null;
            if (hazard)
            {
                var nearest = CoopManager.AllHeroes
                    .OrderBy(h => (h.transform.position - p1.transform.position).sqrMagnitude)
                    .FirstOrDefault(h => !ReferenceEquals(h, p1));
                if (nearest != null) anchor = nearest.transform.position + new Vector3(1.5f, 0.5f, 0f);
            }

            ShadeRevive.Down(one, anchor);
            return true;
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
