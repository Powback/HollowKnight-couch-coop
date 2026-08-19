using HarmonyLib;

namespace HKCouchCoop
{
    /// <summary>
    /// A bench is one seat: only one Knight can sit (the game has a single
    /// seating state machine per bench). The couch rule that follows: whoever
    /// sits, the REST is the party's — a full heal on any Knight refills
    /// everyone's pools. Respawn point is shared save data already.
    /// </summary>
    [HarmonyPatch(typeof(HeroController), nameof(HeroController.MaxHealth))]
    internal static class RestPatchMaxHealth
    {
        private static void Postfix() => CoopManager.PartyRest();
    }

    [HarmonyPatch(typeof(HeroController), nameof(HeroController.MaxHealthKeepBlue))]
    internal static class RestPatchMaxHealthKeepBlue
    {
        private static void Postfix() => CoopManager.PartyRest();
    }
}
