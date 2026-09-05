// Behaviour tests for the query index (#5): marginals sum to the population, region runs hold exactly the
// region's people, every filtered count and breakdown agrees with a brute-force scan, the fast paths
// (unfiltered, region-scoped, tile-scoped) give the same answers as the slow one, and an empty dataset
// answers zero everywhere. Pure, so it runs without a game.
using System;
using System.Collections.Generic;
using RegionsAndSocieties.Demographics;
using RegionsAndSocieties.PersistentWorld.Population;

namespace QueryIndexTests
{
    public static class Program
    {
        private static int failures;
        private const int Seed = 31337;

        public static int Main()
        {
            PopulationDataset ds = Planet();
            PopulationIndex ix = ds.index;
            int N = ds.Count;

            Section("marginals");
            Check("people count is the snapshot total", N == 300 + 120 + 80 + 25);
            Check("every marginal sums to the population", Sum(ix.bySex) == N && Sum(ix.byAgeBucket) == N && Sum(ix.byEducation) == N && Sum(ix.byClass) == N && Sum(ix.byWork) == N && Sum(ix.byRegion) == N && Sum(ix.byFaction) == N && Sum(ix.byXenotype) == N && Sum(ix.byIdeo) == N && Sum(ix.byLinked) == N);
            Check("sector marginal sums to the employed", Sum(ix.bySector) == ix.byWork[(int)WorkStatus.Employed]);
            Check("region marginal: slot 0 is no-region", ix.byRegion[0] == 25 && ix.byRegion[1] == 420 && ix.byRegion[2] == 80);
            Check("linked marginal counts the overlay", ix.byLinked[1] == 2 && ds.LinkedCount == 2);
            Check("marginal sizes follow the catalogue", ix.byFaction.Length == 3 && ix.byXenotype.Length == 3 && ix.byIdeo.Length == 2);

            Section("region runs");
            ix.RegionRun(0, out int s0, out int e0); ix.RegionRun(1, out int s1, out int e1); ix.RegionRun(-1, out int sn, out int en);
            Check("run lengths match the marginals", e0 - s0 == 420 && e1 - s1 == 80 && en - sn == 25);
            Check("runs tile the whole order", s0 == 0 && e0 == s1 && e1 == sn && en == N);
            bool runsRight = true;
            for (int k = s0; k < e0; k++) runsRight &= ds.RegionSlotOf(in ds.people[ix.order[k]]) == 0;
            for (int k = s1; k < e1; k++) runsRight &= ds.RegionSlotOf(in ds.people[ix.order[k]]) == 1;
            for (int k = sn; k < en; k++) runsRight &= ds.RegionSlotOf(in ds.people[ix.order[k]]) == -1;
            Check("each run holds exactly its region's people", runsRight);
            Check("order is a permutation", IsPermutation(ix.order, N));
            Check("region slot lookups", ds.RegionSlotOfId(77) == 0 && ds.RegionSlotOfId(78) == 1 && ds.RegionSlotOfId(-1) == -1 && ds.RegionSlotOfId(999) == int.MinValue && ds.RegionOfTile(400) == 77 && ds.RegionOfTile(4000) == -1);

            Section("counts agree with brute force");
            var filters = new List<(string, PopulationFilter)>
            {
                ("all", new PopulationFilter()),
                ("region 77", new PopulationFilter { regionId = 77 }),
                ("region 78 women", new PopulationFilter { regionId = 78, sex = 1 }),
                ("no region", new PopulationFilter { regionId = -1 }),
                ("unknown region", new PopulationFilter { regionId = 999 }),
                ("over 40", new PopulationFilter { minAge = 41 }),
                ("children", new PopulationFilter { ageBucket = (int)AgeBucket.Child }),
                ("undergrad+", new PopulationFilter { minEducation = (int)EducationTier.Undergrad }),
                ("postgrad women over 40 in 77", new PopulationFilter { regionId = 77, education = (int)EducationTier.Postgrad, sex = 1, minAge = 41 }),
                ("affluent industry", new PopulationFilter { ses = (int)SesTier.Affluent, sector = (int)OccupationSector.Industry }),
                ("sector implies employed", new PopulationFilter { sector = (int)OccupationSector.Trade, work = (int)WorkStatus.Unemployed }),
                ("faction 1 hussars", new PopulationFilter { factionKey = 1, raceKey = 1 }),
                ("plain humans", new PopulationFilter { raceKey = -1 }),
                ("linked", new PopulationFilter { linked = 1 }),
                ("tile 400 elders", new PopulationFilter { tile = 400, ageBucket = (int)AgeBucket.Elder }),
                ("tile 400 wrong region", new PopulationFilter { tile = 400, regionId = 78 }),
                ("unknown tile", new PopulationFilter { tile = 5 }),
            };
            bool countsOk = true, nonTrivial = true, breakdownsOk = true, selectOk = true;
            foreach (var (name, f) in filters)
            {
                int fast = PopulationQuery.Count(ds, f);
                int slow = Brute(ds, f);
                if (fast != slow) { countsOk = false; Console.WriteLine($"     mismatch {name}: {fast} vs {slow}"); }
                foreach (Dimension d in Enum.GetValues(typeof(Dimension)))
                {
                    int[] b = PopulationQuery.Breakdown(ds, f, d);
                    int expected = d == Dimension.Sector ? Brute(ds, f, WorkStatus.Employed) : slow;
                    if (Sum(b) != expected) { breakdownsOk = false; Console.WriteLine($"     breakdown {name}/{d}: {Sum(b)} vs {expected}"); }
                }
                List<int> sel = PopulationQuery.Select(ds, f);
                if (sel.Count != slow) selectOk = false;
                foreach (int i in sel) if (i < 0 || i >= N || !f.Matches(in ds.people[i]) || (f.regionId != int.MinValue && ds.RegionOf(in ds.people[i]) != f.regionId)) selectOk = false;
            }
            Check("every filter counts like the brute-force scan", countsOk);
            Check("every breakdown sums to its filter's count", breakdownsOk);
            Check("select returns exactly the matching people", selectOk);
            Check("the interesting filters are non-empty", PopulationQuery.Count(ds, filters[8].Item2) > 0 && PopulationQuery.Count(ds, filters[9].Item2) > 0 && PopulationQuery.Count(ds, filters[13].Item2) == 2);
            Check("unknown region / tile / contradictory sector count zero", PopulationQuery.Count(ds, filters[4].Item2) == 0 && PopulationQuery.Count(ds, filters[16].Item2) == 0 && PopulationQuery.Count(ds, filters[10].Item2) == 0 && PopulationQuery.Count(ds, filters[15].Item2) == 0);
            Check("select honours the limit", PopulationQuery.Select(ds, new PopulationFilter(), 7).Count == 7);
            Check("unfiltered breakdown is a copy of the marginal", !ReferenceEquals(PopulationQuery.Breakdown(ds, null, Dimension.Sex), ix.bySex) && Sum(PopulationQuery.Breakdown(ds, null, Dimension.Sex)) == N);
            Check("null filter means all", PopulationQuery.Count(ds, null) == N);

            Section("empty dataset");
            PopulationDataset empty = PopulationDataset.Empty;
            Check("zero everywhere", PopulationQuery.Count(empty, null) == 0 && PopulationQuery.Count(empty, new PopulationFilter { regionId = 1 }) == 0 && Sum(PopulationQuery.Breakdown(empty, null, Dimension.Region)) == 0 && PopulationQuery.Select(empty, null).Count == 0);
            Check("null dataset is safe", PopulationQuery.Count(null, null) == 0 && PopulationQuery.Breakdown(null, null, Dimension.Sex).Length == 0);

            Console.WriteLine(failures == 0 ? "queryindex: all checks passed" : $"queryindex: {failures} FAILED");
            return failures == 0 ? 0 : 1;
        }

        // Two regions plus a no-region tile: a mixed city and hamlet in 77, a tribal village in 78.
        private static PopulationDataset Planet()
        {
            var profiles = new[]
            {
                new RegionProfile { femaleFraction = 0.5f, ageShares = new[] { 0.2f, 0.65f, 0.15f }, educationShares = new[] { 0.05f, 0.15f, 0.4f, 0.3f, 0.1f }, sesShares = new[] { 0.1f, 0.3f, 0.45f, 0.15f }, occupationShares = new[] { 0.1f, 0.45f, 0.15f, 0.3f }, employmentRate = 74, raceKeys = new[] { 0, 1 }, raceWeights = new[] { 0.7f, 0.3f }, factionKeys = new[] { 0, 1 }, factionWeights = new[] { 0.6f, 0.4f }, ideoKeys = new[] { 0 }, ideoWeights = new[] { 1f } },
                new RegionProfile { femaleFraction = 0.52f, ageShares = new[] { 0.4f, 0.5f, 0.1f }, educationShares = new[] { 0.7f, 0.25f, 0.05f, 0f, 0f }, sesShares = new[] { 0.8f, 0.2f, 0f, 0f }, occupationShares = new[] { 0.8f, 0.1f, 0.1f, 0f }, employmentRate = 55, factionKeys = new[] { 1 }, factionWeights = new[] { 1f } },
            };
            var rows = new List<TileSlot>
            {
                new TileSlot { tile = 400, region = 0, population = 300 },
                new TileSlot { tile = 12, region = 0, population = 120 },
                new TileSlot { tile = 800, region = 1, population = 80 },
                new TileSlot { tile = 4000, region = -1, population = 25 },
            };
            var linked = new[]
            {
                new LinkedPerson { pawnId = 900, tile = 400, index = 3, female = true, age = 44, raceKey = 1, factionKey = 0, ideoKey = 0 },
                new LinkedPerson { pawnId = 901, tile = 800, index = 0, female = false, age = 30, raceKey = -1, factionKey = 1, ideoKey = -1 },
            };
            var snap = PopulationSnapshot.From(Seed, rows, new[] { 77, 78 }, profiles, new[] { "Baseliner", "Hussar" }, new[] { "Empire", "Tribe" }, new[] { "Creed" }, linked: linked);
            return PopulationBuilder.Build(snap);
        }

        private static int Brute(PopulationDataset ds, PopulationFilter f, WorkStatus? mustBe = null)
        {
            int n = 0;
            for (int i = 0; i < ds.Count; i++)
            {
                ref readonly Individual p = ref ds.people[i];
                if (f.regionId != int.MinValue && ds.RegionOf(in p) != f.regionId) continue;
                if (!f.Matches(in p)) continue;
                if (mustBe.HasValue && p.work != mustBe.Value) continue;
                n++;
            }
            return n;
        }

        private static int Sum(int[] a) { int s = 0; foreach (int x in a) s += x; return s; }

        private static bool IsPermutation(int[] order, int n)
        {
            if (order.Length != n) return false;
            var seen = new bool[n];
            foreach (int i in order) { if (i < 0 || i >= n || seen[i]) return false; seen[i] = true; }
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
