// Behaviour tests for the async materialization loop (#3): a snapshot builds into exactly the people it
// declares, laid out tile by tile and identical to sampling them one at a time; the loop refuses a second
// build while one runs, publishes only completed builds, keeps the previous dataset through a cancelled
// or failed build, and swaps atomically. Pure, so it runs without a game.
using System;
using System.Collections.Generic;
using System.Threading;
using RegionsAndSocieties.PersistentWorld.Population;

namespace MaterializationTests
{
    public static class Program
    {
        private static int failures;
        private const int Seed = 987654321;

        public static int Main()
        {
            Section("a build yields exactly the people the snapshot declares, tile by tile");
            PopulationSnapshot snap = Planet(out RegionProfile[] profiles);
            PopulationDataset ds = PopulationBuilder.Build(snap);
            Check("count is the snapshot total", ds.Count == snap.TotalPopulation && ds.Count == 12 + 250 + 3 + 40);
            Check("birth tiles are in ascending order", snap.tiles[0].tile < snap.tiles[1].tile && snap.tiles[1].tile < snap.tiles[2].tile);
            Check("home tiles are the populated birth tiles, ascending", ds.TileCount == 4 && ds.tiles[0].tile == 12 && ds.tiles[3].tile == 4000);
            Check("tileStart offsets are cumulative", ds.tileStart[0] == 0 && ds.tileStart[ds.TileCount] == ds.Count && Monotonic(ds.tileStart));
            Check("a zero-population tile takes no room", ds.TryTileRange(9000, out _, out int zc) == false && zc == 0);
            Check("tile range lookup", ds.TryTileRange(400, out int s400, out int c400) && c400 == 250 && ds.people[s400].tile == 400 && ds.people[s400 + 249].birthIndex == 249);
            Check("unknown tile has no range", !ds.TryTileRange(1, out _, out _));
            Check("TryGetBorn", ds.TryGetBorn(400, 17, out Individual p17) && p17.tile == 400 && p17.birthIndex == 17 && p17.id == PersonId.Make(Seed, 400, 17));
            Check("TryGetBorn out of range is false", !ds.TryGetBorn(400, 250, out _) && !ds.TryGetBorn(400, -1, out _));
            Check("TryGetById and PositionOf agree", ds.TryGetById(p17.id, out Individual q17) && q17.birthIndex == 17 && ds.people[ds.PositionOf(p17.id)].id == p17.id && ds.PositionOf(123) == -1);
            Check("region of a person is the Core province id", ds.RegionOf(in p17) == 77 && ds.TryGetResident(4000, 0, out Individual q) && ds.RegionOf(in q) == -1);
            Check("build serial matches the snapshot", ds.buildSerial == snap.serial);

            Section("the build is the sampler, person for person");
            bool same = true;
            for (int i = 0; i < ds.TileCount; i++)
            {
                TileSlot slot = ds.tiles[i];
                RegionProfile prof = snap.ProfileFor(snap.RegionSlotOfTile(slot.tile));
                for (int n = 0; n < slot.population; n++)
                {
                    Individual a = ds.people[ds.tileStart[i] + n];
                    Individual b = IndividualSampler.Sample(Seed, slot.tile, n, prof);
                    same &= a.id == b.id && a.tile == b.tile && a.birthIndex == b.birthIndex && a.age == b.age && a.wealth == b.wealth && a.female == b.female && a.raceKey == b.raceKey;
                }
            }
            Check("every person equals a direct sample of its birth slot", same);
            Check("a tile outside any region uses the empty profile", ds.TryGetBorn(4000, 1, out Individual w) && w.raceKey == -1 && w.factionKey == -1);
            Check("two builds of the same snapshot are identical", SamePeople(ds, PopulationBuilder.Build(snap)));
            Check("a different seed is a different planet", !SamePeople(ds, PopulationBuilder.Build(Planet(out _, Seed + 1))));

            Section("edge cases");
            Check("empty snapshot builds an empty dataset", PopulationBuilder.Build(PopulationSnapshot.Empty()).Count == 0);
            Check("null snapshot builds an empty dataset", PopulationBuilder.Build(null).Count == 0);
            Check("the Empty dataset is usable", PopulationDataset.Empty.Count == 0 && !PopulationDataset.Empty.TryTileRange(0, out _, out _) && !PopulationDataset.Empty.TryGetById(1, out _));
            var cts = new CancellationTokenSource(); cts.Cancel();
            bool cancelled = false;
            try { PopulationBuilder.Build(Planet(out _), cts.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check("a cancelled build throws rather than returning a partial dataset", cancelled);

            Section("the loop: refuse concurrent starts, publish only completed builds, swap atomically");
            var loop = new MaterializationLoop();
            Check("starts empty, never null", loop.Current != null && loop.Current.Count == 0 && loop.Swaps == 0);
            Check("poll with nothing running is a no-op", !loop.Poll());
            PopulationSnapshot big = BigPlanet(200000);
            Check("start accepted", loop.TryStart(big));
            Check("second start refused while building", !loop.TryStart(big));
            Check("current unchanged until poll", loop.Current.Count == 0);
            loop.Wait();
            Check("still unchanged after completion, before poll", loop.Current.Count == 0 && !loop.IsBuilding);
            int swapped = 0; loop.Swapped += d => swapped++;
            Check("poll publishes", loop.Poll() && loop.Current.Count == 200000 && loop.Swaps == 1 && swapped == 1);
            Check("poll again is a no-op", !loop.Poll() && loop.Swaps == 1);
            PopulationDataset first = loop.Current;

            Check("a cancelled build keeps the previous dataset", loop.TryStart(BigPlanet(2000000)) && Cancel(loop) && ReferenceEquals(loop.Current, first) && loop.Swaps == 1);
            Check("start accepted again after cancel", loop.TryStart(Planet(out _)));
            loop.Wait();
            Check("the next build replaces it", loop.Poll() && !ReferenceEquals(loop.Current, first) && loop.Current.Count == 305 && loop.Swaps == 2);
            Check("BuildNow is start+wait+poll", loop.BuildNow(BigPlanet(1000)) && loop.Current.Count == 1000 && loop.Swaps == 3);
            Check("BuildNow refuses a null snapshot", !loop.BuildNow(null) && loop.Swaps == 3);

            Console.WriteLine(failures == 0 ? "materialization: all checks passed" : $"materialization: {failures} FAILED");
            return failures == 0 ? 0 : 1;
        }

        // Four tiles: a hamlet (12) and a city (250) in province 77, three people in province 78, forty on a
        // tile in no region, and one populated-zero tile that must take no room.
        private static PopulationSnapshot Planet(out RegionProfile[] profiles, int seed = Seed)
        {
            profiles = new[]
            {
                new RegionProfile { femaleFraction = 0.5f, ageShares = new[] { 0.3f, 0.6f, 0.1f }, raceKeys = new[] { 0, 1 }, raceWeights = new[] { 0.8f, 0.2f }, factionKeys = new[] { 3 }, factionWeights = new[] { 1f } },
                new RegionProfile { femaleFraction = 0.4f, ageShares = new[] { 0.1f, 0.8f, 0.1f } },
            };
            var rows = new List<TileSlot>
            {
                new TileSlot { tile = 400, region = 0, population = 250 },
                new TileSlot { tile = 9000, region = 0, population = 0 },
                new TileSlot { tile = 4000, region = -1, population = 40 },
                new TileSlot { tile = 12, region = 0, population = 12 },
                new TileSlot { tile = 800, region = 1, population = 3 },
            };
            return PopulationSnapshot.From(seed, rows, new[] { 77, 78 }, profiles, new[] { "Baseliner", "Hussar" }, new[] { "A", "B", "C", "Empire" });
        }

        private static PopulationSnapshot BigPlanet(int people)
        {
            var rows = new List<TileSlot>();
            int perTile = 500, tile = 0;
            for (int left = people; left > 0; left -= perTile, tile++)
                rows.Add(new TileSlot { tile = tile, region = 0, population = Math.Min(perTile, left) });
            return PopulationSnapshot.From(Seed, rows, new[] { 1 }, new[] { new RegionProfile { ageShares = new[] { 0.2f, 0.6f, 0.2f } } });
        }

        private static bool Cancel(MaterializationLoop loop)
        {
            loop.Cancel();
            loop.Wait();
            bool published = loop.Poll();
            return !published;
        }

        private static bool Monotonic(int[] a) { for (int i = 1; i < a.Length; i++) if (a[i] < a[i - 1]) return false; return true; }

        private static bool SamePeople(PopulationDataset a, PopulationDataset b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                Individual x = a.people[i], y = b.people[i];
                if (x.id != y.id || x.tile != y.tile || x.birthIndex != y.birthIndex || x.age != y.age || x.wealth != y.wealth || x.female != y.female) return false;
            }
            return true;
        }

        private static void Section(string name) => Console.WriteLine("-- " + name);

        private static void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + name);
            if (!ok) failures++;
        }
    }
}
