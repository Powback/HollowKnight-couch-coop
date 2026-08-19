using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace HKCouchCoop
{
    /// <summary>
    /// Independent health for extra players.
    ///
    /// All of the game's health logic — damage math, Joni's Blessing, Fragile
    /// Heart, lifeblood masks, the death check — runs inside HeroController
    /// *instance* methods and reads/writes `playerData.health`/`healthBlue`.
    /// Rather than reimplementing any of it, each extra Knight carries a
    /// private pool, and for the duration of that Knight's own health-touching
    /// calls the shared PlayerData fields are swapped to its pool and back.
    /// Vanilla code computes against the right numbers without knowing.
    ///
    /// The swap is restored in a Finalizer so an exception inside vanilla code
    /// can never leave player one wearing a clone's health.
    /// </summary>
    internal static class HealthPool
    {
        internal sealed class Swap
        {
            internal int P1Health;
            internal int P1Blue;
            internal int P1Soul;
            internal int P1Reserve;
            internal CoopPlayer Player;
        }

        internal static Swap Begin(HeroController hc)
        {
            if (!Plugin.Cfg.IndependentHealth.Value) return null;

            var player = CoopManager.FindExtra(hc);
            if (player == null) return null;

            var pd = PlayerData.instance;
            if (pd == null) return null;

            var swap = new Swap
            {
                P1Health = pd.health, P1Blue = pd.healthBlue,
                P1Soul = pd.MPCharge, P1Reserve = pd.MPReserve,
                Player = player,
            };
            pd.health = player.Health;
            pd.healthBlue = player.HealthBlue;
            pd.MPCharge = player.Soul;
            pd.MPReserve = player.SoulReserve;
            return swap;
        }

        internal static void End(Swap swap)
        {
            if (swap == null) return;
            var pd = PlayerData.instance;
            if (pd == null) return;

            // Keep what vanilla computed for the clone, give player one back theirs.
            swap.Player.Health = pd.health;
            swap.Player.HealthBlue = pd.healthBlue;
            swap.Player.Soul = pd.MPCharge;
            swap.Player.SoulReserve = pd.MPReserve;
            pd.health = swap.P1Health;
            pd.healthBlue = swap.P1Blue;
            pd.MPCharge = swap.P1Soul;
            pd.MPReserve = swap.P1Reserve;
        }
    }

    [HarmonyPatch]
    internal static class HealthSwapPatches
    {
        private static readonly string[] Wrapped =
        {
            // Health
            "TakeDamage", "AddHealth", "TakeHealth", "MaxHealth", "MaxHealthKeepBlue",
            // Soul — every mutation and the focus gate run through these.
            "SoulGain", "AddMPCharge", "AddMPChargeSpa", "SetMPCharge",
            "TakeMP", "TakeMPQuick", "TakeReserveMP", "CanFocus",
        };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var name in Wrapped)
            {
                var m = AccessTools.Method(typeof(HeroController), name);
                if (m != null) yield return m;
                else Plugin.Log.LogWarning($"Health swap: HeroController.{name} not found; skipping.");
            }
        }

        private static void Prefix(HeroController __instance, out object __state)
            => __state = HealthPool.Begin(__instance);

        private static Exception Finalizer(Exception __exception, object __state)
        {
            HealthPool.End(__state as HealthPool.Swap);
            return __exception;
        }
    }
}
