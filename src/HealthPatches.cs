using UnityEngine;
using System;
using System.Collections.Generic;
using System.Reflection;
using CoopKit;
using InControl;
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

        /// <summary>
        /// A Knight's mask count, from wherever that Knight actually keeps it:
        /// the shared PlayerData for player one, its own pool for a clone.
        /// Reading PlayerData for everyone reports player one's masks three
        /// times over and makes a clone's damage look like it did nothing.
        /// </summary>
        internal static int Read(HeroController hc)
        {
            var extra = CoopManager.FindExtra(hc);
            if (extra != null) return extra.Health;
            var pd = PlayerData.instance;
            return pd != null ? pd.health : 0;
        }

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
    /// <summary>
    /// Friendly fire, hung off the swing itself.
    ///
    /// Two earlier attempts failed for reasons worth keeping. Hanging it on
    /// DamageEnemies.OnTriggerEnter2D never fired at all: trigger callbacks go
    /// through the physics layer matrix, where nail-versus-hero is disabled.
    /// Moving to DamageEnemies.FixedUpdate never fired either — a run measured
    /// patchRuns:0 — because that component is not what a Knight's nail uses.
    ///
    /// NailSlash.StartSlash IS the swing: it is the method that enables the
    /// nail's own polygon collider. So a swing is registered here and tested
    /// against the other Knights for as long as it lasts, with
    /// Collider2D.Distance measuring the geometry directly rather than asking
    /// the physics system who is allowed to touch whom.
    /// </summary>
    internal static class FriendlyFire
    {
        // Three probes along the one path an attack takes:
        //   inputActions.attack  ->  CanAttack()  ->  DoAttack()  ->  StartSlash()
        // A zero at the end says nothing about WHICH link broke, and several
        // rewrites were spent guessing. Each link now reports for itself.
        internal static int Samples;     // the probe below ran at all
        internal static int NoHandler;   // ...but InputHandler was not there
        internal static int InputSeen;   // InControl gave player one the button
        internal static int RawAction3;  // ANY pad reported physical X itself
        internal static int RawAnyButton;// ANY pad reported ANY face button
        internal static int Attacks;     // DoAttack ran: input arrived AND the gate opened
        internal static int Slashes;     // StartSlash seen at all, by anyone
        internal static int Swings;      // ...and it was a Knight's, with FF on
        internal static int Overlaps;    // a swing was on another Knight
        internal static int Hits;        // TakeDamage was CALLED on a Knight
        internal static int Landed;      // ...and a mask actually came off
        internal static int Refused;     // ...and the game refused it

        private static readonly ThrottledLog _hitLog =
            new ThrottledLog(1, m => Plugin.Log.LogInfo(m));

        private sealed class Swing
        {
            internal HeroController Attacker;
            internal Collider2D Blade;
            internal float Until;
            internal readonly HashSet<int> Struck = new HashSet<int>();
        }

        private static readonly List<Swing> Active = new List<Swing>();

        /// <summary>A Knight swung. Watch it for as long as the arc lasts.</summary>
        internal static void Begin(NailSlash slash)
        {
            if (slash == null) return;
            // Counted BEFORE any guard. Every previous version of this counter
            // sat behind the friendly-fire check, so a zero could not tell "no
            // swing reached the game" from "swings happened and were filtered"
            // — and two rewrites were spent on the wrong half of that.
            Slashes++;
            if (!Plugin.Cfg.FriendlyFire.Value || !CoopManager.Active) return;
            var attacker = slash.GetComponentInParent<HeroController>();
            if (attacker == null) return;
            var blade = slash.GetComponent<Collider2D>();
            if (blade == null) return;

            Swings++;
            Active.Add(new Swing
            {
                Attacker = attacker,
                Blade = blade,
                Until = Time.unscaledTime + 0.4f,   // longer than any slash arc
            });
        }

        /// <summary>Driven from Plugin.Update.</summary>
        /// <summary>
        /// Samples player one's raw attack button. This asks InControl the same
        /// question HeroController asks, so a zero here is proof the input
        /// never arrived rather than an inference from a missing swing.
        /// </summary>
        internal static void SampleInput()
        {
            // Counted first. A guarded counter cannot tell "never pressed" from
            // "never sampled", which is the exact trap the Slashes counter fell
            // into twice.
            Samples++;

            // Ask InControl about the hardware directly, below any binding.
            // This separates the two remaining explanations for a dead attack
            // button: RawAction3 climbing while InputSeen stays flat means the
            // pad is fine and the game's binding is not; both flat means the
            // button never reaches InControl at all, and the fault is under the
            // game — the pad, SDL, or Proton's XInput.
            for (var i = 0; i < InputManager.Devices.Count; i++)
            {
                var d = InputManager.Devices[i];
                if (d == null) continue;
                if (d.Action3.IsPressed) RawAction3++;
                if (d.Action1.IsPressed || d.Action2.IsPressed
                    || d.Action3.IsPressed || d.Action4.IsPressed) RawAnyButton++;
            }

            var ih = InputHandler.Instance;
            if (ih == null || ih.inputActions == null) { NoHandler++; return; }
            if (ih.inputActions.attack.IsPressed) InputSeen++;
        }

        internal static void Tick()
        {
            if (Active.Count == 0) return;
            var now = Time.unscaledTime;
            for (var i = Active.Count - 1; i >= 0; i--)
            {
                var sw = Active[i];
                if (sw.Attacker == null || sw.Blade == null || now > sw.Until)
                {
                    Active.RemoveAt(i);
                    continue;
                }
                if (!sw.Blade.enabled) continue;    // between frames of the arc
                Resolve(sw);
            }
        }

        private static void Resolve(Swing sw)
        {
            // ONE mask, not nailDamage. HeroController.TakeDamage counts in
            // masks, while nailDamage is the enemy-facing number (5 on a base
            // nail, more once upgraded) — passing it emptied a full six-mask
            // Knight in three swings, which is not "friendly fire can kill",
            // it is a one-hit kill wearing a disguise. Contact damage in this
            // game is one mask, sometimes two; a nail is worth one.
            const int damage = 1;

            foreach (var victim in CoopManager.AllHeroes)
            {
                if (victim == null || ReferenceEquals(victim, sw.Attacker)) continue;
                var id = victim.GetInstanceID();
                if (sw.Struck.Contains(id) || !Overlapping(sw.Blade, victim)) continue;

                Overlaps++;
                var side = sw.Attacker.transform.position.x <= victim.transform.position.x
                    ? GlobalEnums.CollisionSide.left
                    : GlobalEnums.CollisionSide.right;
                // Their own TakeDamage: the pool swap puts it on their health,
                // their invulnerability applies, their death is their own.
                // Measured across the call, because "we dealt a hit" and "a
                // mask came off" are different claims and this counter has
                // already been read as the second when it only ever meant the
                // first. HeroController.TakeDamage refuses silently for a list
                // of reasons — invulnerability frames after an earlier hit,
                // damage mode, recoil — and returns nothing to say so.
                var pool = HealthPool.Read(victim);
                victim.TakeDamage(sw.Blade.gameObject, side, damage, hazardType: 0);
                var after = HealthPool.Read(victim);
                if (after < pool) Landed++;
                else Refused++;
                // Name the condition that refused it. CanTakeDamage tests
                // eight things and returns one bool, so "refused" on its own
                // sends the next person round the same loop this one took.
                _hitLog.Log(Time.unscaledTimeAsDouble,
                    $"Friendly fire: {pool} -> {after} masks on {victim.gameObject.name}"
                    + (after < pool ? "" : " REFUSED by " + WhyRefused(victim)));

                sw.Struck.Add(id);
                Hits++;
            }
        }

        /// <summary>
        /// Which of HeroController.CanTakeDamage's conditions is blocking.
        /// Read straight off the victim, so it describes this refusal rather
        /// than a guess about refusals in general.
        /// </summary>
        private static string WhyRefused(HeroController v)
        {
            var pd = PlayerData.instance;
            var why = new List<string>();
            if (v.cState == null) return "no cState";
            if (v.cState.invulnerable) why.Add("invulnerable (i-frames)");
            if (v.cState.recoiling) why.Add("recoiling");
            if (v.cState.dead) why.Add("dead");
            if (v.cState.hazardDeath) why.Add("hazardDeath");
            if (pd != null && pd.isInvincible) why.Add("playerData.isInvincible");
            if (v.transitionState != GlobalEnums.HeroTransitionState.WAITING_TO_TRANSITION)
                why.Add("transitionState=" + v.transitionState);
            if (v.damageMode != GlobalEnums.DamageMode.FULL_DAMAGE)
                why.Add("damageMode=" + v.damageMode);
            return why.Count > 0 ? string.Join(" + ", why.ToArray()) : "nothing obvious";
        }

        /// <summary>
        /// Geometry, not physics. Collider2D.Distance ignores the layer matrix
        /// that stops a nail touching a hero in the first place.
        /// </summary>
        private static bool Overlapping(Collider2D blade, HeroController victim)
        {
            foreach (var vc in victim.GetComponents<Collider2D>())
            {
                if (vc == null || !vc.enabled) continue;
                if (blade.Distance(vc).isOverlapped) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Counts DoAttack, which sits between the input read and the swing. With
    /// this and <see cref="FriendlyFire.InputSeen"/>, a failed swing names its
    /// own cause: no input means the button never bound, input without an
    /// attack means CanAttack() refused, and an attack without a slash means
    /// this assembly's patch did not take.
    /// </summary>
    [HarmonyPatch(typeof(HeroController), "DoAttack")]
    internal static class AttackProbePatch
    {
        private static void Postfix() => FriendlyFire.Attacks++;
    }

    [HarmonyPatch(typeof(NailSlash), nameof(NailSlash.StartSlash))]
    internal static class NailSwingPatch
    {
        private static void Postfix(NailSlash __instance) =>
            Guard.Run(() => FriendlyFire.Begin(__instance), "Friendly fire swing");
    }
}
