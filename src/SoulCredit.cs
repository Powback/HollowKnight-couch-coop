using System;
using CoopKit;
using HarmonyLib;

namespace HKCouchCoop
{
    /// <summary>
    /// Soul goes to the Knight who earned it. Enemies grant soul on nail hits
    /// through the single call `HeroController.instance.SoulGain()` inside
    /// HealthManager.Hit — hardwired to player one no matter whose nail landed.
    ///
    /// The hit knows its attacker (`hitInstance.Source` is the slash object, a
    /// child of the striking Knight), so for the duration of Hit the singleton
    /// is pointed at that Knight. Vanilla then does the right thing on its own:
    /// SoulGain runs on the attacker, the soul swap scopes it to their pool —
    /// and enemy-death geo from their kills flies to them instead of player
    /// one. Restore lives in a Finalizer, as always.
    /// </summary>
    [HarmonyPatch(typeof(HealthManager), "Hit")]
    internal static class SoulCredit
    {
        private static void Prefix(HitInstance hitInstance, out Masquerade<HeroController>.Scope __state)
        {
            __state = null;
            if (!CoopManager.Active || hitInstance.Source == null) return;

            var attacker = hitInstance.Source.GetComponentInParent<HeroController>();
            if (attacker == null || !CoopManager.IsExtra(attacker)) return;

            __state = Reflect.HeroMasq.Impersonate(attacker);
        }

        private static Exception Finalizer(Exception __exception, Masquerade<HeroController>.Scope __state)
        {
            __state?.Restore();
            return __exception;
        }
    }
}
