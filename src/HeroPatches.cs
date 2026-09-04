using System;
using System.Reflection;
using CoopKit;
using HarmonyLib;

namespace HKCouchCoop
{
    /// <summary>
    /// The clone-spawn window. <see cref="CoopManager"/> opens it around the
    /// single <c>Instantiate</c> that creates an extra Knight, so the patches
    /// below can tell our own spawn apart from any other hero waking up.
    ///
    /// Non-reentrant by construction: there is exactly one Instantiate call
    /// site and it does not recurse.
    /// </summary>
    internal static class SpawnWindow
    {
        internal static bool Open { get; private set; }

        /// <summary>The real player one, for the duration of the window.</summary>
        internal static HeroController PlayerOne { get; private set; }

        internal static void Begin(HeroController playerOne)
        {
            PlayerOne = playerOne;
            Open = true;
        }

        internal static void End()
        {
            Open = false;
            PlayerOne = null;
        }
    }

    /// <summary>
    /// HeroController gates on singleton identity in exactly two places, and
    /// this is the first: Awake destroys any hero that is not the singleton,
    /// so <c>_instance</c> has to be null for the length of a clone's Awake
    /// or the clone deletes itself.
    ///
    /// A null <c>_instance</c> is NOT inert, which is why the window has to be
    /// this tight. <c>SilentInstance</c> answers null by running
    /// <c>FindObjectOfType&lt;HeroController&gt;()</c> and calling
    /// <c>DontDestroyOnLoad</c> on whatever it finds — with clones on the
    /// field that is an arbitrary Knight, marked undestroyable for the rest of
    /// the session. Nulling it around the whole <c>Instantiate</c> (which is
    /// what this used to do) exposed that to every Awake and OnEnable in the
    /// cloned hierarchy, and vanilla components do read the singleton there:
    /// TrackTriggerObjects subscribes to its <c>heroInPosition</c> event, and
    /// GradeOverride and Dripper cache it. This is the class of bug that
    /// produced the HeroBox null-hero crash.
    ///
    /// Scoped to Awake's body, the exposure is provably nil: Awake calls only
    /// SetupGameRefs and SetupPools, neither of which touches the singleton
    /// (SetupPools is empty). Every other component in the hierarchy — those
    /// waking before this one, and every OnEnable, which Unity runs after all
    /// Awakes — sees player one, the answer that was true a frame earlier and
    /// the only one that is ever right to cache.
    /// </summary>
    [HarmonyPatch(typeof(HeroController), "Awake")]
    internal static class HeroAwakePatch
    {
        private static void Prefix(HeroController __instance) => Guard.Run(() =>
        {
            if (!SpawnWindow.Open) return;
            if (ReferenceEquals(__instance, SpawnWindow.PlayerOne)) return;
            Reflect.HeroInstance = null;   // let this clone's Awake adopt itself
        }, "Hero awake window");

        // Finalizer, not Postfix: Awake sets _instance to the clone, and a
        // throw anywhere in its body would otherwise leave it there forever.
        private static Exception Finalizer(Exception __exception)
        {
            if (SpawnWindow.Open) Reflect.HeroInstance = SpawnWindow.PlayerOne;
            return __exception;   // never swallow the original error
        }
    }

    /// <summary>
    /// The second identity gate: SceneInit early-returns for any hero that is
    /// not the instance, which would leave extra players without input
    /// acceptance or gameplay-scene state after every room transition.
    ///
    /// Rather than rewriting the IL, we briefly point the singleton at the
    /// clone for the duration of the call. The restore lives in a Finalizer,
    /// not a Postfix: Harmony skips Postfixes when the patched method throws,
    /// and a singleton left pointing at a clone would break player one
    /// permanently. A Finalizer runs on every exit path.
    /// </summary>
    [HarmonyPatch(typeof(HeroController), nameof(HeroController.SceneInit))]
    internal static class SceneInitPatch
    {
        private static void Prefix(HeroController __instance, out Masquerade<HeroController>.Scope __state)
        {
            __state = Guard.Run(() => CoopManager.IsExtra(__instance)
                ? Reflect.HeroMasq.Impersonate(__instance)
                : null, "SceneInit masq");
        }

        private static Exception Finalizer(Exception __exception, Masquerade<HeroController>.Scope __state)
        {
            __state?.Restore();
            return __exception;   // never swallow the original error
        }
    }

    /// <summary>
    /// GameManager keeps its own hero reference, and unlike the singleton it
    /// is a CACHE — nothing re-derives it until the object it points at dies.
    /// A single bad read therefore outlives the scope that caused it, which
    /// makes it the one place a time-scoped masquerade can leak permanently.
    ///
    /// Two ways it goes wrong with clones on the field:
    ///
    /// * <c>hero_ctrl = HeroController.instance</c> — read during any
    ///   ownership masquerade, this binds GameManager to a clone, and its
    ///   caller immediately does <c>inputHandler.AttachHeroController(hero_ctrl)</c>,
    ///   handing the game's global input handler to that clone.
    /// * <c>heroLight</c> resolves by <c>FindGameObjectWithTag</c> — singular,
    ///   arbitrary among matches. Instantiate copies tags, so every clone
    ///   carries HeroLightMain and the group's light can end up owned by a
    ///   Knight who then leaves the session.
    ///
    /// Neither is a judgement call: GameManager's hero is player one, always.
    /// Re-assert both after every setup rather than trying to prove no path
    /// can reach it at a bad moment.
    /// </summary>
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.SetupHeroRefs))]
    internal static class GameManagerHeroRefsPatch
    {
        // Resolved and verified in Reflect: a missing setter here would
        // silently disable the guard, so the plugin refuses to load instead.
        private static void Postfix(GameManager __instance) => Guard.Run(() =>
        {
            var p1 = CoopManager.PlayerOne;
            if (p1 == null || __instance == null) return;

            if (!ReferenceEquals(__instance.hero_ctrl, p1))
                Reflect.SetGameManagerHero.Invoke(__instance, new object[] { p1 });

            // Prefer player one's own light over whichever tagged object the
            // scene-wide search happened to reach first.
            if (p1.heroLight != null && !ReferenceEquals(__instance.heroLight, p1.heroLight))
                Reflect.SetGameManagerHeroLight.Invoke(__instance, new object[] { p1.heroLight });
        }, "GameManager hero refs");
    }
}
