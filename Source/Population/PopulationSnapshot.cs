using System;
using System.Collections.Generic;

namespace RegionsAndSocieties.PersistentWorld.Population
{
    /// <summary>A tile with people: where it is, which region's profile it draws from, how many are born there
    /// (in a snapshot) or live there (in a dataset's home-tile table).</summary>
    public struct TileSlot
    {
        public int tile;        // world tile id
        public int region;      // index into <see cref="PopulationSnapshot.regionIds"/> / <c>profiles</c>; -1 = no region
        public int population;  // people
    }

    /// <summary>
    /// Everything the background worker needs to materialize the planet, copied out as plain numbers on
    /// the main thread and never mutated afterwards: per-region distributions, per-tile birth counts, the
    /// catalogue labels, the sparse overlay (#9) and the world-pawn links (#4). No Def, no Faction, no
    /// Find. Hundreds of regions, tens of thousands of tiles; cheap to take, safe to hand across threads.
    /// </summary>
    public sealed class PopulationSnapshot
    {
        public readonly int worldSeed;
        public readonly TileSlot[] tiles;          // birth tiles with people, ascending tile order
        public readonly int[] regionIds;           // region slot -> Core province id
        public readonly RegionProfile[] profiles;  // region slot -> distributions (null entries read as Empty)
        public readonly string[] raceLabels;
        public readonly string[] factionLabels;
        public readonly string[] ideoLabels;
        public readonly PersonDelta[] deltas;      // the overlay, sorted by id
        public readonly LinkedPerson[] linked;     // people who are real world pawns, sorted by id
        public readonly int densityVersion;        // Core's population cache version when taken
        public readonly int serial;                // increments per snapshot, so builds can be told apart

        private readonly Dictionary<int, int> tileSlot;   // tile id -> index into tiles
        private static int nextSerial;

        public PopulationSnapshot(int worldSeed, TileSlot[] tiles, int[] regionIds, RegionProfile[] profiles,
            string[] raceLabels, string[] factionLabels, string[] ideoLabels, int densityVersion,
            PersonDelta[] deltas = null, LinkedPerson[] linked = null)
        {
            this.worldSeed = worldSeed;
            this.tiles = tiles ?? Array.Empty<TileSlot>();
            this.regionIds = regionIds ?? Array.Empty<int>();
            this.profiles = profiles ?? Array.Empty<RegionProfile>();
            this.raceLabels = raceLabels ?? Array.Empty<string>();
            this.factionLabels = factionLabels ?? Array.Empty<string>();
            this.ideoLabels = ideoLabels ?? Array.Empty<string>();
            this.deltas = deltas ?? Array.Empty<PersonDelta>();
            this.linked = linked ?? Array.Empty<LinkedPerson>();
            this.densityVersion = densityVersion;
            serial = System.Threading.Interlocked.Increment(ref nextSerial);
            tileSlot = new Dictionary<int, int>(this.tiles.Length);
            for (int i = 0; i < this.tiles.Length; i++) tileSlot[this.tiles[i].tile] = i;
        }

        /// <summary>Sum of every tile's birth count: the number of people born into this snapshot.</summary>
        public long TotalPopulation
        {
            get
            {
                long total = 0;
                for (int i = 0; i < tiles.Length; i++) if (tiles[i].population > 0) total += tiles[i].population;
                return total;
            }
        }

        /// <summary>The index into <see cref="tiles"/> of a birth tile, or -1.</summary>
        public int TileSlotOf(int tile) => tileSlot.TryGetValue(tile, out int i) ? i : -1;

        /// <summary>How many people are born on a tile (0 for a tile not in the snapshot).</summary>
        public int BirthPopulationOf(int tile) => tileSlot.TryGetValue(tile, out int i) ? Math.Max(0, tiles[i].population) : 0;

        /// <summary>The region slot of a tile, or -1 when the tile is unknown to this snapshot. A tile people
        /// moved to that has no births is unknown here; 0.3.0 widens this to every land tile.</summary>
        public int RegionSlotOfTile(int tile)
        {
            if (!tileSlot.TryGetValue(tile, out int i)) return -1;
            int r = tiles[i].region;
            return r < 0 || r >= regionIds.Length ? -1 : r;
        }

        /// <summary>The profile a region slot samples from; the empty profile for -1 or a missing entry.</summary>
        public RegionProfile ProfileFor(int regionSlot)
        {
            if (regionSlot < 0 || regionSlot >= profiles.Length) return RegionProfile.Empty();
            return profiles[regionSlot] ?? RegionProfile.Empty();
        }

        /// <summary>An empty planet — what a world without Core, or before its first build, materializes to.</summary>
        public static PopulationSnapshot Empty(int worldSeed = 0)
            => new PopulationSnapshot(worldSeed, null, null, null, null, null, null, -1);

        /// <summary>A snapshot from a plain list of (tile, region, births) rows and region profiles —
        /// the constructor tests and other pure callers use. Rows are sorted by tile.</summary>
        public static PopulationSnapshot From(int worldSeed, IList<TileSlot> rows, int[] regionIds, RegionProfile[] profiles,
            string[] raceLabels = null, string[] factionLabels = null, string[] ideoLabels = null, int densityVersion = 0,
            PersonDelta[] deltas = null, LinkedPerson[] linked = null)
        {
            var arr = new TileSlot[rows?.Count ?? 0];
            for (int i = 0; i < arr.Length; i++) arr[i] = rows[i];
            Array.Sort(arr, (a, b) => a.tile.CompareTo(b.tile));
            if (deltas != null) Array.Sort(deltas, (a, b) => a.id.CompareTo(b.id));
            if (linked != null) Array.Sort(linked, (a, b) => a.id.CompareTo(b.id));
            return new PopulationSnapshot(worldSeed, arr, regionIds, profiles, raceLabels, factionLabels, ideoLabels, densityVersion, deltas, linked);
        }
    }
}
