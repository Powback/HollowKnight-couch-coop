using System;
using System.Collections.Generic;
using System.Reflection;
using CoopKit;
using HarmonyLib;

namespace HKCouchCoop
{
    /// <summary>
    /// Independent health and soul for extra players, built on the kit's
    /// scoped pool swap: for the duration of a Knight's own state-touching
    /// calls, the shared PlayerData fields hold that Knight's pools, and what
    /// vanilla computed (damage math, Joni's Blessing, lifeblood, the focus
    /// gate — all of it) is written back to the pools afterwards. Restored in
    /// a Finalizer so an exception can never leave player one wearing a
    /// clone's numbers.
    /// </summary>
    internal static class HealthPool
    {
        private static readonly PoolSwap<CoopPlayer> Swap = new PoolSwap<CoopPlayer>()
            .Add(() => PlayerData.instance.health,     v => PlayerData.instance.health = v,
                 p => p.Health,      (p, v) => p.Health = v)
            .Add(() => PlayerData.instance.healthBlue, v => PlayerData.instance.healthBlue = v,
                 p => p.HealthBlue,  (p, v) => p.HealthBlue = v)
            .Add(() => PlayerData.instance.MPCharge,   v => PlayerData.instance.MPCharge = v,
                 p => p.Soul,        (p, v) => p.Soul = v)
            .Add(() => PlayerData.instance.MPReserve,  v => PlayerData.instance.MPReserve = v,
                 p => p.SoulReserve, (p, v) => p.SoulReserve = v);

        /// <summary>True while a pool swap is unclosed — at a frame boundary,
        /// a leak with the shared PlayerData holding a clone's numbers.</summary>
        internal static bool AnyOpen => Swap.AnyOpen;

        /// <summary>Give PlayerData its own values back. See Plugin's frame
        /// boundary check; mirrors ShadeRevive.ForceRestoreBank.</summary>
        internal static int ForceEndAll() => Swap.ForceEndAll();

        internal static PoolSwap<CoopPlayer>.Scope Begin(HeroController hc)
        {
            if (!Plugin.Cfg.IndependentHealth.Value) return null;
            if (PlayerData.instance == null) return null;
            return Swap.Begin(CoopManager.FindExtra(hc));
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

        private static void Prefix(HeroController __instance, out PoolSwap<CoopPlayer>.Scope __state)
            => __state = Guard.Run(() => HealthPool.Begin(__instance), "Health swap");

        private static Exception Finalizer(Exception __exception, PoolSwap<CoopPlayer>.Scope __state)
        {
            __state?.End();
            return __exception;
        }
    }
}
