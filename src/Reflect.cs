using System;
using System.Reflection;
using CoopKit;
using HarmonyLib;

namespace HKCouchCoop
{
    /// <summary>
    /// Cached accessors for the private game state we have to reach into.
    /// Everything here is resolved once at load; a null result means the game
    /// changed shape and the plugin should refuse to run rather than NRE later.
    /// </summary>
    internal static class Reflect
    {
        // HeroController._instance — the singleton backing field. We swap this
        // temporarily so vanilla `this == _instance` guards accept player two.
        private static readonly FieldInfo HeroInstanceField =
            AccessTools.Field(typeof(HeroController), "_instance");

        // HeroController.inputHandler — the per-instance input source. This is
        // the field that makes the whole mod possible: give the clone its own
        // InputHandler and all vanilla movement code reads player two's pad.
        private static readonly FieldInfo HeroInputHandlerField =
            AccessTools.Field(typeof(HeroController), "inputHandler");

        // CameraController.hero_ctrl — camera's follow target.
        private static readonly FieldInfo CamHeroField =
            AccessTools.Field(typeof(CameraController), "hero_ctrl");

        // GameManager's own hero references. Both are auto-properties with
        // private setters, and both are CACHES: read once, never re-derived
        // until what they point at dies. See GameManagerHeroRefsPatch.
        internal static readonly MethodInfo SetGameManagerHero =
            AccessTools.PropertySetter(typeof(GameManager), "hero_ctrl");
        internal static readonly MethodInfo SetGameManagerHeroLight =
            AccessTools.PropertySetter(typeof(GameManager), "heroLight");

        /// <summary>True when every member we depend on was found.</summary>
        internal static bool Verify(out string missing)
        {
            missing = null;
            if (HeroInstanceField == null) missing = "HeroController._instance";
            else if (HeroInputHandlerField == null) missing = "HeroController.inputHandler";
            else if (CamHeroField == null) missing = "CameraController.hero_ctrl";
            else if (SetGameManagerHero == null) missing = "GameManager.hero_ctrl setter";
            else if (SetGameManagerHeroLight == null) missing = "GameManager.heroLight setter";
            return missing == null;
        }

        internal static HeroController HeroInstance
        {
            get => (HeroController)HeroInstanceField.GetValue(null);
            set => HeroInstanceField.SetValue(null, value);
        }

        internal static void SetInputHandler(HeroController hero, InputHandler handler)
            => HeroInputHandlerField.SetValue(hero, handler);

        internal static InputHandler GetInputHandler(HeroController hero)
            => hero == null ? null : (InputHandler)HeroInputHandlerField.GetValue(hero);

        internal static CameraController CameraHero_Set(CameraController cam, HeroController hero)
        {
            CamHeroField.SetValue(cam, hero);
            return cam;
        }

        /// <summary>
        /// Kit masquerade over the singleton backing field: patches store the
        /// scope in Harmony __state and restore it from a Finalizer.
        /// </summary>
        internal static readonly Masquerade<HeroController> HeroMasq =
            new Masquerade<HeroController>(() => HeroInstance, v => HeroInstance = v);
    }
}
