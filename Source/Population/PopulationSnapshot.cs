using System;
using System.Collections.Generic;

namespace RegionsAndSocieties.PersistentWorld.Population
{
    /// <summary>One populated tile in a snapshot: where it is, which region's profile it draws from, how many live there.</summary>
    public struct TileSlot
    {
        public int tile;        // world tile id
        public int region;      // index into <see cref="PopulationSnapshot.regionIds"/> / <c>profiles</c>; -1 = no region
        public int population;  // people on this tile (its source population, not the smeared field)
    }

    /// <summary>
    /// Everything the background worker needs to materialize the planet, copied out as plain numbers on
    /// the main thread and never mutated afterwards. Per-region distributions, per-tile head counts, and
    /// the catalogue labels — no Def, no Faction, no Find. Hundreds of regions, tens of thousands of
    /// tiles; cheap to take, safe to hand across threads.
    /// </summary>
    public sealed class PopulationSnapshot
    {
        public readonly int worldSeed;
        public readonly TileSlot[] tiles;          // populated tiles only, in ascending tile order
        public readonly int[] regionIds;           // region slot -> Core province id
        public readonly RegionProfile[] profiles;  // region slot -> distributions (never null entries)
        public readonly string[] raceLabels;
        public readonly string[] factionLabels;
        public readonly string[] ideoLabels;
        public readonly int densityVersion;        // Core's population cache version when taken
        public readonly int serial;                // increments per snapshot, so builds can be told apart

        private static int nextSerial;

        public PopulationSnapshot(int worldSeed, TileSlot[] tiles, int[] regionIds, RegionProfile[] profiles,
            string[] raceLabels, string[] factionLabels, string[] ideoLabels, int densityVersion)
        {
            this.worldSeed = worldSeed;
            this.tiles = tiles ?? Array.Empty<TileSlot>();
            this.regionIds = regionIds ?? Array.Empty<int>();
            this.profiles = profiles ?? Array.Empty<RegionProfile>();
            this.raceLabels = raceLabels ?? Array.Empty<string>();
            this.factionLabels = factionLabels ?? Array.Empty<string>();
            this.ideoLabels = ideoLabels ?? Array.Empty<string>();
            this.densityVersion = densityVersion;
            serial = System.Threading.Interlocked.Increment(ref nextSerial);
        }

        /// <summary>Sum of every tile's head count: the number of people a build of this snapshot yields.</summary>
        public long TotalPopulation
        {
            get
            {
                long total = 0;
                for (int i = 0; i < tiles.Length; i++) if (tiles[i].population > 0) total += tiles[i].population;
                return total;
            }
        }

        /// <summary>The profile a tile slot samples from; the empty profile for a tile outside any region.</summary>
        public RegionProfile ProfileFor(in TileSlot slot)
        {
            if (slot.region < 0 || slot.region >= profiles.Length) return RegionProfile.Empty();
            return profiles[slot.region] ?? RegionProfile.Empty();
        }

        /// <summary>An empty planet — what a world without Core, or before its first build, materializes to.</summary>
        public static PopulationSnapshot Empty(int worldSeed = 0)
            => new PopulationSnapshot(worldSeed, null, null, null, null, null, null, -1);

        /// <summary>A snapshot from a plain list of (tile, region, population) rows and region profiles —
        /// the constructor tests and other pure callers use.</summary>
        public static PopulationSnapshot From(int worldSeed, IList<TileSlot> rows, int[] regionIds, RegionProfile[] profiles,
            string[] raceLabels = null, string[] factionLabels = null, string[] ideoLabels = null, int densityVersion = 0)
        {
            var arr = new TileSlot[rows?.Count ?? 0];
            for (int i = 0; i < arr.Length; i++) arr[i] = rows[i];
            Array.Sort(arr, (a, b) => a.tile.CompareTo(b.tile));
            return new PopulationSnapshot(worldSeed, arr, regionIds, profiles, raceLabels, factionLabels, ideoLabels, densityVersion);
        }
    }
}
