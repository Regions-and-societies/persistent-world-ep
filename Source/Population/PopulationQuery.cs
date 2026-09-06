using System;
using System.Collections.Generic;
using RegionsAndSocieties.Demographics;

namespace RegionsAndSocieties.PersistentWorld.Population
{
    /// <summary>
    /// What to count. Every field is optional: leave it at its default and that axis is not filtered.
    /// A plain class with public fields so a consumer working by reflection can build one with
    /// <c>Activator.CreateInstance</c> and <c>FieldInfo.SetValue</c>.
    /// </summary>
    public sealed class PopulationFilter
    {
        public int regionId = int.MinValue;   // Core province id; int.MinValue = any; -1 = people in no region
        public int factionKey = int.MinValue; // catalogue keys; int.MinValue = any; -1 = none
        public int raceKey = int.MinValue;
        public int ideoKey = int.MinValue;
        public int sex = -1;                  // -1 any, 0 male, 1 female
        public int minAge = int.MinValue;     // inclusive
        public int maxAge = int.MaxValue;     // inclusive
        public int ageBucket = -1;            // -1 any, else AgeBucket ordinal
        public int education = -1;            // -1 any, else EducationTier ordinal
        public int minEducation = -1;         // inclusive lower bound on the tier ordinal, -1 = none
        public int ses = -1;                  // -1 any, else SesTier ordinal
        public int minSes = -1;
        public int work = -1;                 // -1 any, else WorkStatus ordinal
        public int sector = -1;               // -1 any, else OccupationSector ordinal (implies employed)
        public int linked = -1;               // -1 any, 0 derived only, 1 pawn-backed only
        public int tile = int.MinValue;       // one home tile (where people live now); int.MinValue = any
        public int birthTile = int.MinValue;  // one birth tile; int.MinValue = any
        public int moved = -1;                // -1 any, 0 living where born, 1 living elsewhere
        public int household = int.MinValue;  // one household (index within the tile; needs tile); int.MinValue = any
        public int minHouseholdSize = -1;     // inclusive bounds on the size of the person's household, -1 = none
        public int maxHouseholdSize = -1;

        public bool IsUnfiltered =>
            regionId == int.MinValue && factionKey == int.MinValue && raceKey == int.MinValue && ideoKey == int.MinValue
            && sex < 0 && minAge == int.MinValue && maxAge == int.MaxValue && ageBucket < 0 && education < 0 && minEducation < 0
            && ses < 0 && minSes < 0 && work < 0 && sector < 0 && linked < 0 && tile == int.MinValue
            && birthTile == int.MinValue && moved < 0
            && household == int.MinValue && minHouseholdSize < 0 && maxHouseholdSize < 0;

        public static readonly PopulationFilter All = new PopulationFilter();

        /// <summary>Does this person pass every set axis? Region is checked by the caller (it lives on the tile).</summary>
        public bool Matches(in Individual p)
        {
            if (factionKey != int.MinValue && p.factionKey != factionKey) return false;
            if (raceKey != int.MinValue && p.raceKey != raceKey) return false;
            if (ideoKey != int.MinValue && p.ideoKey != ideoKey) return false;
            if (sex >= 0 && (p.female ? 1 : 0) != sex) return false;
            if (p.age < minAge || p.age > maxAge) return false;
            if (ageBucket >= 0 && (int)p.ageBucket != ageBucket) return false;
            if (education >= 0 && (int)p.education != education) return false;
            if (minEducation >= 0 && (int)p.education < minEducation) return false;
            if (ses >= 0 && (int)p.ses != ses) return false;
            if (minSes >= 0 && (int)p.ses < minSes) return false;
            if (work >= 0 && (int)p.work != work) return false;
            if (sector >= 0 && (p.work != WorkStatus.Employed || (int)p.sector != sector)) return false;
            if (linked >= 0 && (p.IsLinked ? 1 : 0) != linked) return false;
            if (tile != int.MinValue && p.tile != tile) return false;
            if (birthTile != int.MinValue && p.birthTile != birthTile) return false;
            if (moved >= 0 && (p.Moved ? 1 : 0) != moved) return false;
            if (household != int.MinValue && p.household != household) return false;
            if (minHouseholdSize >= 0 && p.householdSize < minHouseholdSize) return false;
            if (maxHouseholdSize >= 0 && p.householdSize > maxHouseholdSize) return false;
            return true;
        }
    }

    /// <summary>
    /// The query engine (#5): counts and breakdowns over a <see cref="PopulationDataset"/>. An unfiltered
    /// count or breakdown is answered from the index's marginals in O(1); a region-scoped query scans only
    /// that region's run; anything else is one linear pass over a struct array, which at 100k+ people is
    /// a few milliseconds. No allocation on the count path.
    /// </summary>
    public static class PopulationQuery
    {
        /// <summary>How many people match.</summary>
        public static int Count(PopulationDataset ds, PopulationFilter filter)
        {
            if (ds == null) return 0;
            filter = filter ?? PopulationFilter.All;
            if (filter.IsUnfiltered) return ds.Count;

            int n = 0;
            Scan(ds, filter, (in Individual p) => n++);
            return n;
        }

        /// <summary>The matching people split along <paramref name="dimension"/>. Indexing follows
        /// <see cref="PopulationIndex"/>: key-typed dimensions are offset by one (slot 0 = "none").</summary>
        public static int[] Breakdown(PopulationDataset ds, PopulationFilter filter, Dimension dimension)
        {
            if (ds == null) return Array.Empty<int>();
            filter = filter ?? PopulationFilter.All;
            if (filter.IsUnfiltered)
            {
                int[] m = ds.index.Marginal(dimension);
                var copy = new int[m.Length];
                Array.Copy(m, copy, m.Length);
                return copy;
            }

            var counts = new int[BreakdownSize(ds, dimension)];
            Scan(ds, filter, (in Individual p) =>
            {
                int slot = SlotOf(ds, in p, dimension);
                if (slot >= 0 && slot < counts.Length) counts[slot]++;
            });
            return counts;
        }

        /// <summary>The indices (into <c>ds.people</c>) of the matching people, in tile order — or region
        /// order when the filter names a region. Capped at <paramref name="limit"/> (0 = no cap).</summary>
        public static List<int> Select(PopulationDataset ds, PopulationFilter filter, int limit = 0)
        {
            var into = new List<int>();
            if (ds == null) return into;
            filter = filter ?? PopulationFilter.All;
            Scan(ds, filter, (in Individual p) =>
            {
                if (limit > 0 && into.Count >= limit) return;
                into.Add(ds.PositionOf(p.id));
            });
            return into;
        }

        /// <summary>How many slots a breakdown along <paramref name="dimension"/> has for this dataset.</summary>
        public static int BreakdownSize(PopulationDataset ds, Dimension dimension)
            => ds.index.Marginal(dimension).Length;

        /// <summary>Which breakdown slot a person falls in along a dimension.</summary>
        public static int SlotOf(PopulationDataset ds, in Individual p, Dimension dimension)
        {
            switch (dimension)
            {
                case Dimension.Region: return ds.RegionSlotOf(in p) + 1;
                case Dimension.Faction: return p.factionKey + 1;
                case Dimension.Xenotype: return p.raceKey + 1;
                case Dimension.Ideoligion: return p.ideoKey + 1;
                case Dimension.AgeBucket: return (int)p.ageBucket;
                case Dimension.Education: return (int)p.education;
                case Dimension.Class: return (int)p.ses;
                case Dimension.Sector: return p.work == WorkStatus.Employed ? (int)p.sector : -1;
                case Dimension.WorkStatus: return (int)p.work;
                case Dimension.Sex: return p.female ? 1 : 0;
                case Dimension.Linked: return p.IsLinked ? 1 : 0;
                default: return p.household < 0 ? 0 : Math.Min(p.householdSize, HouseholdRules.MaxOccupancy);
            }
        }

        public delegate void Visitor(in Individual person);

        /// <summary>Visit every matching person. Region-scoped filters walk only that region's run.</summary>
        public static void Scan(PopulationDataset ds, PopulationFilter filter, Visitor visit)
        {
            if (ds == null || visit == null) return;
            filter = filter ?? PopulationFilter.All;
            Individual[] people = ds.people;

            if (filter.tile != int.MinValue)
            {
                if (!ds.TryTileRange(filter.tile, out int start, out int count)) return;
                if (filter.regionId != int.MinValue && ds.RegionOfTile(filter.tile) != filter.regionId) return;
                for (int i = start; i < start + count; i++)
                    if (filter.Matches(in people[i])) visit(in people[i]);
                return;
            }

            if (filter.regionId != int.MinValue)
            {
                int slot = ds.RegionSlotOfId(filter.regionId);
                if (slot == int.MinValue) return;   // unknown region id: nobody
                ds.index.RegionRun(slot, out int rs, out int re);
                int[] order = ds.index.order;
                for (int k = rs; k < re; k++)
                    if (filter.Matches(in people[order[k]])) visit(in people[order[k]]);
                return;
            }

            for (int i = 0; i < people.Length; i++)
                if (filter.Matches(in people[i])) visit(in people[i]);
        }
    }
}
