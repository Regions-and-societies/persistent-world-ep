using System;
using System.Diagnostics;
using System.Threading;
using RegionsAndSocieties.Demographics;

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

            int linkedCount = Overlay(snapshot, tiles, tileStart, people);
            cancel.ThrowIfCancellationRequested();
            HouseholdTable households = HouseholdTable.Build(snapshot);
            StampHouseholds(households, tiles, tileStart, people);
            cancel.ThrowIfCancellationRequested();
            PopulationIndex index = PopulationIndex.Build(snapshot, tileStart, people);

            clock.Stop();
            return new PopulationDataset(snapshot, people, tileStart, clock.ElapsedMilliseconds, linkedCount, index, households);
        }

        /// <summary>Write each person's household index and size onto the person, so filters and the export
        /// need no table lookup. O(people).</summary>
        public static void StampHouseholds(HouseholdTable households, TileSlot[] tiles, int[] tileStart, Individual[] people)
        {
            for (int t = 0; t < tiles.Length; t++)
            {
                int n = households.CountOnTile(t);
                for (int h = 0; h < n; h++)
                {
                    if (!households.TryMembers(t, h, out int first, out int size)) continue;
                    for (int k = first; k < first + size; k++)
                    {
                        ref Individual p = ref people[tileStart[t] + k];
                        p.household = h;
                        p.householdSize = size;
                    }
                }
            }
        }

        // The union with the tracked overlay (#4): a slot a real pawn holds reports the pawn's own sex, age,
        // xenotype, faction and ideoligion. Its derived education, class and wealth stand (0.1.0 has no
        // pawn-side source for them), but work status is re-derived so it never contradicts the real age.
        // Links to slots that no longer exist are skipped; the reconcile step drops them next snapshot.
        private static int Overlay(PopulationSnapshot snapshot, TileSlot[] tiles, int[] tileStart, Individual[] people)
        {
            LinkedPerson[] linked = snapshot.linked;
            if (linked.Length == 0) return 0;

            int applied = 0, ti = 0;
            for (int k = 0; k < linked.Length; k++)
            {
                ref readonly LinkedPerson link = ref linked[k];
                // Both are sorted by tile, so a single forward scan finds each link's tile.
                while (ti < tiles.Length && tiles[ti].tile < link.tile) ti++;
                if (ti >= tiles.Length || tiles[ti].tile != link.tile) continue;
                if (link.index < 0 || link.index >= tiles[ti].population) continue;

                ref Individual p = ref people[tileStart[ti] + link.index];
                p.pawnId = link.pawnId;
                p.female = link.female;
                p.age = link.age < 0 ? 0 : link.age;
                p.ageBucket = BucketOf(p.age);
                p.raceKey = link.raceKey;
                p.factionKey = link.factionKey;
                p.ideoKey = link.ideoKey;
                switch (p.ageBucket)
                {
                    case AgeBucket.Child: p.work = WorkStatus.Dependent; break;
                    case AgeBucket.Elder: p.work = WorkStatus.Retired; break;
                    default: if (p.work == WorkStatus.Dependent || p.work == WorkStatus.Retired) p.work = WorkStatus.Unemployed; break;
                }
                applied++;
            }
            return applied;
        }

        /// <summary>Core's age bands applied to a real age.</summary>
        public static AgeBucket BucketOf(int age)
        {
            if (age < AgeStructureRules.ChildMaxAge) return AgeBucket.Child;
            if (age >= AgeStructureRules.ElderMinAge) return AgeBucket.Elder;
            return AgeBucket.WorkingAge;
        }
    }
}
