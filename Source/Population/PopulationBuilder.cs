using System;
using System.Diagnostics;
using System.Threading;

namespace RegionsAndSocieties.PersistentWorld.Population
{
    /// <summary>
    /// The compute step of snapshot → compute → swap (#3): runs the deterministic sampler over every
    /// populated tile of a <see cref="PopulationSnapshot"/> and lays the result out as a
    /// <see cref="PopulationDataset"/>. This is where the O(population) work lives — off the game loop,
    /// on a background thread. Reads only the snapshot; touches no game state; allocates the output once.
    /// </summary>
    public static class PopulationBuilder
    {
        /// <summary>Materialize the whole snapshot. Throws <see cref="OperationCanceledException"/> if
        /// <paramref name="cancel"/> is signalled, leaving nothing published.</summary>
        public static PopulationDataset Build(PopulationSnapshot snapshot, CancellationToken cancel = default)
        {
            snapshot = snapshot ?? PopulationSnapshot.Empty();
            var clock = Stopwatch.StartNew();

            TileSlot[] tiles = snapshot.tiles;
            var tileStart = new int[tiles.Length + 1];
            long total = 0;
            for (int i = 0; i < tiles.Length; i++)
            {
                tileStart[i] = (int)total;
                if (tiles[i].population > 0) total += tiles[i].population;
            }
            tileStart[tiles.Length] = (int)total;
            if (total > int.MaxValue) throw new InvalidOperationException("Population exceeds what one dataset can hold.");

            var people = new Individual[total];
            int seed = snapshot.worldSeed;
            for (int i = 0; i < tiles.Length; i++)
            {
                if ((i & 255) == 0) cancel.ThrowIfCancellationRequested();
                ref readonly TileSlot slot = ref tiles[i];
                if (slot.population <= 0) continue;
                RegionProfile profile = snapshot.ProfileFor(in slot);
                int start = tileStart[i];
                for (int n = 0; n < slot.population; n++)
                    people[start + n] = IndividualSampler.Sample(seed, slot.tile, n, profile);
            }

            clock.Stop();
            return new PopulationDataset(snapshot, people, tileStart, clock.ElapsedMilliseconds);
        }
    }
}
