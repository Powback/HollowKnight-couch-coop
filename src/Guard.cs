using System;
using CoopKit;
using UnityEngine;

namespace HKCouchCoop
{
    /// <summary>
    /// Exception jacket for Harmony patch bodies that run inside the game's
    /// per-frame loops. A throw from a prefix on Fsm.Update or an input poll
    /// would take the surrounding vanilla system down with it, every frame,
    /// for everyone — the one remaining way this mod could kill all input.
    /// Patches degrade to vanilla behavior instead, with a throttled log.
    /// </summary>
    internal static class Guard
    {
        private static readonly ThrottledLog Errors =
            new ThrottledLog(10, m => Plugin.Log?.LogError(m));

        internal static T Run<T>(Func<T> f, string site) where T : class
        {
            try { return f(); }
            catch (Exception e) { Errors.Log(Time.unscaledTime, $"{site}: {e}"); return null; }
        }

        internal static void Run(Action a, string site)
        {
            try { a(); }
            catch (Exception e) { Errors.Log(Time.unscaledTime, $"{site}: {e}"); }
        }

        internal static bool Run(Func<bool> f, bool fallback, string site)
        {
            try { return f(); }
            catch (Exception e) { Errors.Log(Time.unscaledTime, $"{site}: {e}"); return fallback; }
        }
    }
}
