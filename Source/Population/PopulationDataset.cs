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

        private readonly Dictionary<int, int> tileIndex;   // world tile id -> index into snapshot.tiles

        public PopulationDataset(PopulationSnapshot snapshot, Individual[] people, int[] tileStart, long buildMillis, int linkedCount = 0)
        {
            this.snapshot = snapshot ?? PopulationSnapshot.Empty();
            this.people = people ?? Array.Empty<Individual>();
            this.tileStart = tileStart ?? new int[this.snapshot.tiles.Length + 1];
            this.buildMillis = buildMillis;
            this.linkedCount = linkedCount;
            buildSerial = this.snapshot.serial;
            tileIndex = new Dictionary<int, int>(this.snapshot.tiles.Length);
            for (int i = 0; i < this.snapshot.tiles.Length; i++) tileIndex[this.snapshot.tiles[i].tile] = i;
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
        public int RegionOf(in Individual person)
        {
            if (!tileIndex.TryGetValue(person.tile, out int i)) return -1;
            int r = snapshot.tiles[i].region;
            return r < 0 || r >= snapshot.regionIds.Length ? -1 : snapshot.regionIds[r];
        }

        /// <summary>A dataset with nobody in it, so consumers never see null.</summary>
        public static readonly PopulationDataset Empty = new PopulationDataset(PopulationSnapshot.Empty(), null, null, 0);
    }
}
