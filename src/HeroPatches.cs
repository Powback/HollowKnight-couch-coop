using System;
using HarmonyLib;

namespace HKCouchCoop
{
    /// <summary>
    /// HeroController has exactly two places that gate on singleton identity.
    /// Awake's is handled at spawn time (see CoopManager). This is the other:
    /// SceneInit early-returns for any hero that is not the instance, which
    /// would leave extra players without input acceptance or gameplay-scene
    /// state after every room transition.
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
        private static void Prefix(HeroController __instance, out HeroController __state)
        {
            __state = null;
            if (!CoopManager.IsExtra(__instance)) return;

            __state = Reflect.HeroInstance;
            Reflect.HeroInstance = __instance;
        }

        private static Exception Finalizer(Exception __exception, HeroController __state)
        {
            if (__state != null) Reflect.HeroInstance = __state;
            return __exception;   // never swallow the original error
        }
    }
}
