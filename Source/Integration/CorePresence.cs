using System;

namespace RegionsAndSocieties.PersistentWorld.Integration
{
    /// <summary>
    /// The "is a Regions and Societies core loaded?" rule, kept free of game types so it can be tested
    /// without RimWorld. The two Core editions are mutually exclusive and About.xml cannot declare
    /// "either of", so presence is decided at runtime from the active-mod list instead.
    /// <para>The caller hands in an <c>isActive(packageId)</c> probe; the game façade
    /// (<c>PersistentWorldInit.CorePresent</c>) binds it to <c>ModLister</c>. The probe is asked for
    /// each edition id in turn and the rule stops at the first hit.</para>
    /// </summary>
    public static class CorePresence
    {
        /// <summary>The standard edition (Map Mode Framework).</summary>
        public const string CoreMmfId = "RegionsAndSocieties.Core";
        /// <summary>The Realistic Planets 2 edition.</summary>
        public const string CoreRp2Id = "RegionsAndSocieties.CoreRP2";

        /// <summary>Every package id that counts as "a Core is present".</summary>
        public static readonly string[] EditionIds = { CoreMmfId, CoreRp2Id };

        /// <summary>True when <paramref name="isActive"/> reports any Core edition as active. A null probe
        /// means the mod list cannot be read, which is treated as "absent" so the EP no-ops safely.</summary>
        public static bool IsPresent(Func<string, bool> isActive)
        {
            if (isActive == null) return false;
            for (int i = 0; i < EditionIds.Length; i++)
                if (isActive(EditionIds[i])) return true;
            return false;
        }

        /// <summary>The one-time warning logged when no edition is present. Lives here so tests can pin
        /// the wording and the façade cannot drift from it.</summary>
        public const string AbsentWarning =
            "[R&S PersistentWorld] No Regions and Societies edition detected. The population layer is disabled; nothing will be materialized.";
    }
}
