using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using RegionsAndSocieties.Demographics;

namespace RegionsAndSocieties.PersistentWorld.Population
{
    /// <summary>
    /// The compute step of snapshot → compute → swap (#3): baseline, overlay, regroup, households, index.
    /// <list type="number">
    /// <item><b>Baseline.</b> Run the deterministic sampler over every birth tile: the birth list.</item>
    /// <item><b>Overlay.</b> Apply the sparse deltas (#9) — new home tiles, deaths — and the world-pawn
    /// links (#4), which put a real pawn's sex, age and keys on its person.</item>
    /// <item><b>Regroup by home.</b> Lay the living out tile by tile by where they live now, and index
    /// them by id, so a person is found by identity and a tile lists its residents.</item>
    /// <item><b>Households and index.</b> Assemble households per home tile (#7) and the query index (#5).</item>
    /// </list>
    /// This is where the O(population) work lives — off the game loop, on a background thread. Reads only
    /// the snapshot; touches no game state.
    /// </summary>
    public static class PopulationBuilder
    {
        /// <summary>Materialize the whole snapshot. Throws <see cref="OperationCanceledException"/> if
        /// <paramref name="cancel"/> is signalled, leaving nothing published.</summary>
        public static PopulationDataset Build(PopulationSnapshot snapshot, CancellationToken cancel = default)
        {
            snapshot = snapshot ?? PopulationSnapshot.Empty();
            var clock = Stopwatch.StartNew();

            // 1. Baseline: everyone at birth, laid out by birth tile.
            TileSlot[] births = snapshot.tiles;
            var birthStart = new int[births.Length + 1];
            long total = 0;
            for (int i = 0; i < births.Length; i++)
            {
                birthStart[i] = (int)total;
                if (births[i].population > 0) total += births[i].population;
            }
            birthStart[births.Length] = (int)total;
            if (total > int.MaxValue) throw new InvalidOperationException("Population exceeds what one dataset can hold.");

            var born = new Individual[total];
            int seed = snapshot.worldSeed;
            for (int i = 0; i < births.Length; i++)
            {
                if ((i & 255) == 0) cancel.ThrowIfCancellationRequested();
                if (births[i].population <= 0) continue;
                RegionProfile profile = snapshot.ProfileFor(births[i].region);
                int start = birthStart[i];
                for (int n = 0; n < births[i].population; n++)
                    born[start + n] = IndividualSampler.Sample(seed, births[i].tile, n, profile);
            }

            // 2. Overlay: deltas (home, dead) and links (real pawns).
            var dead = new bool[total];
            ApplyDeltas(snapshot, birthStart, born, dead);
            int linkedCount = ApplyLinks(snapshot, birthStart, born);
            cancel.ThrowIfCancellationRequested();

            // 3. Regroup the living by home tile.
            RegroupByHome(snapshot, born, dead, out Individual[] people, out TileSlot[] homes, out int[] tileStart);
            cancel.ThrowIfCancellationRequested();

            // 4. Households per home tile, then the index.
            HouseholdTable households = HouseholdTable.Build(seed, homes);
            StampHouseholds(households, homes, tileStart, people);
            PopulationIndex index = PopulationIndex.Build(snapshot, homes, tileStart, people);

            clock.Stop();
            return new PopulationDataset(snapshot, people, homes, tileStart, clock.ElapsedMilliseconds, linkedCount, index, households);
        }

        // A delta names its birth slot, so the baseline row is an offset, not a lookup. A delta whose slot
        // no longer exists (the tile's births shrank) is ignored; the reconcile drops it next snapshot.
        private static void ApplyDeltas(PopulationSnapshot snapshot, int[] birthStart, Individual[] born, bool[] dead)
        {
            PersonDelta[] deltas = snapshot.deltas;
            for (int k = 0; k < deltas.Length; k++)
            {
                ref readonly PersonDelta d = ref deltas[k];
                int pos = BirthPosition(snapshot, birthStart, d.birthTile, d.birthIndex);
                if (pos < 0 || born[pos].id != d.id) continue;
                if (d.Dead) { dead[pos] = true; continue; }
                born[pos].tile = d.homeTile;
            }
        }

        // The union with the world-pawn overlay (#4): a person who is a real pawn reports the pawn's own
        // sex, age, xenotype, faction and ideoligion. Derived education, class and wealth stand (0.1.0 has
        // no pawn-side source for them); work status is re-derived so it never contradicts the real age.
        private static int ApplyLinks(PopulationSnapshot snapshot, int[] birthStart, Individual[] born)
        {
            LinkedPerson[] linked = snapshot.linked;
            int applied = 0;
            for (int k = 0; k < linked.Length; k++)
            {
                ref readonly LinkedPerson link = ref linked[k];
                int pos = BirthPosition(snapshot, birthStart, link.birthTile, link.birthIndex);
                if (pos < 0 || born[pos].id != link.id) continue;

                ref Individual p = ref born[pos];
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

        private static int BirthPosition(PopulationSnapshot snapshot, int[] birthStart, int birthTile, int birthIndex)
        {
            int t = snapshot.TileSlotOf(birthTile);
            if (t < 0 || birthIndex < 0 || birthIndex >= snapshot.tiles[t].population) return -1;
            return birthStart[t] + birthIndex;
        }

        // Counting sort of the living by home tile; within a tile, birth order (so a tile's residents are
        // stable and the id index can be built on top). Home tiles with no births still get a group.
        private static void RegroupByHome(PopulationSnapshot snapshot, Individual[] born, bool[] dead,
            out Individual[] people, out TileSlot[] homes, out int[] tileStart)
        {
            var count = new Dictionary<int, int>();
            int living = 0;
            for (int i = 0; i < born.Length; i++)
            {
                if (dead[i]) continue;
                living++;
                count.TryGetValue(born[i].tile, out int c);
                count[born[i].tile] = c + 1;
            }

            var tiles = new int[count.Count];
            count.Keys.CopyTo(tiles, 0);
            Array.Sort(tiles);

            homes = new TileSlot[tiles.Length];
            tileStart = new int[tiles.Length + 1];
            var slotOf = new Dictionary<int, int>(tiles.Length);
            for (int t = 0; t < tiles.Length; t++)
            {
                homes[t] = new TileSlot { tile = tiles[t], region = snapshot.RegionSlotOfTile(tiles[t]), population = count[tiles[t]] };
                tileStart[t + 1] = tileStart[t] + homes[t].population;
                slotOf[tiles[t]] = t;
            }

            people = new Individual[living];
            var cursor = new int[tiles.Length];
            Array.Copy(tileStart, cursor, tiles.Length);
            for (int i = 0; i < born.Length; i++)
            {
                if (dead[i]) continue;
                int t = slotOf[born[i].tile];
                people[cursor[t]++] = born[i];
            }
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

        /// <summary>Core's age bands applied to a real age.</summary>
        public static AgeBucket BucketOf(int age)
        {
            if (age < AgeStructureRules.ChildMaxAge) return AgeBucket.Child;
            if (age >= AgeStructureRules.ElderMinAge) return AgeBucket.Elder;
            return AgeBucket.WorkingAge;
        }
    }
}
