using System;
using System.Runtime.CompilerServices;
using CoopKit;
using HarmonyLib;
using HutongGames.PlayMaker;

namespace HKCouchCoop
{
    /// <summary>
    /// FSM ownership: a PlayMaker FSM living ON an extra Knight (its Spell
    /// Control, its Charm Effects, its nail arts) must act as that Knight —
    /// but its actions were authored against `HeroController.instance`.
    /// Without this, an extra player's fireball launches from player one's
    /// body and their focus heals player one.
    ///
    /// For the duration of any FSM tick whose owner GameObject sits under an
    /// extra Knight, the singleton points at that Knight. World FSMs (owned by
    /// benches, gates, enemies) are untouched: the masquerade engages only for
    /// hero-parented FSMs, so scope stays tight and cheap.
    ///
    /// Cost control: one ConditionalWeakTable lookup per FSM tick, resolved to
    /// a cached owner; a single Active check bails the common case.
    /// </summary>
    internal static class FsmOwnership
    {
        private sealed class OwnerBox { internal HeroController Hero; }

        private static readonly ConditionalWeakTable<Fsm, OwnerBox> Cache =
            new ConditionalWeakTable<Fsm, OwnerBox>();

        // Interaction ownership: a WORLD FSM (bench, NPC, lever) triggered by
        // an extra Knight's press executes as that Knight until someone else
        // triggers it — so a bench seats the player who sat on it, not player
        // one from across the room.
        private static readonly ConditionalWeakTable<Fsm, OwnerBox> WorldTrigger =
            new ConditionalWeakTable<Fsm, OwnerBox>();

        /// <summary>The extra Knight currently driving a world interaction
        /// (bench, NPC dialogue), if any. Menu-class input follows them while
        /// their conversation holds them (control relinquished).</summary>
        internal static HeroController InteractionOwner;

        /// <summary>Claim (extra) or clear (null / player one) a world FSM.</summary>
        internal static void ClaimWorldFsm(Fsm fsm, HeroController hero)
        {
            if (fsm == null) return;
            WorldTrigger.GetValue(fsm, _ => new OwnerBox()).Hero = hero;
            InteractionOwner = hero;
        }

        internal static Masquerade<HeroController>.Scope BeginFor(Fsm fsm)
        {
            if (!CoopManager.Active || fsm == null) return null;

            var claimed = ClaimedOwner(fsm);
            if (claimed != null) return Reflect.HeroMasq.Impersonate(claimed);

            // An enemy's own FSM acts against whoever is NEAREST, not against
            // player one. Enemy AI reads HeroController.instance to decide who
            // to chase, face and swing at, so without this every enemy in the
            // game fixates on player one and treats the other Knights as
            // scenery — reported from play as "enemies only attack p1". The
            // damage routing was already fixed (HeroBox points at its own
            // Knight); this is the targeting.
            var enemy = NearestForEnemy(fsm);
            if (enemy != null) return Reflect.HeroMasq.Impersonate(enemy);

            var box = Cache.GetValue(fsm, Resolve);

            var hero = box.Hero;
            if (hero == null) return null;          // Unity-null covers destroyed clones
            if (CoopManager.FindExtra(hero) == null)
            {
                // Owner left the roster (revive spawns a NEW hero object, so a
                // stale cache entry resolves here) — recompute once.
                box.Hero = ResolveHero(fsm);
                hero = box.Hero;
                if (hero == null || CoopManager.FindExtra(hero) == null) return null;
            }

            return Reflect.HeroMasq.Impersonate(hero);
        }

        // Which FSMs belong to enemies, remembered so the component lookup
        // happens once per FSM rather than every tick of every FSM in the room.
        private static readonly ConditionalWeakTable<Fsm, OwnerBox> EnemyCache =
            new ConditionalWeakTable<Fsm, OwnerBox>();

        private sealed class EnemyFlag { internal bool IsEnemy; }
        private static readonly ConditionalWeakTable<Fsm, EnemyFlag> IsEnemyCache =
            new ConditionalWeakTable<Fsm, EnemyFlag>();

        /// <summary>
        /// The Knight an enemy FSM should be acting against, or null if this is
        /// not an enemy.
        ///
        /// "Enemy" means it carries a HealthManager — the component that makes
        /// something killable — which is the same test the game itself uses to
        /// decide what a nail can hit. Nearest wins, so an enemy turns on
        /// whoever walked into it rather than on whoever happens to be the
        /// singleton.
        /// </summary>
        private static HeroController NearestForEnemy(Fsm fsm)
        {
            var go = fsm.GameObject;
            if (go == null) return null;

            var flag = IsEnemyCache.GetValue(fsm, _ => new EnemyFlag
            {
                IsEnemy = go.GetComponentInParent<HealthManager>() != null,
            });
            if (!flag.IsEnemy) return null;

            HeroController nearest = null;
            var best = float.MaxValue;
            var here = go.transform.position;
            foreach (var h in CoopManager.FramableHeroes)
            {
                if (h == null) continue;
                var d = (h.transform.position - here).sqrMagnitude;
                if (d < best) { best = d; nearest = h; }
            }

            // Player one is the singleton already; masquerading to him costs a
            // scope for nothing.
            return ReferenceEquals(nearest, CoopManager.PlayerOne) ? null : nearest;
        }

        /// <summary>
        /// A recent world-interaction claim outranks parentage — world FSMs
        /// have no hero parent at all, so a bench or a lever is only tied to
        /// the Knight who pressed it.
        /// </summary>
        private static HeroController ClaimedOwner(Fsm fsm)
        {
            if (!WorldTrigger.TryGetValue(fsm, out var claim) || claim.Hero == null)
                return null;
            if (CoopManager.FindExtra(claim.Hero) != null) return claim.Hero;
            claim.Hero = null;      // stale (left or downed) — fall through
            return null;
        }

        private static OwnerBox Resolve(Fsm fsm) => new OwnerBox { Hero = ResolveHero(fsm) };

        private static HeroController ResolveHero(Fsm fsm)
        {
            var go = fsm.GameObject;
            if (go == null) return null;
            var hero = go.GetComponentInParent<HeroController>();
            return hero != null && CoopManager.IsExtra(hero) ? hero : null;
        }
    }

    [HarmonyPatch(typeof(Fsm), nameof(Fsm.Update))]
    internal static class FsmUpdatePatch
    {
        private static void Prefix(Fsm __instance, out Masquerade<HeroController>.Scope __state)
            => __state = Guard.Run(() => FsmOwnership.BeginFor(__instance), "FsmOwnership");
        private static Exception Finalizer(Exception __exception, Masquerade<HeroController>.Scope __state)
        { __state?.Restore(); return __exception; }
    }

    [HarmonyPatch(typeof(Fsm), nameof(Fsm.FixedUpdate))]
    internal static class FsmFixedUpdatePatch
    {
        private static void Prefix(Fsm __instance, out Masquerade<HeroController>.Scope __state)
            => __state = Guard.Run(() => FsmOwnership.BeginFor(__instance), "FsmOwnership");
        private static Exception Finalizer(Exception __exception, Masquerade<HeroController>.Scope __state)
        { __state?.Restore(); return __exception; }
    }

    [HarmonyPatch(typeof(Fsm), nameof(Fsm.LateUpdate))]
    internal static class FsmLateUpdatePatch
    {
        private static void Prefix(Fsm __instance, out Masquerade<HeroController>.Scope __state)
            => __state = Guard.Run(() => FsmOwnership.BeginFor(__instance), "FsmOwnership");
        private static Exception Finalizer(Exception __exception, Masquerade<HeroController>.Scope __state)
        { __state?.Restore(); return __exception; }
    }
}
