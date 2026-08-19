using System.Collections;
using UnityEngine;

namespace HKCouchCoop
{
    /// <summary>
    /// Death and revival in the game's own grammar, with the game's own actors.
    ///
    /// When an extra player dies: their body plays the real hero-death effect
    /// and is destroyed, and the REAL Shade — the same prefab SceneManager
    /// spawns for a normal death — rises where they fell. Teammates fight it
    /// exactly like any shade; killing it revives the fallen player at half
    /// masks, stepping out of the shade's death burst.
    ///
    /// One piece of invisible bookkeeping: the shade's own death sequence
    /// settles the player's "shade bank" (banked geo in `geoPool`, the
    /// `shadeScene` marker, the `soulLimited` flag). A revival shade must not
    /// touch player one's real bank — so the bank is neutralized the instant
    /// the revival shade dies and restored a few seconds later, after its
    /// death sequence has finished reading the fields.
    /// </summary>
    internal static class ShadeRevive
    {
        private sealed class Bank
        {
            internal bool SoulLimited; internal string Scene; internal string MapZone;
            internal float X, Y; internal int GeoPool;
        }

        // The bank is only ever neutralized between a revival shade's death and
        // the timed restore. If ANYTHING ends the session in that window — save
        // and quit, menu, plugin teardown — restore immediately, or the
        // neutralized values could be written into the save file.
        private static Bank _pending;

        internal static void ForceRestoreBank()
        {
            if (_pending != null) Apply(_pending);
            _pending = null;
        }

        private static void Apply(Bank bank)
        {
            var pd = PlayerData.instance;
            if (pd == null) return;
            pd.soulLimited = bank.SoulLimited;
            pd.shadeScene = bank.Scene;
            pd.shadeMapZone = bank.MapZone;
            pd.shadePositionX = bank.X;
            pd.shadePositionY = bank.Y;
            pd.geoPool = bank.GeoPool;
        }

        internal static void Down(CoopPlayer player, Vector3? shadeAnchor = null)
        {
            var hero = player.Hero;
            if (hero == null) return;

            var pos = shadeAnchor ?? hero.transform.position;

            // The real death visual: the burst prefab is the clone's own child
            // copy; detach it so it outlives the body it announces.
            var death = hero.heroDeathPrefab;
            if (death != null)
            {
                death.transform.SetParent(null, worldPositionStays: true);
                death.SetActive(true);
                Object.Destroy(death, 4f);
            }

            Object.Destroy(hero.gameObject);
            player.Hero = null;
            player.Downed = true;

            SpawnShade(player, pos);
            NativeHud.Notify($"Player {player.Number} has fallen — defeat their shade");
        }

        /// <summary>Also used to re-materialize the shade after a room change.</summary>
        internal static void SpawnShade(CoopPlayer player, Vector3 pos)
        {
            var prefab = GameManager.instance != null && GameManager.instance.sm != null
                ? GameManager.instance.sm.hollowShadeObject
                : null;

            if (prefab == null)
            {
                // No prefab to hand (should not happen in gameplay scenes) —
                // degrade honestly: the player is out and rejoins with Start.
                Plugin.Log.LogWarning("No shade prefab available; downed player must rejoin with Start.");
                CoopManager.RemoveDowned(player);
                return;
            }

            var shade = Object.Instantiate(
                prefab, new Vector3(pos.x, pos.y + 0.5f, 0.006f), Quaternion.identity);
            player.Shade = shade;

            var hm = shade.GetComponent<HealthManager>() ?? shade.GetComponentInChildren<HealthManager>();
            if (hm != null)
            {
                hm.OnDeath += () => OnShadeSlain(player);
            }
            else
            {
                Plugin.Log.LogWarning("Revival shade has no HealthManager; falling back to Start-rejoin.");
                Object.Destroy(shade);
                CoopManager.RemoveDowned(player);
            }
        }

        private static void OnShadeSlain(CoopPlayer player)
        {
            var pd = PlayerData.instance;
            Vector3 at = player.Shade != null
                ? player.Shade.transform.position
                : (HeroController.instance != null
                    ? HeroController.instance.transform.position : Vector3.zero);

            // Neutralize the bank before the shade's death sequence reads it,
            // restore once that sequence is over.
            if (pd != null)
            {
                var bank = new Bank
                {
                    SoulLimited = pd.soulLimited, Scene = pd.shadeScene, MapZone = pd.shadeMapZone,
                    X = pd.shadePositionX, Y = pd.shadePositionY, GeoPool = pd.geoPool,
                };
                pd.geoPool = 0;
                pd.shadeScene = "None";
                _pending = bank;
                Plugin.Instance.StartCoroutine(RestoreBank(bank));
            }

            player.Shade = null;
            CoopManager.ReviveAt(player, at);
        }

        private static IEnumerator RestoreBank(Bank bank)
        {
            // Realtime: a pause must not stretch the window the bank sits
            // neutralized in.
            yield return new WaitForSecondsRealtime(4f);
            if (_pending == bank) _pending = null;
            Apply(bank);
        }

        internal static void DestroyShade(CoopPlayer player)
        {
            if (player.Shade != null) Object.Destroy(player.Shade);
            player.Shade = null;
        }
    }
}
