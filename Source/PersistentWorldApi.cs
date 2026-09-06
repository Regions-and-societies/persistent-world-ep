using System;
using System.Collections.Generic;
using RegionsAndSocieties.Demographics;
using RegionsAndSocieties.PersistentWorld.Integration;
using RegionsAndSocieties.PersistentWorld.Population;

namespace RegionsAndSocieties.PersistentWorld
{
    /// <summary>
    /// The public query surface (#5) — what other mods, overlays, panels and the debug dump call. One
    /// static class, plain argument types, no exceptions on the happy path, reflection-friendly:
    /// <c>Type.GetType("RegionsAndSocieties.PersistentWorld.PersistentWorldApi, RegionsAndSocieties.PersistentWorld")</c>
    /// and go. Every call reads the last <i>completed</i> dataset of the current world and is a no-op
    /// (zero, empty, false) when there is no world, no Core, or no build yet.
    ///
    /// <para>People are addressed by their id (#9), which never changes; where they live is an attribute.
    /// Everything here is main-thread-agnostic: the dataset is immutable and the reference is swapped
    /// atomically, so a caller on any thread sees one consistent planet. Only <see cref="RequestRebuild"/>,
    /// <see cref="RebuildNow"/> and <see cref="MovePerson"/> touch game state and must run on the main thread.</para>
    /// </summary>
    public static class PersistentWorldApi
    {
        /// <summary>True when a Core edition is loaded and a world is live.</summary>
        public static bool IsAvailable => PersistentWorldInit.Enabled && PersistentWorldComponent.Instance != null;

        /// <summary>True once at least one build has been published for this world.</summary>
        public static bool HasData => Dataset.Count > 0 || Dataset.buildSerial > 0;

        /// <summary>The build serial of the current dataset. Changes on every swap, so a consumer can cache by it.</summary>
        public static int Version => Dataset.buildSerial;

        /// <summary>The current dataset — typed access for consumers that reference this assembly directly.</summary>
        public static PopulationDataset Dataset => PersistentWorldComponent.Dataset;

        /// <summary>Everyone alive on the planet.</summary>
        public static int TotalPopulation() => Dataset.Count;

        /// <summary>How many people live in a Core province (id from Core's region manager).</summary>
        public static int CountInRegion(int provinceId) => PopulationQuery.Count(Dataset, new PopulationFilter { regionId = provinceId });

        /// <summary>How many people live on one world tile.</summary>
        public static int CountOnTile(int tile) => Dataset.TryTileRange(tile, out _, out int count) ? count : 0;

        /// <summary>How many people match the filter.</summary>
        public static int Count(PopulationFilter filter) => PopulationQuery.Count(Dataset, filter);

        /// <summary>The matching people split along a dimension, as label → count. Labels are the catalogue's
        /// (faction names, xenotype labels, ideoligion names) or Core's enum names; "none" is the -1 slot.</summary>
        public static Dictionary<string, int> Breakdown(PopulationFilter filter, Dimension dimension)
        {
            PopulationDataset ds = Dataset;
            int[] counts = PopulationQuery.Breakdown(ds, filter, dimension);
            var result = new Dictionary<string, int>(counts.Length);
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] == 0) continue;
                string label = SlotLabel(ds, dimension, i);
                result.TryGetValue(label, out int prior);
                result[label] = prior + counts[i];
            }
            return result;
        }

        /// <summary>Reflection-friendly overload: dimension by name (see <see cref="Dimension"/>).</summary>
        public static Dictionary<string, int> Breakdown(PopulationFilter filter, string dimension)
        {
            return Enum.TryParse(dimension, true, out Dimension d) ? Breakdown(filter, d) : new Dictionary<string, int>();
        }

        /// <summary>The id of the person born at <paramref name="birthIndex"/> on <paramref name="birthTile"/>
        /// in the current world. Pure arithmetic; says nothing about whether they are alive.</summary>
        public static long IdOf(int birthTile, int birthIndex) => PersonId.Make(Dataset.snapshot.worldSeed, birthTile, birthIndex);

        /// <summary>The person with this id, if alive.</summary>
        public static bool TryGetPerson(long id, out Individual person) => Dataset.TryGetById(id, out person);

        /// <summary>The person born at these coordinates, wherever they live now, if alive.</summary>
        public static bool TryGetPersonBorn(int birthTile, int birthIndex, out Individual person) => Dataset.TryGetBorn(birthTile, birthIndex, out person);

        /// <summary>Resident <paramref name="n"/> of a home tile, in birth order.</summary>
        public static bool TryGetResident(int tile, int n, out Individual person) => Dataset.TryGetResident(tile, n, out person);

        /// <summary>The person a real world pawn is, if it is linked (#4).</summary>
        public static bool TryGetPawnPerson(int pawnThingId, out long id)
        {
            id = 0;
            var links = PersistentWorldComponent.Instance?.Links;
            if (links == null || !links.TryGet(pawnThingId, out PawnLink link)) return false;
            id = link.id;
            return true;
        }

        /// <summary>Indices into <see cref="PopulationDataset.people"/> of the matching people, capped at <paramref name="limit"/> (0 = all).</summary>
        public static List<int> Select(PopulationFilter filter, int limit = 0) => PopulationQuery.Select(Dataset, filter, limit);

        /// <summary>Households on the planet (#7).</summary>
        public static int TotalHouseholds() => Dataset.HouseholdCount;

        /// <summary>Households on one home tile.</summary>
        public static int HouseholdsOnTile(int tile) => Dataset.HouseholdsOnTile(tile);

        /// <summary>The household a person belongs to and how many live in it. False if no such person.</summary>
        public static bool TryGetHousehold(long id, out int tile, out int household, out int size)
        {
            tile = -1; household = -1; size = 0;
            if (!Dataset.TryGetById(id, out Individual p)) return false;
            tile = p.tile; household = p.household; size = p.householdSize;
            return household >= 0;
        }

        /// <summary>The members of household <paramref name="household"/> on a home tile, as a run of
        /// <see cref="PopulationDataset.people"/>: (array start, size).</summary>
        public static bool TryGetHouseholdMembers(int tile, int household, out int start, out int size)
            => Dataset.TryHousehold(tile, household, out start, out size);

        /// <summary>The head of a household: its oldest adult, else its oldest member.</summary>
        public static bool TryGetHouseholdHead(int tile, int household, out Individual head)
            => Dataset.TryHouseholdHead(tile, household, out head);

        /// <summary>People living somewhere other than where they were born.</summary>
        public static int MovedCount() => Dataset.MovedCount;

        /// <summary>Change where a person lives (main thread). Takes effect in the next build; the change is
        /// persisted in the overlay (#9). Returns false if the person is unknown to the current dataset.
        /// This is the seam the 0.3.0 dynamics and consumer mods drive movement through.</summary>
        public static bool MovePerson(long id, int homeTile)
        {
            var comp = PersistentWorldComponent.Instance;
            if (comp == null || !Dataset.TryGetById(id, out Individual p)) return false;
            if (comp.Overlay.Move(id, p.birthTile, p.birthIndex, homeTile)) comp.RequestRebuild();
            return true;
        }

        /// <summary>True when the world's SQLite database is open (#17). False means in-memory only.</summary>
        public static bool DatabaseAvailable => PersistentWorldComponent.Instance?.Database?.Ready == true;

        /// <summary>The SQL door (#20): one read-only SELECT over the census tables (people, households, links,
        /// labels, regions, tiles, builds) and the lineage (commits, deltas). Rows come back as column → value
        /// maps, at most <paramref name="limit"/> (0 = all). Empty when there is no database or the statement
        /// fails; writes are refused. Safe from any thread.</summary>
        public static List<Dictionary<string, object>> Query(string sql, int limit = 1000)
        {
            var db = PersistentWorldComponent.Instance?.Database;
            if (db == null || string.IsNullOrEmpty(sql)) return new List<Dictionary<string, object>>();
            return db.Run("query", c => Db.CensusStore.Query(c, sql, limit), new List<Dictionary<string, object>>());
        }

        /// <summary>Ask the loop for a fresh build at the next tick (main thread). No-op without a world.</summary>
        public static void RequestRebuild() => PersistentWorldComponent.Request();

        /// <summary>Snapshot, build and publish right now on the calling thread (main thread; for debug paths).</summary>
        public static PopulationDataset RebuildNow() => PersistentWorldComponent.Instance?.BuildNow() ?? PopulationDataset.Empty;

        /// <summary>The human label of a catalogue key or enum ordinal along a dimension.</summary>
        public static string Label(Dimension dimension, int keyOrOrdinal)
            => SlotLabel(Dataset, dimension, dimension <= Dimension.Ideoligion ? keyOrOrdinal + 1 : keyOrOrdinal);

        // Labels: key-typed dimensions read the snapshot's captured label tables; the rest use Core's enums.
        private static string SlotLabel(PopulationDataset ds, Dimension dimension, int slot)
        {
            PopulationSnapshot s = ds.snapshot;
            switch (dimension)
            {
                case Dimension.Region: return slot == 0 ? "no region" : "region " + At(s.regionIds, slot - 1);
                case Dimension.Faction: return slot == 0 ? "unowned" : At(s.factionLabels, slot - 1, "faction " + (slot - 1));
                case Dimension.Xenotype: return slot == 0 ? "Human" : At(s.raceLabels, slot - 1, "xenotype " + (slot - 1));
                case Dimension.Ideoligion: return slot == 0 ? "none" : At(s.ideoLabels, slot - 1, "ideoligion " + (slot - 1));
                case Dimension.AgeBucket: return ((AgeBucket)slot).ToString();
                case Dimension.Education: return ((EducationTier)slot).ToString();
                case Dimension.Class: return ((SesTier)slot).ToString();
                case Dimension.Sector: return ((OccupationSector)slot).ToString();
                case Dimension.WorkStatus: return ((WorkStatus)slot).ToString();
                case Dimension.Sex: return slot == 1 ? "Female" : "Male";
                case Dimension.Linked: return slot == 1 ? "Linked" : "Derived";
                default: return slot == 0 ? "no household" : "household of " + slot;
            }
        }

        private static string At(string[] labels, int i, string fallback = null)
            => labels != null && i >= 0 && i < labels.Length && labels[i] != null ? labels[i] : (fallback ?? i.ToString());
        private static string At(int[] ids, int i) => ids != null && i >= 0 && i < ids.Length ? ids[i].ToString() : i.ToString();
    }
}
