using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HKCouchCoop
{
    /// <summary>
    /// Routes world-interaction input to the Knight who is actually there.
    ///
    /// The game's `ListenFor*` PlayMaker actions — the ones driving wells,
    /// doors, benches, levers and NPC prompts — all resolve their input source
    /// as `GameManager.instance.GetComponent&lt;InputHandler&gt;()`, which is
    /// always player one. The FSM's *proximity* check and its *input* check are
    /// therefore decoupled: an extra Knight can stand in a well while player
    /// one, anywhere on the map, presses Up and sends everyone down it.
    ///
    /// We swap each action's cached handler to the relevant Knight's before
    /// every input poll.
    /// </summary>
    internal static class CoopInput
    {
        /// <summary>
        /// Menu and UI listeners stay with player one. Redirecting these to
        /// whichever Knight is closer would let an extra pad steer the pause
        /// menu, inventory and dialogue panes.
        /// </summary>
        private static readonly HashSet<string> MenuActions = new HashSet<string>
        {
            "ListenForMenuActions", "ListenForMenuSubmit", "ListenForMenuCancel",
            "ListenForPaneLeft", "ListenForPaneRight", "ListenForInventory",
            "ListenForPromptContinue",
        };

        private static readonly Dictionary<System.Type, FieldInfo> HandlerFields =
            new Dictionary<System.Type, FieldInfo>();

        internal static IEnumerable<MethodBase> ListenForCheckMethods()
        {
            foreach (var type in typeof(HeroController).Assembly.GetTypes())
            {
                if (type.Namespace != "HutongGames.PlayMaker.Actions") continue;
                if (!type.Name.StartsWith("ListenFor")) continue;
                if (MenuActions.Contains(type.Name)) continue;

                // Only ListenForUp/Down/Cast actually have CheckForInput — the
                // rest poll inside OnUpdate. Plain reflection rather than
                // AccessTools: a miss here is expected, not warning-worthy.
                const BindingFlags any = BindingFlags.Instance
                    | BindingFlags.Public | BindingFlags.NonPublic;
                var field = type.GetField("inputHandler", any);
                var method = type.GetMethod("CheckForInput", any);
                if (field == null || method == null) continue;

                HandlerFields[type] = field;
                yield return method;
            }
        }

        /// <summary>
        /// Which Knight's input should this FSM read? An FSM living on a Knight
        /// uses that Knight. Anything in the world uses the nearest one, which
        /// is whoever is standing in its trigger.
        /// </summary>
        private static InputHandler ResolveFor(GameObject owner)
        {
            var heroes = CoopManager.AllHeroes.ToList();
            if (owner == null || heroes.Count == 0) return null;

            var onHero = owner.GetComponentInParent<HeroController>();
            if (onHero != null) return Reflect.GetInputHandler(onHero);

            var here = owner.transform.position;
            var nearest = heroes
                .OrderBy(h => (h.transform.position - here).sqrMagnitude)
                .First();

            return Reflect.GetInputHandler(nearest);
        }

        internal static void Retarget(object action)
        {
            if (!CoopManager.Active) return;

            if (!HandlerFields.TryGetValue(action.GetType(), out var field)) return;

            var owner = (action as HutongGames.PlayMaker.FsmStateAction)?.Fsm?.GameObject;
            if (owner == null) return;

            var handler = ResolveFor(owner);
            if (handler != null) field.SetValue(action, handler);
        }
    }

    [HarmonyPatch]
    internal static class ListenForPatches
    {
        private static IEnumerable<MethodBase> TargetMethods() => CoopInput.ListenForCheckMethods();

        // Runs before every poll, including the one OnEnter performs immediately,
        // so a state cannot fire on the wrong player's input even on entry.
        private static void Prefix(object __instance) => CoopInput.Retarget(__instance);
    }
}
