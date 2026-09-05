using System;
using System.Collections.Generic;

namespace RegionsAndSocieties.PersistentWorld.Population
{
    /// <summary>
    /// A completed materialization: every derived person on the planet, laid out tile by tile in one flat
    /// array, plus the snapshot it was built from. Immutable once published — queries only ever read the
    /// last completed dataset, never one under construction. This is the in-memory store the query index
    /// (#5), households (#7) and the export (#6) all read.
    /// </summary>
    public sealed class PopulationDataset
    {
        public readonly PopulationSnapshot snapshot;
        public readonly Individual[] people;   // grouped by tile, in snapshot.tiles order, index ascending within a tile
        public readonly int[] tileStart;       // tileStart[i] = offset of snapshot.tiles[i]'s first person; length tiles+1
        public readonly int buildSerial;       // matches snapshot.serial
        public readonly long buildMillis;      // wall-clock cost of the build, for the debug dump
        public readonly int linkedCount;       // slots backed by a real world pawn (#4)
        public readonly PopulationIndex index; // region runs + marginals (#5), built with the dataset

        private readonly Dictionary<int, int> tileIndex;     // world tile id -> index into snapshot.tiles
        private readonly Dictionary<int, int> regionSlots;   // Core province id -> region slot

        public PopulationDataset(PopulationSnapshot snapshot, Individual[] people, int[] tileStart, long buildMillis, int linkedCount = 0, PopulationIndex index = null)
        {
            this.snapshot = snapshot ?? PopulationSnapshot.Empty();
            this.people = people ?? Array.Empty<Individual>();
            this.tileStart = tileStart ?? new int[this.snapshot.tiles.Length + 1];
            this.buildMillis = buildMillis;
            this.linkedCount = linkedCount;
            buildSerial = this.snapshot.serial;
            tileIndex = new Dictionary<int, int>(this.snapshot.tiles.Length);
            for (int i = 0; i < this.snapshot.tiles.Length; i++) tileIndex[this.snapshot.tiles[i].tile] = i;
            regionSlots = new Dictionary<int, int>(this.snapshot.regionIds.Length);
            for (int r = 0; r < this.snapshot.regionIds.Length; r++) regionSlots[this.snapshot.regionIds[r]] = r;
            this.index = index ?? PopulationIndex.Build(this.snapshot, this.tileStart, this.people);
        }

        public int Count => people.Length;
        public int TileCount => snapshot.tiles.Length;
        public int LinkedCount => linkedCount;

        /// <summary>The range of <see cref="people"/> living on world tile <paramref name="tile"/>; false when
        /// the tile has nobody (or is not in the snapshot).</summary>
        public bool TryTileRange(int tile, out int start, out int count)
        {
            if (tileIndex.TryGetValue(tile, out int i))
            {
                start = tileStart[i];
                count = tileStart[i + 1] - start;
                return count > 0;
            }
            start = 0; count = 0;
            return false;
        }

        /// <summary>Person <paramref name="index"/> on <paramref name="tile"/>, or false if no such slot.</summary>
        public bool TryGet(int tile, int index, out Individual person)
        {
            if (TryTileRange(tile, out int start, out int count) && index >= 0 && index < count)
            {
                person = people[start + index];
                return true;
            }
            person = default;
            return false;
        }

        /// <summary>The Core province id a person belongs to, or -1.</summary>
        public int RegionOf(in Individual person) => RegionOfTile(person.tile);

        /// <summary>The Core province id of a world tile in this dataset, or -1.</summary>
        public int RegionOfTile(int tile)
        {
            int r = RegionSlotOfTile(tile);
            return r < 0 ? -1 : snapshot.regionIds[r];
        }

        /// <summary>The region slot (index into snapshot.regionIds) of a person, or -1.</summary>
        public int RegionSlotOf(in Individual person) => RegionSlotOfTile(person.tile);

        private int RegionSlotOfTile(int tile)
        {
            if (!tileIndex.TryGetValue(tile, out int i)) return -1;
            int r = snapshot.tiles[i].region;
            return r < 0 || r >= snapshot.regionIds.Length ? -1 : r;
        }

        /// <summary>The region slot of a Core province id: -1 for "no region" (id -1), int.MinValue if the id
        /// is not in this dataset at all.</summary>
        public int RegionSlotOfId(int regionId)
        {
            if (regionId == -1) return -1;
            return regionSlots.TryGetValue(regionId, out int slot) ? slot : int.MinValue;
        }

        /// <summary>Every Core province id in this dataset, in region-slot order.</summary>
        public int[] RegionIds => snapshot.regionIds;

        /// <summary>A dataset with nobody in it, so consumers never see null.</summary>
        public static readonly PopulationDataset Empty = new PopulationDataset(PopulationSnapshot.Empty(), null, null, 0);
    }
}
