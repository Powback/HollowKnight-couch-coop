using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace HKCouchCoop
{
    /// <summary>
    /// Routes every PlayMaker `ListenFor*` input poll to the right Knight.
    ///
    /// These actions drive two very different things through one hardwired
    /// input source (`GameManager.instance.GetComponent&lt;InputHandler&gt;()`
    /// — always player one):
    ///  - world interactions (wells, doors, prompts): must answer to the
    ///    Knight standing there;
    ///  - the hero's own ability FSMs (Spell Control's quick cast, Superdash,
    ///    Dream Nail): must answer to the Knight that OWNS the FSM — or player
    ///    one's buttons fire every Knight's abilities at once while the
    ///    extras' own buttons do nothing.
    ///
    /// Only three listener types poll via a hookable CheckForInput; the rest
    /// poll inside OnEnter/OnUpdate — so the handler field is swapped before
    /// all three, for every non-menu listener type. Resolution is cached per
    /// action instance and revalidated when the roster changes.
    /// </summary>
    internal static class CoopInput
    {
        /// <summary>
        /// Menu and UI listeners stay with player one — redirecting them would
        /// let extra pads steer the pause menu, inventory and dialogue panes.
        /// </summary>
        private static readonly HashSet<string> MenuActions = new HashSet<string>
        {
            "ListenForMenuActions", "ListenForMenuSubmit", "ListenForMenuCancel",
            "ListenForPaneLeft", "ListenForPaneRight", "ListenForInventory",
            "ListenForPromptContinue",
        };

        private static readonly Dictionary<System.Type, FieldInfo> HandlerFields =
            new Dictionary<System.Type, FieldInfo>();

        private sealed class Resolution
        {
            internal InputHandler Handler;
            internal int RosterVersion;
        }

        private static readonly ConditionalWeakTable<object, Resolution> Cache =
            new ConditionalWeakTable<object, Resolution>();

        /// <summary>Bumped on join/leave so cached routes recompute.</summary>
        internal static int RosterVersion;

        internal static IEnumerable<MethodBase> ListenForPollMethods()
        {
            const BindingFlags any = BindingFlags.Instance
                | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (var type in typeof(HeroController).Assembly.GetTypes())
            {
                if (type.Namespace != "HutongGames.PlayMaker.Actions") continue;
                if (!type.Name.StartsWith("ListenFor")) continue;
                if (MenuActions.Contains(type.Name)) continue;

                var field = type.GetField("inputHandler", any);
                if (field == null) continue;
                HandlerFields[type] = field;

                // Swap before every place a poll can happen. Declared-only:
                // inherited FsmStateAction virtuals would duplicate targets.
                foreach (var name in new[] { "CheckForInput", "OnEnter", "OnUpdate" })
                {
                    var m = type.GetMethod(name, any | BindingFlags.DeclaredOnly);
                    if (m != null) yield return m;
                }
            }
        }

        /// <summary>
        /// Which Knight's input should this FSM read? An FSM living on a Knight
        /// uses that Knight; anything in the world uses the nearest one — the
        /// one standing in its trigger.
        /// </summary>
        private static InputHandler ResolveFor(GameObject owner)
        {
            if (owner == null) return null;

            var onHero = owner.GetComponentInParent<HeroController>();
            if (onHero != null) return Reflect.GetInputHandler(onHero);

            var heroes = CoopManager.AllHeroes.ToList();
            if (heroes.Count == 0) return null;

            var here = owner.transform.position;
            HeroController nearest = null;
            var best = float.MaxValue;
            foreach (var h in heroes)
            {
                var d = (h.transform.position - here).sqrMagnitude;
                if (d < best) { best = d; nearest = h; }
            }
            return Reflect.GetInputHandler(nearest);
        }

        internal static void Retarget(object action)
        {
            if (!CoopManager.Active) return;
            if (!HandlerFields.TryGetValue(action.GetType(), out var field)) return;

            var fsm = (action as HutongGames.PlayMaker.FsmStateAction)?.Fsm;
            var box = Cache.GetValue(action, _ => new Resolution { RosterVersion = -1 });

            if (box.RosterVersion != RosterVersion || box.Handler == null)
            {
                box.Handler = ResolveFor(fsm?.GameObject);
                box.RosterVersion = RosterVersion;
            }

            if (box.Handler == null) return;
            field.SetValue(action, box.Handler);

            // Interaction ownership: a world FSM (no hero parent) whose resolved
            // Knight is an extra pressing an interaction button right now gets
            // claimed for that Knight — its whole sequence (seating, dialogue)
            // then executes as them via FsmOwnership.
            var hero = box.Handler.GetComponentInParent<HeroController>();
            if (hero == null)
            {
                // Extra handlers live on inactive carrier objects; map back.
                hero = CoopManager.HeroForHandler(box.Handler);
            }
            if (hero == null || !CoopManager.IsExtra(hero)) return;
            if (fsm?.GameObject == null || fsm.GameObject.GetComponentInParent<HeroController>() != null) return;

            var a = box.Handler.inputActions;
            if (a != null && (a.up.WasPressed || a.down.WasPressed || a.cast.WasPressed || a.attack.WasPressed))
                FsmOwnership.ClaimWorldFsm(fsm, hero);
        }
    }

    [HarmonyPatch]
    internal static class ListenForPatches
    {
        private static IEnumerable<MethodBase> TargetMethods() => CoopInput.ListenForPollMethods();

        // Runs before every poll — including OnEnter's immediate check — so a
        // state can never fire on the wrong player's input.
        private static void Prefix(object __instance)
            => Guard.Run(() => CoopInput.Retarget(__instance), "ListenFor retarget");
    }
}
