using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CoopKit;
using HarmonyLib;
using UnityEngine;

namespace HKCouchCoop
{
    /// <summary>
    /// World physics responses that detect ANY Knight but act on THE Knight —
    /// the same trigger/actor decoupling as the wells, in the contact layer:
    ///
    ///  - TinkEffect: an extra tinks a spike wall → player one gets recoiled.
    ///  - CollisionEnterEvent (bounce shrooms and everything else subscribed
    ///    to it): an extra stomps a shroom → player one bounces.
    ///  - SoulOrb collection: an extra's orbs fly to them but feed player
    ///    one's soul.
    ///  - LineOfSightDetector: sight-gated enemies only ever "see" player one.
    ///
    /// One idiom fixes all four: resolve the Knight the physics actually
    /// involves and masquerade the singleton for the handler's duration.
    /// </summary>
    internal static class EnvironmentPatches
    {
        internal static HeroController HeroOf(Component c) =>
            c == null ? null : c.GetComponentInParent<HeroController>();

        internal static Masquerade<HeroController>.Scope ImpersonateExtra(HeroController hero) =>
            hero != null && CoopManager.IsExtra(hero)
                ? Reflect.HeroMasq.Impersonate(hero)
                : null;
    }

    /// <summary>Nail tink recoil goes to the Knight whose nail tinked.</summary>
    [HarmonyPatch(typeof(TinkEffect), "OnTriggerEnter2D")]
    internal static class TinkPatch
    {
        private static void Prefix(Collider2D collision, out Masquerade<HeroController>.Scope __state)
            => __state = Guard.Run(() => CoopManager.Active
                ? EnvironmentPatches.ImpersonateExtra(EnvironmentPatches.HeroOf(collision))
                : null, "Tink");

        private static Exception Finalizer(Exception __exception, Masquerade<HeroController>.Scope __state)
        { __state?.Restore(); return __exception; }
    }

    /// <summary>
    /// Bounce shrooms (and every other CollisionEnterEvent subscriber) act on
    /// the Knight that actually made contact.
    /// </summary>
    [HarmonyPatch]
    internal static class CollisionEventPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CollisionEnterEvent), "OnCollisionEnter2D");
            var stay = AccessTools.Method(typeof(CollisionEnterEvent), "OnCollisionStay2D");
            if (stay != null) yield return stay;
        }

        private static void Prefix(Collision2D collision, out Masquerade<HeroController>.Scope __state)
            => __state = Guard.Run(() => CoopManager.Active
                ? EnvironmentPatches.ImpersonateExtra(EnvironmentPatches.HeroOf(collision.collider))
                : null, "CollisionEvent");

        private static Exception Finalizer(Exception __exception, Masquerade<HeroController>.Scope __state)
        { __state?.Restore(); return __exception; }
    }

    /// <summary>
    /// Soul orb collection grants to the Knight the orb was flying to. The
    /// grant lives inside the Zoom coroutine, so the patch rides its compiler-
    /// generated MoveNext and resolves the orb's own target field.
    /// </summary>
    [HarmonyPatch]
    internal static class SoulOrbCollectPatch
    {
        private static readonly FieldInfo TargetField =
            AccessTools.Field(typeof(SoulOrb), "target");

        private static MethodBase TargetMethod()
            => AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(SoulOrb), "Zoom"));

        private static void Prefix(object __instance, out Masquerade<HeroController>.Scope __state)
            => __state = Guard.Run(() => Resolve(__instance), "SoulOrb collect");

        private static Masquerade<HeroController>.Scope Resolve(object stateMachine)
        {
            if (!CoopManager.Active || TargetField == null) return null;

            // The state machine's `<>4__this` is the SoulOrb.
            var self = stateMachine.GetType()
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(f => f.FieldType == typeof(SoulOrb))?.GetValue(stateMachine) as SoulOrb;
            if (self == null) return null;

            var target = TargetField.GetValue(self) as Transform;
            return EnvironmentPatches.ImpersonateExtra(EnvironmentPatches.HeroOf(target));
        }

        private static Exception Finalizer(Exception __exception, Masquerade<HeroController>.Scope __state)
        { __state?.Restore(); return __exception; }
    }

    /// <summary>
    /// HeroBox routes incoming damage through a HeroController cached at
    /// Awake from the singleton — which is deliberately null during clone
    /// instantiation (the spawn masquerade), and would be player one at any
    /// other time. Either way an extra's contact damage goes to the wrong
    /// body. The box's hero is its parent; resolve it that way for everyone
    /// (identical result for player one).
    /// </summary>
    [HarmonyPatch]
    internal static class HeroBoxRoutePatch
    {
        private static readonly FieldInfo HeroField = AccessTools.Field(typeof(HeroBox), "heroCtrl");

        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var name in new[] { "Awake", "OnEnable", "Start" })
            {
                var m = AccessTools.Method(typeof(HeroBox), name);
                if (m != null) yield return m;
            }
        }

        private static void Postfix(HeroBox __instance) => Guard.Run(() =>
        {
            if (HeroField == null) return;
            var own = __instance.GetComponentInParent<HeroController>();
            if (own != null) HeroField.SetValue(__instance, own);
        }, "HeroBox route");
    }

    /// <summary>
    /// Geo caches its collecting hero at spawn; geo spawned during a clone's
    /// kill caches the clone, and collecting it after that player left would
    /// NRE inside vanilla code (lost geo + console spam). Re-point a dead
    /// cached hero at the singleton before the trigger runs.
    /// </summary>
    [HarmonyPatch(typeof(GeoControl), "OnTriggerEnter2D")]
    internal static class GeoSafetyPatch
    {
        private static readonly FieldInfo HeroField = AccessTools.Field(typeof(GeoControl), "hero");

        private static void Prefix(GeoControl __instance) => Guard.Run(() =>
        {
            if (HeroField == null) return;
            var hero = HeroField.GetValue(__instance) as HeroController;
            // Player one, not the singleton: this can run inside an extra's
            // ownership scope, and re-pointing dead geo at a clone reinstates
            // exactly the dangling reference it is meant to clear.
            if (hero == null && CoopManager.PlayerOne != null)
                HeroField.SetValue(__instance, CoopManager.PlayerOne);
        }, "Geo safety");
    }

    /// <summary>Sight-gated enemies see whoever is nearest, not only player one.</summary>
    [HarmonyPatch(typeof(LineOfSightDetector), "Update")]
    internal static class LineOfSightPatch
    {
        private static void Prefix(LineOfSightDetector __instance, out Masquerade<HeroController>.Scope __state)
            => __state = Guard.Run(() => Nearest(__instance), "LineOfSight");

        private static Masquerade<HeroController>.Scope Nearest(LineOfSightDetector det)
        {
            if (!CoopManager.Active) return null;

            var here = det.transform.position;
            HeroController nearest = null;
            var best = float.MaxValue;
            foreach (var h in CoopManager.AllHeroes)
            {
                var d = (h.transform.position - here).sqrMagnitude;
                if (d < best) { best = d; nearest = h; }
            }
            return EnvironmentPatches.ImpersonateExtra(nearest);
        }

        private static Exception Finalizer(Exception __exception, Masquerade<HeroController>.Scope __state)
        { __state?.Restore(); return __exception; }
    }
}

namespace HKCouchCoop
{
    /// <summary>
    /// Rumble belongs to the Knight it happened to. Vanilla resolves ONE
    /// global mixer from InputManager.ActiveDevice — whoever touched their
    /// pad last feels everyone's hits. Route by acting context instead: the
    /// masquerade identifies an extra's vibration (their FSM effects), whose
    /// own pad gets the mixer; player one's vibrations go to player one's
    /// assigned pad — or nowhere when player one is keyboard.
    /// </summary>
    [HarmonyPatch(typeof(VibrationManager), nameof(VibrationManager.GetMixer))]
    internal static class VibrationRoutePatch
    {
        private static bool Prefix(ref VibrationMixer __result)
        {
            var handled = Guard.Run(() =>
            {
                if (!CoopManager.Active) return false;

                InControl.InputDevice device;
                var acting = Reflect.HeroInstance;   // masquerade = acting Knight
                var extra = CoopManager.FindExtra(acting);
                if (extra != null)
                    device = extra.Input?.Device;
                else
                    device = InputAssign.P1Device();  // null = keyboard P1: silence

                VibrationMixer mixer = null;
                if (device != null && device.IsAttached
                    && device is VibrationManager.IVibrationMixerProvider provider)
                    mixer = provider.GetVibrationMixer();

                _routed = mixer;
                return true;
            }, false, "Vibration route");

            if (!handled) return true;
            __result = _routed;
            return false;
        }

        private static VibrationMixer _routed;
    }
}

namespace HKCouchCoop
{
    /// <summary>
    /// Make the darkness cutout reach every pane.
    ///
    /// Darkness is not drawn per camera: a second camera renders a cutout into
    /// a RenderTexture and publishes it globally as `_DarknessCutout`, with the
    /// matrix that projects world positions into it as `_DarknessCameraVP`.
    /// Because that is a full view-projection matrix, ANY world point inside
    /// that camera's frustum resolves correctly — which means split-screen does
    /// not need a darkness pass per pane at all. It needs one darkness camera
    /// wide enough to contain every pane's view.
    ///
    /// Left alone, that camera inherits the game camera's framing, which under
    /// a split is only the first pane — so every other pane samples outside the
    /// cutout and loses its darkness. Here it is widened to cover the whole
    /// group instead, deliberately ignoring the configured zoom ceiling: that
    /// ceiling exists to stop the VIEW stretching past what the game renders
    /// well, and this camera is never looked at.
    ///
    /// Runs as a postfix on EnsureSetup, which OnPreRender calls immediately
    /// before it computes and publishes the matrix — so the matrix published is
    /// the widened one.
    /// </summary>
    [HarmonyPatch(typeof(DarknessCameraEffect), "EnsureSetup")]
    internal static class DarknessCoveragePatch
    {
        private static readonly FieldInfo DarkCam =
            AccessTools.Field(typeof(DarknessCameraEffect), "camera");
        private static readonly FieldInfo MainCam =
            AccessTools.Field(typeof(DarknessCameraEffect), "mainCamera");

        private static Vector3? _restoreLocal;

        private static void Postfix(DarknessCameraEffect __instance) => Guard.Run(() =>
        {
            if (DarkCam == null || MainCam == null) return;
            var dark = DarkCam.GetValue(__instance) as Camera;
            if (dark == null) return;

            if (!SplitScreen.Active)
            {
                // Put it back exactly once. Its fov needs no restoring —
                // EnsureSetup copies that from the game camera every frame.
                if (_restoreLocal.HasValue)
                {
                    dark.transform.localPosition = _restoreLocal.Value;
                    _restoreLocal = null;
                }
                return;
            }

            var main = MainCam.GetValue(__instance) as Camera;
            if (main == null) return;

            var heroes = CoopManager.FramableHeroes.ToList();
            if (heroes.Count < 2) return;

            if (!_restoreLocal.HasValue) _restoreLocal = dark.transform.localPosition;

            var bounds = new Bounds(heroes[0].transform.position, Vector3.zero);
            foreach (var h in heroes) bounds.Encapsulate(h.transform.position);

            var camZ = main.transform.position.z;
            dark.transform.position = new Vector3(bounds.center.x, bounds.center.y, camZ);

            var fov = CoopCamera.FovFor(heroes, main, camZ, dark.aspect, clamp: false);
            if (fov > 0.01f) dark.fieldOfView = fov;
        }, "Darkness coverage");
    }
}
