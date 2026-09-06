using System.Reflection;
using HarmonyLib;
using Verse;
using RegionsAndSocieties.PersistentWorld.Integration;

namespace RegionsAndSocieties.PersistentWorld
{
    /// <summary>
    /// Entry point: applies this assembly's Harmony patches and warns once if no Regions and Societies
    /// edition is present. Every path that touches an R&amp;S type is gated behind <see cref="Enabled"/>
    /// (there is no hard modDependency, because the two R&amp;S editions are mutually exclusive), so this
    /// mod loads harmlessly even when R&amp;S is absent — the population layer simply stays off.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class PersistentWorldInit
    {
        public const string HarmonyId = "regionsandsocieties.persistentworld";

        /// <summary>Decided once at startup; the active mod list does not change during a session.</summary>
        public static readonly bool Enabled;

        /// <summary>True when the SQLite runtime loaded (#17). False means "no database": the census still
        /// runs in memory and the save keeps its packed overlay copy.</summary>
        public static bool DatabaseAvailable => SqliteRuntime.Available;

        static PersistentWorldInit()
        {
            new Harmony(HarmonyId).PatchAll(Assembly.GetExecutingAssembly());
            Enabled = CorePresent();
            if (!Enabled) Log.Warning(CorePresence.AbsentWarning);
            else SqliteRuntime.Initialize(SqliteRuntime.ModRoot());
        }

        /// <summary>True when either R&amp;S edition is active. Uses ModLister only — never touches an R&amp;S
        /// type — so it is safe to call even when the R&amp;S assembly is not loaded.</summary>
        public static bool CorePresent()
        {
            return CorePresence.IsPresent(id =>
                ModLister.GetActiveModWithIdentifier(id, true) != null
                || ModLister.GetActiveModWithIdentifier(id) != null);
        }
    }
}
