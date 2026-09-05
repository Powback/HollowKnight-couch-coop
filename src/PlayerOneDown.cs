using System.Collections.Generic;
using UnityEngine;

namespace HKCouchCoop
{
    /// <summary>
    /// Player one falling without the run ending.
    ///
    /// An extra player is downed by destroying its Knight and letting the shade
    /// stand in. Player one cannot be treated that way at all: his
    /// HeroController is the singleton the camera follows, the save writes
    /// through, and every FSM in the game resolves — destroying it ends the
    /// session rather than the life. So he is hidden and disarmed in place
    /// instead, and put back when his shade is beaten.
    ///
    /// He also stays GLUED to the nearest living Knight while down, which is
    /// not cosmetic. Room transitions in this game are player one's: the door
    /// triggers on him. A downed player one left lying where he fell would
    /// leave the survivors unable to change rooms at all, so a shade they could
    /// not reach — across a gap, behind a boss — would softlock the run outright.
    /// Riding along with a living Knight keeps every door working.
    /// </summary>
    internal static class PlayerOneDown
    {
        internal static bool Downed { get; private set; }

        private static readonly List<Renderer> _hidden = new List<Renderer>();
        private static bool _hadInvulnerable;

        /// <summary>Hide and disarm player one where he stands.</summary>
        internal static void Down(HeroController p1)
        {
            if (p1 == null || Downed) return;

            ShadeRevive.PlayDeathBurst(p1);

            _hidden.Clear();
            foreach (var r in p1.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                if (r == null || !r.enabled) continue;
                r.enabled = false;
                _hidden.Add(r);
            }

            p1.RelinquishControl();
            p1.StopAnimationControl();

            // Nothing may hurt him while he is already down, or a hazard he is
            // lying in re-enters the death path every frame.
            if (p1.cState != null)
            {
                _hadInvulnerable = p1.cState.invulnerable;
                p1.cState.invulnerable = true;
            }

            Downed = true;
            Plugin.Log.LogInfo("Player one is down; the run continues while a teammate stands.");
        }

        /// <summary>Give him back, at the position his shade was beaten.</summary>
        internal static void Revive(HeroController p1, Vector3 at, int masks)
        {
            if (p1 == null || !Downed) return;

            p1.transform.position = new Vector3(at.x, at.y, p1.transform.position.z);

            foreach (var r in _hidden)
                if (r != null) r.enabled = true;
            _hidden.Clear();

            p1.StartAnimationControl();
            p1.RegainControl();
            if (p1.cState != null) p1.cState.invulnerable = _hadInvulnerable;

            var pd = PlayerData.instance;
            if (pd != null) pd.health = Mathf.Clamp(masks, 1, pd.CurrentMaxHealth);

            Downed = false;
            NativeHud.Notify("Player one revived");
            Plugin.Log.LogInfo($"Player one revived at {masks} masks.");
        }

        /// <summary>
        /// Ride along with whoever is still standing, so doors keep working.
        /// Called every frame while down.
        /// </summary>
        internal static void Tick(HeroController p1)
        {
            if (!Downed || p1 == null) return;

            HeroController host = null;
            var best = float.MaxValue;
            foreach (var h in CoopManager.AllHeroes)
            {
                if (h == null || ReferenceEquals(h, p1)) continue;
                var d = (h.transform.position - p1.transform.position).sqrMagnitude;
                if (d < best) { best = d; host = h; }
            }
            if (host == null) return;

            var at = host.transform.position;
            p1.transform.position = new Vector3(at.x, at.y, p1.transform.position.z);
        }

        /// <summary>Put him back on his feet unconditionally — session teardown.</summary>
        internal static void ForceUp(HeroController p1)
        {
            if (!Downed) return;
            Revive(p1, p1 != null ? p1.transform.position : Vector3.zero,
                   PlayerData.instance != null ? PlayerData.instance.CurrentMaxHealth : 5);
        }
    }
}
