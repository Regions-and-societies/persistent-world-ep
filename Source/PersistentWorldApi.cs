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
    /// <para>Everything here is main-thread-agnostic: the dataset is immutable and the reference is
    /// swapped atomically, so a caller on any thread sees one consistent planet. Only
    /// <see cref="RequestRebuild"/> and <see cref="RebuildNow"/> touch game state and must run on the main thread.</para>
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

        /// <summary>Everyone on the planet.</summary>
        public static int TotalPopulation() => Dataset.Count;

        /// <summary>How many people in a Core province (id from Core's region manager).</summary>
        public static int CountInRegion(int provinceId) => PopulationQuery.Count(Dataset, new PopulationFilter { regionId = provinceId });

        /// <summary>How many people on one world tile.</summary>
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

        /// <summary>The person in a slot, if the slot exists.</summary>
        public static bool TryGetPerson(int tile, int index, out Individual person) => Dataset.TryGet(tile, index, out person);

        /// <summary>The slot a real world pawn holds, if it is linked (#4).</summary>
        public static bool TryGetPawnSlot(int pawnThingId, out int tile, out int index)
        {
            tile = -1; index = -1;
            var links = PersistentWorldComponent.Instance?.Links;
            if (links == null || !links.TryGet(pawnThingId, out PawnSlot slot)) return false;
            tile = slot.tile; index = slot.index;
            return true;
        }

        /// <summary>Indices into <see cref="PopulationDataset.people"/> of the matching people, capped at <paramref name="limit"/> (0 = all).</summary>
        public static List<int> Select(PopulationFilter filter, int limit = 0) => PopulationQuery.Select(Dataset, filter, limit);

        /// <summary>The human label of a catalogue key or enum ordinal along a dimension.</summary>
        public static string Label(Dimension dimension, int keyOrOrdinal)
            => SlotLabel(Dataset, dimension, dimension <= Dimension.Ideoligion ? keyOrOrdinal + 1 : keyOrOrdinal);

        /// <summary>Ask the loop for a fresh build at the next tick (main thread). No-op without a world.</summary>
        public static void RequestRebuild() => PersistentWorldComponent.Request();

        /// <summary>Snapshot, build and publish right now on the calling thread (main thread; for debug paths).</summary>
        public static PopulationDataset RebuildNow() => PersistentWorldComponent.Instance?.BuildNow() ?? PopulationDataset.Empty;

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
                default: return slot == 1 ? "Linked" : "Derived";
            }
        }

        private static string At(string[] labels, int i, string fallback = null)
            => labels != null && i >= 0 && i < labels.Length && labels[i] != null ? labels[i] : (fallback ?? i.ToString());
        private static string At(int[] ids, int i) => ids != null && i >= 0 && i < ids.Length ? ids[i].ToString() : i.ToString();
    }
}
