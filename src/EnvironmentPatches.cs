using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CoopKit;
using HarmonyLib;
using UnityEngine;

namespace HKCouchCoop
{
    /// <summary>
    /// World physics responses that detect ANY Knight but act on THE Knight —
    /// the same trigger/actor decoupling as the wells, in the contact layer:
    ///
    ///  - TinkEffect: an extra tinks a spike wall → player one gets recoiled.
    ///  - CollisionEnterEvent (bounce shrooms and everything else subscribed
    ///    to it): an extra stomps a shroom → player one bounces.
    ///  - SoulOrb collection: an extra's orbs fly to them but feed player
    ///    one's soul.
    ///  - LineOfSightDetector: sight-gated enemies only ever "see" player one.
    ///
    /// One idiom fixes all four: resolve the Knight the physics actually
    /// involves and masquerade the singleton for the handler's duration.
    /// </summary>
    internal static class EnvironmentPatches
    {
        internal static HeroController HeroOf(Component c) =>
            c == null ? null : c.GetComponentInParent<HeroController>();

        internal static Masquerade<HeroController>.Scope ImpersonateExtra(HeroController hero) =>
            hero != null && CoopManager.IsExtra(hero)
                ? Reflect.HeroMasq.Impersonate(hero)
                : null;
    }

    /// <summary>Nail tink recoil goes to the Knight whose nail tinked.</summary>
    [HarmonyPatch(typeof(TinkEffect), "OnTriggerEnter2D")]
    internal static class TinkPatch
    {
        private static void Prefix(Collider2D collision, out Masquerade<HeroController>.Scope __state)
            => __state = CoopManager.Active
                ? EnvironmentPatches.ImpersonateExtra(EnvironmentPatches.HeroOf(collision))
                : null;

        private static Exception Finalizer(Exception __exception, Masquerade<HeroController>.Scope __state)
        { __state?.Restore(); return __exception; }
    }

    /// <summary>
    /// Bounce shrooms (and every other CollisionEnterEvent subscriber) act on
    /// the Knight that actually made contact.
    /// </summary>
    [HarmonyPatch]
    internal static class CollisionEventPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CollisionEnterEvent), "OnCollisionEnter2D");
            var stay = AccessTools.Method(typeof(CollisionEnterEvent), "OnCollisionStay2D");
            if (stay != null) yield return stay;
        }

        private static void Prefix(Collision2D collision, out Masquerade<HeroController>.Scope __state)
            => __state = CoopManager.Active
                ? EnvironmentPatches.ImpersonateExtra(EnvironmentPatches.HeroOf(collision.collider))
                : null;

        private static Exception Finalizer(Exception __exception, Masquerade<HeroController>.Scope __state)
        { __state?.Restore(); return __exception; }
    }

    /// <summary>
    /// Soul orb collection grants to the Knight the orb was flying to. The
    /// grant lives inside the Zoom coroutine, so the patch rides its compiler-
    /// generated MoveNext and resolves the orb's own target field.
    /// </summary>
    [HarmonyPatch]
    internal static class SoulOrbCollectPatch
    {
        private static readonly FieldInfo TargetField =
            AccessTools.Field(typeof(SoulOrb), "target");

        private static MethodBase TargetMethod()
            => AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(SoulOrb), "Zoom"));

        private static void Prefix(object __instance, out Masquerade<HeroController>.Scope __state)
        {
            __state = null;
            if (!CoopManager.Active || TargetField == null) return;

            // The state machine's `<>4__this` is the SoulOrb.
            var self = __instance.GetType()
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(f => f.FieldType == typeof(SoulOrb))?.GetValue(__instance) as SoulOrb;
            if (self == null) return;

            var target = TargetField.GetValue(self) as Transform;
            __state = EnvironmentPatches.ImpersonateExtra(EnvironmentPatches.HeroOf(target));
        }

        private static Exception Finalizer(Exception __exception, Masquerade<HeroController>.Scope __state)
        { __state?.Restore(); return __exception; }
    }

    /// <summary>Sight-gated enemies see whoever is nearest, not only player one.</summary>
    [HarmonyPatch(typeof(LineOfSightDetector), "Update")]
    internal static class LineOfSightPatch
    {
        private static void Prefix(LineOfSightDetector __instance, out Masquerade<HeroController>.Scope __state)
        {
            __state = null;
            if (!CoopManager.Active) return;

            var here = __instance.transform.position;
            HeroController nearest = null;
            var best = float.MaxValue;
            foreach (var h in CoopManager.AllHeroes)
            {
                var d = (h.transform.position - here).sqrMagnitude;
                if (d < best) { best = d; nearest = h; }
            }
            __state = EnvironmentPatches.ImpersonateExtra(nearest);
        }

        private static Exception Finalizer(Exception __exception, Masquerade<HeroController>.Scope __state)
        { __state?.Restore(); return __exception; }
    }
}
