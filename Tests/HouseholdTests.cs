// Behaviour tests for household assembly (#7): a tile's sizes sum to its population, stay inside 1..7,
// number exactly Core's residence count, are deterministic per (seed, tile) and differ across tiles;
// rural tiles make big households and cities small ones, matching Core's occupancy; the table gives every
// person exactly one household as a contiguous run; heads are the oldest adult; the index and filters see
// household size; and the export round-trips it. Pure, so it runs without a game.
using System;
using System.Collections.Generic;
using System.IO;
using RegionsAndSocieties.Demographics;
using RegionsAndSocieties.PersistentWorld.Population;

namespace HouseholdTests
{
    public static class Program
    {
        private static int failures;
        private const int Seed = 777;

        public static int Main()
        {
            Section("sizes");
            bool sums = true, band = true, counts = true;
            foreach (int pop in new[] { 1, 2, 3, 7, 8, 14, 15, 40, 100, 250, 400, 1000 })
            {
                int[] s = HouseholdRules.Sizes(Seed, 100, pop);
                int total = 0; foreach (int x in s) { total += x; band &= x >= 1 && x <= HouseholdRules.MaxOccupancy; }
                sums &= total == pop;
                int floor = (pop + HouseholdRules.MaxOccupancy - 1) / HouseholdRules.MaxOccupancy;
                counts &= s.Length == Math.Max(ResidenceRules.For(pop).residences, floor) && s.Length == HouseholdRules.Count(pop);
            }
            Check("sizes sum to the population", sums);
            Check("every household is 1..7 people", band);
            Check("household count is Core's residence count, floored so nobody exceeds 7", counts);
            Check("8 people are two homes, not one of eight", HouseholdRules.Count(8) == 2 && HouseholdRules.Count(7) == 1 && HouseholdRules.Count(14) == ResidenceRules.For(14).residences);
            Check("no people, no households", HouseholdRules.Sizes(Seed, 1, 0).Length == 0 && HouseholdRules.Count(0) == 0);
            Check("one person is one household", HouseholdRules.Sizes(Seed, 1, 1).Length == 1 && HouseholdRules.Sizes(Seed, 1, 1)[0] == 1);
            Check("deterministic per (seed, tile, population)", Same(HouseholdRules.Sizes(Seed, 100, 250), HouseholdRules.Sizes(Seed, 100, 250)));
            Check("varies across tiles and seeds", !Same(HouseholdRules.Sizes(Seed, 100, 250), HouseholdRules.Sizes(Seed, 101, 250)) && !Same(HouseholdRules.Sizes(Seed, 100, 250), HouseholdRules.Sizes(Seed + 1, 100, 250)));
            Check("a tile's households are not all the same size", Distinct(HouseholdRules.Sizes(Seed, 100, 250)) >= 3);
            Check("a hamlet lives in big households", Mean(HouseholdRules.Sizes(Seed, 5, 14)) >= 5f);
            Check("a city lives in small households", Mean(HouseholdRules.Sizes(Seed, 6, 1000)) <= 2.2f);
            Check("mean size tracks Core's occupancy", Math.Abs(Mean(HouseholdRules.Sizes(Seed, 7, 120)) - ResidenceRules.For(120).occupancy) < 0.6f);

            Section("the table");
            PopulationDataset ds = Planet();
            HouseholdTable ht = ds.households;
            Check("household count is the sum over tiles", ht.Count == HouseholdRules.Count(250) + HouseholdRules.Count(14) + HouseholdRules.Count(3) + HouseholdRules.Count(40) && ds.HouseholdCount == ht.Count);
            Check("per-tile counts", ds.HouseholdsOnTile(400) == HouseholdRules.Count(250) && ds.HouseholdsOnTile(12) == HouseholdRules.Count(14) && ds.HouseholdsOnTile(800) == 1 && ds.HouseholdsOnTile(9000) == 0 && ds.HouseholdsOnTile(1) == 0);
            bool covered = true, contiguous = true, stamped = true;
            foreach (int tile in new[] { 400, 12, 800, 4000 })
            {
                ds.TryTileRange(tile, out int start, out int count);
                var seen = new int[count];
                int expect = 0;
                for (int h = 0; h < ds.HouseholdsOnTile(tile); h++)
                {
                    if (!ds.TryHousehold(tile, h, out int hs, out int size)) { covered = false; break; }
                    contiguous &= hs - start == expect;
                    expect += size;
                    for (int i = hs; i < hs + size; i++)
                    {
                        seen[i - start]++;
                        stamped &= ds.people[i].household == h && ds.people[i].householdSize == size;
                        stamped &= ds.HouseholdOf(tile, i - start) == h;
                    }
                }
                foreach (int n in seen) covered &= n == 1;
                contiguous &= expect == count;
            }
            Check("every person is in exactly one household", covered);
            Check("households are consecutive runs that tile the population", contiguous);
            Check("each person is stamped with its household and size, and the lookup agrees", stamped);
            Check("no such household", !ds.TryHousehold(400, 9999, out _, out _) && !ds.TryHousehold(1, 0, out _, out _) && ds.HouseholdOf(400, -1) == -1 && ds.HouseholdOf(400, 250) == -1);

            Section("heads");
            bool heads = true;
            for (int h = 0; h < ds.HouseholdsOnTile(400); h++)
            {
                if (!ds.TryHouseholdHead(400, h, out Individual head)) { heads = false; break; }
                ds.TryHousehold(400, h, out int hs, out int size);
                bool anyAdult = false; int oldestAdult = -1, oldest = -1;
                for (int i = hs; i < hs + size; i++)
                {
                    Individual p = ds.people[i];
                    if (p.age > oldest) oldest = p.age;
                    if (p.ageBucket != AgeBucket.Child) { anyAdult = true; if (p.age > oldestAdult) oldestAdult = p.age; }
                }
                heads &= head.household == h && (anyAdult ? head.ageBucket != AgeBucket.Child && head.age == oldestAdult : head.age == oldest);
            }
            Check("the head is the oldest adult, else the oldest member", heads);
            Check("no head for a missing household", !ds.TryHouseholdHead(400, 9999, out _));

            Section("index and filters");
            int[] bySize = ds.index.byHouseholdSize;
            Check("household-size marginal sums to the population and has no 'none'", Sum(bySize) == ds.Count && bySize[0] == 0 && bySize.Length == HouseholdRules.MaxOccupancy + 1);
            int peopleInBigHomes = 0; foreach (Individual p in ds.people) if (p.householdSize >= 5) peopleInBigHomes++;
            Check("filter by household size agrees with brute force", PopulationQuery.Count(ds, new PopulationFilter { minHouseholdSize = 5 }) == peopleInBigHomes && peopleInBigHomes > 0);
            ds.TryHousehold(12, 0, out _, out int size0);
            Check("filter by one household", PopulationQuery.Count(ds, new PopulationFilter { tile = 12, household = 0 }) == size0 && size0 > 0);
            Check("breakdown by household size", Sum(PopulationQuery.Breakdown(ds, new PopulationFilter { regionId = 77 }, Dimension.HouseholdSize)) == PopulationQuery.Count(ds, new PopulationFilter { regionId = 77 }));

            Section("export");
            var w = new StringWriter(); PopulationExport.WriteJson(ds, w);
            PopulationDataset back = PopulationExport.ReadJson(new StringReader(w.ToString()));
            Check("household fields round-trip", back != null && back.HouseholdCount == ds.HouseholdCount && back.people[100].household == ds.people[100].household && back.people[100].householdSize == ds.people[100].householdSize);
            Check("a file whose households disagree with the table is refused", PopulationExport.ReadJson(new StringReader(Tamper(w.ToString(), ds))) == null);
            Check("empty dataset has an empty table", PopulationDataset.Empty.HouseholdCount == 0 && !PopulationDataset.Empty.TryHousehold(0, 0, out _, out _));

            Console.WriteLine(failures == 0 ? "households: all checks passed" : $"households: {failures} FAILED");
            return failures == 0 ? 0 : 1;
        }

        private static PopulationDataset Planet()
        {
            var profiles = new[] { new RegionProfile { femaleFraction = 0.5f, ageShares = new[] { 0.3f, 0.55f, 0.15f } } };
            var rows = new List<TileSlot>
            {
                new TileSlot { tile = 400, region = 0, population = 250 },
                new TileSlot { tile = 12, region = 0, population = 14 },
                new TileSlot { tile = 800, region = 0, population = 3 },
                new TileSlot { tile = 4000, region = -1, population = 40 },
                new TileSlot { tile = 9000, region = 0, population = 0 },
            };
            return PopulationBuilder.Build(PopulationSnapshot.From(Seed, rows, new[] { 77 }, profiles));
        }

        // Flip the household column of person #0 of tile 12 to a different household.
        private static string Tamper(string json, PopulationDataset ds)
        {
            ds.TryGetBorn(12, 0, out Individual p);
            string row = "[\"" + PersonId.ToHex(p.id) + "\",12,0,12," + (p.female ? 1 : 0) + "," + p.age + "," + (int)p.ageBucket + "," + (int)p.education + "," + (int)p.ses + "," + p.wealth + "," + (int)p.work + "," + (int)p.sector + "," + p.raceKey + "," + p.factionKey + "," + p.ideoKey + "," + p.pawnId + "," + p.household + "," + p.householdSize + "]";
            string bad = row.Substring(0, row.LastIndexOf(',', row.LastIndexOf(',') - 1)) + "," + (p.household + 1) + "," + p.householdSize + "]";
            return json.Contains(row) ? json.Replace(row, bad) : "tampered row not found";
        }

        private static bool Same(int[] a, int[] b) { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
        private static int Distinct(int[] a) { var s = new HashSet<int>(a); return s.Count; }
        private static float Mean(int[] a) { if (a.Length == 0) return 0; float t = 0; foreach (int x in a) t += x; return t / a.Length; }
        private static int Sum(int[] a) { int s = 0; foreach (int x in a) s += x; return s; }

        private static void Section(string name) => Console.WriteLine("-- " + name);

        private static void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + name);
            if (!ok) failures++;
        }
    }
}
