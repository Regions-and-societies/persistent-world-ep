using System;
using RegionsAndSocieties.Demographics;

namespace RegionsAndSocieties.PersistentWorld.Population
{
    /// <summary>The cuts the index precomputes and a breakdown can be asked for.</summary>
    public enum Dimension
    {
        Region = 0,      // Core province (region slot; -1 = no region)
        Faction = 1,     // catalogue key (-1 = unowned)
        Xenotype = 2,    // catalogue key (-1 = plain human)
        Ideoligion = 3,  // catalogue key (-1 = none)
        AgeBucket = 4,   // child / working-age / elder
        Education = 5,   // five tiers
        Class = 6,       // four SES tiers
        Sector = 7,      // four sectors, among the employed only
        WorkStatus = 8,  // dependent / employed / unemployed / retired
        Sex = 9,         // male / female
        Linked = 10,     // derived / pawn-backed
    }

    /// <summary>
    /// The in-memory index over a <see cref="PopulationDataset"/> (#5), built by the worker as part of the
    /// build so no query ever pays for it. Two things: the people of each region as one contiguous run
    /// (a region's tiles are scattered through the tile-ordered people array, so region queries need
    /// their own ordering), and the marginal count of every dimension over the whole planet. Pure C#,
    /// no file I/O, no game types.
    /// </summary>
    public sealed class PopulationIndex
    {
        /// <summary>Indices into <c>people</c>, grouped by region slot; the last group is "no region".</summary>
        public readonly int[] order;
        /// <summary>regionStart[r]..regionStart[r+1] is region slot r's run in <see cref="order"/>; length regions+2
        /// (the extra group at the end is the no-region people).</summary>
        public readonly int[] regionStart;
        public readonly int regionCount;

        // Marginals over the whole dataset. Key-typed dimensions are offset by one so -1 lands in slot 0.
        public readonly int[] byRegion;      // by region slot (+1)
        public readonly int[] byFaction;     // by faction key (+1)
        public readonly int[] byXenotype;    // by race key (+1)
        public readonly int[] byIdeo;        // by ideo key (+1)
        public readonly int[] byAgeBucket;   // 3
        public readonly int[] byEducation;   // 5
        public readonly int[] byClass;       // 4
        public readonly int[] bySector;      // 4, employed only
        public readonly int[] byWork;        // 4
        public readonly int[] bySex;         // male, female
        public readonly int[] byLinked;      // derived, linked

        private PopulationIndex(int regionCount, int[] order, int[] regionStart,
            int[] byRegion, int[] byFaction, int[] byXenotype, int[] byIdeo, int[] byAgeBucket, int[] byEducation,
            int[] byClass, int[] bySector, int[] byWork, int[] bySex, int[] byLinked)
        {
            this.regionCount = regionCount; this.order = order; this.regionStart = regionStart;
            this.byRegion = byRegion; this.byFaction = byFaction; this.byXenotype = byXenotype; this.byIdeo = byIdeo;
            this.byAgeBucket = byAgeBucket; this.byEducation = byEducation; this.byClass = byClass; this.bySector = bySector;
            this.byWork = byWork; this.bySex = bySex; this.byLinked = byLinked;
        }

        /// <summary>Index a built population. O(people), one pass for the counts and one for the grouping.</summary>
        public static PopulationIndex Build(PopulationSnapshot snapshot, int[] tileStart, Individual[] people)
        {
            snapshot = snapshot ?? PopulationSnapshot.Empty();
            people = people ?? Array.Empty<Individual>();
            TileSlot[] tiles = snapshot.tiles;
            int regions = snapshot.regionIds.Length;

            int factions = 1 + snapshot.factionLabels.Length, races = 1 + snapshot.raceLabels.Length, ideos = 1 + snapshot.ideoLabels.Length;
            var byRegion = new int[regions + 1];
            var byFaction = new int[factions];
            var byXenotype = new int[races];
            var byIdeo = new int[ideos];
            var byAge = new int[AgeStructureRules.BucketCount];
            var byEdu = new int[EducationRules.TierCount];
            var byClass = new int[SocioeconomicRules.TierCount];
            var bySector = new int[EmploymentRules.SectorCount];
            var byWork = new int[4];
            var bySex = new int[2];
            var byLinked = new int[2];

            // Pass 1: marginals; region membership comes from the tile, not the person.
            var regionOfPerson = new int[people.Length];   // region slot, -1 = none
            for (int t = 0; t < tiles.Length && t + 1 < tileStart.Length; t++)
            {
                int region = tiles[t].region;
                if (region < 0 || region >= regions) region = -1;
                for (int i = tileStart[t]; i < tileStart[t + 1]; i++)
                {
                    regionOfPerson[i] = region;
                    ref readonly Individual p = ref people[i];
                    byRegion[region + 1]++;
                    byFaction[Slot(p.factionKey, factions)]++;
                    byXenotype[Slot(p.raceKey, races)]++;
                    byIdeo[Slot(p.ideoKey, ideos)]++;
                    byAge[Clamp((int)p.ageBucket, byAge.Length)]++;
                    byEdu[Clamp((int)p.education, byEdu.Length)]++;
                    byClass[Clamp((int)p.ses, byClass.Length)]++;
                    byWork[Clamp((int)p.work, byWork.Length)]++;
                    if (p.work == WorkStatus.Employed) bySector[Clamp((int)p.sector, bySector.Length)]++;
                    bySex[p.female ? 1 : 0]++;
                    byLinked[p.IsLinked ? 1 : 0]++;
                }
            }

            // Pass 2: counting sort of person indices by region slot (no-region last), stable in tile order.
            var regionStart = new int[regions + 2];
            for (int r = 0; r < regions; r++) regionStart[r + 1] = regionStart[r] + byRegion[r + 1];
            regionStart[regions + 1] = regionStart[regions] + byRegion[0];
            var cursor = new int[regions + 1];
            Array.Copy(regionStart, cursor, regions + 1);
            var order = new int[people.Length];
            for (int i = 0; i < people.Length; i++)
            {
                int r = regionOfPerson[i];
                int g = r < 0 ? regions : r;
                order[cursor[g]++] = i;
            }

            return new PopulationIndex(regions, order, regionStart, byRegion, byFaction, byXenotype, byIdeo, byAge, byEdu, byClass, bySector, byWork, bySex, byLinked);
        }

        /// <summary>The run of <see cref="order"/> holding region slot <paramref name="region"/> (-1 = no region).</summary>
        public void RegionRun(int region, out int start, out int end)
        {
            int g = region < 0 || region >= regionCount ? regionCount : region;
            start = regionStart[g];
            end = regionStart[g + 1];
        }

        /// <summary>The marginal counts of a dimension, indexed as documented on the field.</summary>
        public int[] Marginal(Dimension dimension)
        {
            switch (dimension)
            {
                case Dimension.Region: return byRegion;
                case Dimension.Faction: return byFaction;
                case Dimension.Xenotype: return byXenotype;
                case Dimension.Ideoligion: return byIdeo;
                case Dimension.AgeBucket: return byAgeBucket;
                case Dimension.Education: return byEducation;
                case Dimension.Class: return byClass;
                case Dimension.Sector: return bySector;
                case Dimension.WorkStatus: return byWork;
                case Dimension.Sex: return bySex;
                default: return byLinked;
            }
        }

        private static int Slot(int key, int size) => key < -1 || key + 1 >= size ? 0 : key + 1;
        private static int Clamp(int v, int size) => v < 0 ? 0 : (v >= size ? size - 1 : v);
    }
}
