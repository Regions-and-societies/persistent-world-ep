using System;
using RegionsAndSocieties.Demographics;

namespace RegionsAndSocieties.PersistentWorld.Population
{
    /// <summary>
    /// Household assembly (#7): the residence model as actual families — who lives with whom. Pure and
    /// deterministic: a tile's households are a function of <c>(world seed, tile, population)</c>, so the
    /// same seed always yields the same households and a household is stable across rebuilds for as long
    /// as its tile's population is.
    ///
    /// <para>How many homes a tile has, and how full, comes from Core's <see cref="ResidenceRules"/> (a
    /// hamlet packs an extended family of ~7 into each home, a city ~1.8) — this EP does not restate that
    /// model. What it adds is the split: the tile's people, in index order, are cut into that many
    /// contiguous runs, each 1..<see cref="MaxOccupancy"/> long, with a seeded jitter so households of one
    /// tile are not all the same size. Household <c>h</c> of a tile is therefore the index slots
    /// <c>[start_h, start_h + size_h)</c>, keyed <c>(tile, h)</c>.</para>
    /// </summary>
    public static class HouseholdRules
    {
        /// <summary>The largest household the split will make — Core's rural (extended-family) occupancy.</summary>
        public const int MaxOccupancy = (int)ResidenceRules.RuralOccupancy;

        // Keeps household draws off the per-person seed stream and off Core's per-tile salts.
        private const int HouseholdSalt = 0x4855_5348;

        /// <summary>How many households a tile of <paramref name="population"/> has: Core's residence count,
        /// floored at the number needed to keep every household within <see cref="MaxOccupancy"/> (Core rounds
        /// 8–10 people to a single rural home; here they are two).</summary>
        public static int Count(int population)
        {
            if (population <= 0) return 0;
            int core = ResidenceRules.For(population).residences;
            int floor = (population + MaxOccupancy - 1) / MaxOccupancy;
            return core < floor ? floor : core;
        }

        /// <summary>
        /// Split <paramref name="population"/> people into <see cref="Count"/> household sizes that sum to
        /// the population, each between 1 and <see cref="MaxOccupancy"/>. Even split first, then seeded
        /// pairwise moves of one or two people that keep both households inside the band.
        /// </summary>
        public static int[] Sizes(int worldSeed, int tile, int population)
        {
            int n = Count(population);
            if (n <= 0) return Array.Empty<int>();
            var sizes = new int[n];
            int baseSize = population / n, extra = population % n;
            for (int i = 0; i < n; i++) sizes[i] = baseSize + (i < extra ? 1 : 0);
            if (n == 1) return sizes;

            uint rng = DemographicsRules.TileSeed(worldSeed, tile, HouseholdSalt);
            int moves = n;   // about one move per household: enough spread, still mostly around the mean
            for (int m = 0; m < moves; m++)
            {
                int from = DemographicsRules.RangeInt(ref rng, 0, n - 1);
                int to = DemographicsRules.RangeInt(ref rng, 0, n - 1);
                if (from == to) continue;
                int step = 1 + (DemographicsRules.NextFloat(ref rng) < 0.25f ? 1 : 0);   // usually one person, sometimes two
                if (sizes[from] - step >= 1 && sizes[to] + step <= MaxOccupancy)
                {
                    sizes[from] -= step;
                    sizes[to] += step;
                }
            }
            return sizes;
        }
    }

    /// <summary>
    /// The household layout of a whole dataset, built by the worker with it: for every tile, where each
    /// household's run starts. Two flat int arrays, ~one int per household, so lookups are an offset and a
    /// binary search and the store stays small even at 100k+ people.
    /// </summary>
    public sealed class HouseholdTable
    {
        /// <summary>tileFirst[t]..tileFirst[t+1] is tile t's households in <see cref="starts"/>; length tiles+1.</summary>
        public readonly int[] tileFirst;
        /// <summary>For each household, the index (within its tile) of its first member. A tile's households
        /// are consecutive here and ascending; the run of the last one ends at the tile's population.</summary>
        public readonly int[] starts;
        private readonly TileSlot[] tiles;

        private HouseholdTable(TileSlot[] tiles, int[] tileFirst, int[] starts)
        {
            this.tiles = tiles; this.tileFirst = tileFirst; this.starts = starts;
        }

        public int Count => starts.Length;

        public static readonly HouseholdTable Empty = new HouseholdTable(Array.Empty<TileSlot>(), new int[1], Array.Empty<int>());

        /// <summary>Lay out every home tile's households over its residents. O(households).</summary>
        public static HouseholdTable Build(int worldSeed, TileSlot[] tiles)
        {
            tiles = tiles ?? Array.Empty<TileSlot>();
            var tileFirst = new int[tiles.Length + 1];
            var all = new System.Collections.Generic.List<int>();
            for (int t = 0; t < tiles.Length; t++)
            {
                tileFirst[t] = all.Count;
                int[] sizes = HouseholdRules.Sizes(worldSeed, tiles[t].tile, tiles[t].population);
                int at = 0;
                for (int h = 0; h < sizes.Length; h++) { all.Add(at); at += sizes[h]; }
            }
            tileFirst[tiles.Length] = all.Count;
            return new HouseholdTable(tiles, tileFirst, all.ToArray());
        }

        /// <summary>How many households tile slot <paramref name="t"/> has.</summary>
        public int CountOnTile(int t) => t < 0 || t + 1 >= tileFirst.Length ? 0 : tileFirst[t + 1] - tileFirst[t];

        /// <summary>The member run of household <paramref name="h"/> on tile slot <paramref name="t"/>, as
        /// (first index within the tile, size). False if no such household.</summary>
        public bool TryMembers(int t, int h, out int first, out int size)
        {
            first = 0; size = 0;
            if (t < 0 || t + 1 >= tileFirst.Length || h < 0 || h >= CountOnTile(t)) return false;
            int k = tileFirst[t] + h;
            first = starts[k];
            int end = k + 1 < tileFirst[t + 1] ? starts[k + 1] : tiles[t].population;
            size = end - first;
            return size > 0;
        }

        /// <summary>Which household (index within the tile) person <paramref name="index"/> of tile slot
        /// <paramref name="t"/> belongs to, or -1.</summary>
        public int HouseholdOf(int t, int index)
        {
            if (t < 0 || t + 1 >= tileFirst.Length || index < 0 || index >= tiles[t].population) return -1;
            int lo = tileFirst[t], hi = tileFirst[t + 1] - 1;
            if (hi < lo) return -1;
            while (lo < hi)   // last start <= index
            {
                int mid = (lo + hi + 1) >> 1;
                if (starts[mid] <= index) lo = mid; else hi = mid - 1;
            }
            return lo - tileFirst[t];
        }
    }
}
