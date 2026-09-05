using UnityEngine;
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

namespace HKCouchCoop
{
    /// <summary>
    /// Friendly fire: let a Knight's nail hurt another Knight.
    ///
    /// This cannot happen by accident, which is worth knowing before reading
    /// the patch. A nail's DamageEnemies collects colliders in
    /// OnTriggerEnter2D and explicitly drops layer 9 — the hero layer — so a
    /// Knight is never even recorded as a target. And the damage it would deal
    /// goes through HitTaker to an IHitResponder, which heroes do not have:
    /// they take damage through HeroController.TakeDamage instead. Two
    /// independent reasons a swing passes straight through a teammate.
    ///
    /// So the hit is dealt here, directly, on entry rather than every
    /// FixedUpdate — a swing should cost one mask, not one per frame of
    /// overlap. It routes through the victim's own TakeDamage, which means the
    /// existing pool swap puts it on THEIR health, their invulnerability
    /// frames apply, and their death runs their own death: an extra leaves a
    /// Shade for the others to fight, and player one dying is the real
    /// game-over it has always been, because player one is the save file.
    /// </summary>
    [HarmonyPatch(typeof(DamageEnemies), "FixedUpdate")]
    internal static class FriendlyFirePatch
    {
        // Where the chain breaks, published rather than guessed at.
        internal static int Ticks;       // this patch ran on a Knight's nail
        internal static int Overlaps;    // the nail was touching another Knight
        internal static int Hits;        // damage actually dealt

        // One hit per victim per swing, not one per physics step: vanilla
        // re-damages every FixedUpdate while a target overlaps, which against
        // a teammate standing on you is a mask per frame.
        //
        // Keyed by the individual damage box, NOT one shared set. Every
        // DamageEnemies in the game shares this patch — other Knights' nails,
        // and enemies' too, since KnightHatchling and StalactiteControl carry
        // one — so a single set cleared on any disable would let one player's
        // swing ending re-arm another player's swing against the same victim,
        // and a falling stalactite could do it from across the room.
        private static readonly Dictionary<int, HashSet<int>> HitThisSwing =
            new Dictionary<int, HashSet<int>>();

        private static HashSet<int> VictimsOf(int nailId)
        {
            HashSet<int> set;
            if (!HitThisSwing.TryGetValue(nailId, out set))
            {
                set = new HashSet<int>();
                HitThisSwing[nailId] = set;
            }
            return set;
        }

        /// <summary>This damage box was put away; its swing is over.</summary>
        internal static void ClearSwing(int nailId) => HitThisSwing.Remove(nailId);

        private static void Postfix(DamageEnemies __instance) => Guard.Run(() =>
        {
            if (!Plugin.Cfg.FriendlyFire.Value || !CoopManager.Active) return;
            if (__instance == null || !__instance.isActiveAndEnabled) return;

            var attacker = __instance.GetComponentInParent<HeroController>();
            if (attacker == null) return;          // not a Knight's attack
            var damage = __instance.damageDealt;
            if (damage <= 0) return;

            var nail = __instance.GetComponent<Collider2D>();
            if (nail == null || !nail.enabled) return;
            Ticks++;

            var alreadyHit = VictimsOf(__instance.GetInstanceID());
            foreach (var victim in CoopManager.AllHeroes)
            {
                if (victim == null || ReferenceEquals(victim, attacker)) continue;
                var id = victim.GetInstanceID();
                if (alreadyHit.Contains(id)) continue;

                // Collider2D.Distance, NOT IsTouching or a trigger callback.
                // Trigger events and IsTouching both go through the physics
                // layer matrix, and nail-versus-hero is disabled there — which
                // is exactly why a swing passes through a teammate and why
                // hanging this on OnTriggerEnter2D produced no hits at all.
                // Distance measures the geometry whatever the matrix says.
                var touching = false;
                foreach (var vc in victim.GetComponents<Collider2D>())
                {
                    if (vc == null || !vc.enabled) continue;
                    if (nail.Distance(vc).isOverlapped) { touching = true; break; }
                }
                if (!touching) continue;
                Overlaps++;

                var side = attacker.transform.position.x <= victim.transform.position.x
                    ? GlobalEnums.CollisionSide.left
                    : GlobalEnums.CollisionSide.right;

                // Their TakeDamage, so the pool swap puts it on THEIR health,
                // their invulnerability applies, and their death is their own.
                victim.TakeDamage(__instance.gameObject, side, damage, hazardType: 0);
                alreadyHit.Add(id);
                Hits++;
            }
        }, "Friendly fire");
    }

    /// <summary>A swing ends when the nail's damage box is put away.</summary>
    [HarmonyPatch(typeof(DamageEnemies), "OnDisable")]
    internal static class FriendlyFireSwingEndPatch
    {
        private static void Postfix(DamageEnemies __instance) => Guard.Run(() =>
        {
            if (__instance != null) FriendlyFirePatch.ClearSwing(__instance.GetInstanceID());
        }, "Friendly fire swing end");
    }
}
