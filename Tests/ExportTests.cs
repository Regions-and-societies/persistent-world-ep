// Behaviour tests for the export sidecar (#6): a dataset written as JSON reads back person for person with
// its tiles, regions, labels and linked count; the CSV has a header and one row per person in column
// order; a file the reader cannot trust (wrong schema, torn rows, mismatched tile table, garbage) yields
// null rather than an exception; file names are filesystem-safe; and a restore is adopted only before
// the first build. Pure, so it runs without a game.
using System;
using System.Collections.Generic;
using System.IO;
using RegionsAndSocieties.PersistentWorld.Population;

namespace ExportTests
{
    public static class Program
    {
        private static int failures;
        private const int Seed = 2024;

        public static int Main()
        {
            PopulationDataset ds = Planet();

            Section("JSON round trip");
            string json = Json(ds);
            PopulationDataset back = PopulationExport.ReadJson(new StringReader(json));
            Check("reads back", back != null);
            Check("same count, tiles, regions, linked", back.Count == ds.Count && back.TileCount == ds.TileCount && back.RegionIds.Length == 2 && back.RegionIds[1] == 78 && back.LinkedCount == 2);
            Check("same labels", back.snapshot.raceLabels[1] == "Hussar" && back.snapshot.factionLabels[0] == "Empire \"the\" Great" && back.snapshot.ideoLabels[0] == "Creed\nof\tTabs");
            Check("same seed and density version", back.snapshot.worldSeed == Seed && back.snapshot.densityVersion == 9);
            Check("every person identical", SamePeople(ds, back));
            Check("region of a person survives", back.TryGet(800, 2, out Individual q) && back.RegionOf(in q) == 78 && back.TryGet(4000, 0, out Individual n) && back.RegionOf(in n) == -1);
            Check("index is rebuilt on read", back.index.byLinked[1] == 2 && Sum(back.index.byRegion) == back.Count && PopulationQuery.Count(back, new PopulationFilter { regionId = 77 }) == PopulationQuery.Count(ds, new PopulationFilter { regionId = 77 }));
            Check("re-export of the restored dataset is byte-identical", Json(back).Replace("\"buildSerial\":" + back.buildSerial, "") == json.Replace("\"buildSerial\":" + ds.buildSerial, ""));
            Check("empty dataset round-trips", PopulationExport.ReadJson(new StringReader(Json(PopulationDataset.Empty)))?.Count == 0);

            Section("CSV");
            var sw = new StringWriter();
            PopulationExport.WriteCsv(ds, sw);
            string[] lines = sw.ToString().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Check("header plus one row per person", lines.Length == ds.Count + 1 && lines[0].StartsWith("tile,index,female,age") && lines[0].EndsWith(",region"));
            string[] first = lines[1].Split(',');
            Check("row is in column order with the region appended", first.Length == PopulationExport.Columns.Length + 1 && first[0] == ds.people[0].tile.ToString() && first[1] == "0" && first[first.Length - 1] == ds.RegionOf(in ds.people[0]).ToString());
            ds.TryGet(400, 3, out Individual linked);
            Check("a linked row carries its pawn id", lines[1 + IndexOf(ds, 400, 3)].Split(',')[13] == "900" && linked.pawnId == 900);

            Section("untrusted input yields null, never throws");
            Check("garbage", PopulationExport.ReadJson(new StringReader("not json")) == null);
            Check("empty text", PopulationExport.ReadJson(new StringReader("")) == null);
            Check("wrong schema", PopulationExport.ReadJson(new StringReader(json.Replace("\"schema\":1", "\"schema\":99"))) == null);
            Check("truncated file", PopulationExport.ReadJson(new StringReader(json.Substring(0, json.Length / 2))) == null);
            Check("row count disagrees with the tile table", PopulationExport.ReadJson(new StringReader(json.Replace("[400,0,50]", "[400,0,49]"))) == null);
            Check("a row in the wrong place", PopulationExport.ReadJson(new StringReader(json.Replace("[12,0,", "[13,0,"))) == null);
            Check("not an object", PopulationExport.ReadJson(new StringReader("[1,2,3]")) == null);

            Section("MiniJson");
            var o = (Dictionary<string, object>)MiniJson.Parse(" {\"a\": [1, -2.5, 3e2], \"b\": {\"c\": \"x\\\"y\\u0041\"}, \"t\": true, \"n\": null} ");
            var a = (List<object>)o["a"];
            Check("numbers", (double)a[0] == 1 && (double)a[1] == -2.5 && (double)a[2] == 300);
            Check("nested string with escapes", (string)((Dictionary<string, object>)o["b"])["c"] == "x\"yA");
            Check("literals", (bool)o["t"] && o["n"] == null);
            Check("trailing garbage throws", Throws(() => MiniJson.Parse("{} x")));

            Section("file naming");
            Check("hex id", PopulationExport.FileName("1a2b3c4d", "json") == "population_1a2b3c4d.json");
            Check("unsafe characters are replaced", PopulationExport.FileName("a/b\\c:d e", ".csv") == "population_a_b_c_d_e.csv");
            Check("empty id falls back", PopulationExport.FileName("", "json") == "population_world.json" && PopulationExport.FileName(null, "json") == "population_world.json");

            Section("restore is adopted only before the first build");
            var loop = new MaterializationLoop();
            int swapped = 0; loop.Swapped += _ => swapped++;
            Check("restore into an empty loop is adopted", loop.Restore(back) && ReferenceEquals(loop.Current, back) && loop.Swaps == 0 && swapped == 1);
            Check("a fresh build then replaces it", loop.BuildNow(ds.snapshot) && !ReferenceEquals(loop.Current, back) && loop.Swaps == 1);
            Check("restore after a build is refused", !loop.Restore(back) && !ReferenceEquals(loop.Current, back));
            Check("null restore is refused", !new MaterializationLoop().Restore(null));

            Console.WriteLine(failures == 0 ? "export: all checks passed" : $"export: {failures} FAILED");
            return failures == 0 ? 0 : 1;
        }

        private static PopulationDataset Planet()
        {
            var profiles = new[]
            {
                new RegionProfile { femaleFraction = 0.5f, ageShares = new[] { 0.2f, 0.65f, 0.15f }, educationShares = new[] { 0.1f, 0.2f, 0.4f, 0.2f, 0.1f }, sesShares = new[] { 0.2f, 0.3f, 0.35f, 0.15f }, occupationShares = new[] { 0.2f, 0.4f, 0.1f, 0.3f }, employmentRate = 70, raceKeys = new[] { 0, 1 }, raceWeights = new[] { 0.7f, 0.3f }, factionKeys = new[] { 0 }, factionWeights = new[] { 1f }, ideoKeys = new[] { 0 }, ideoWeights = new[] { 1f } },
                new RegionProfile { femaleFraction = 0.5f, ageShares = new[] { 0.4f, 0.5f, 0.1f }, factionKeys = new[] { 1 }, factionWeights = new[] { 1f } },
            };
            var rows = new List<TileSlot>
            {
                new TileSlot { tile = 400, region = 0, population = 50 },
                new TileSlot { tile = 12, region = 0, population = 20 },
                new TileSlot { tile = 800, region = 1, population = 10 },
                new TileSlot { tile = 4000, region = -1, population = 5 },
            };
            var linked = new[]
            {
                new LinkedPerson { pawnId = 900, tile = 400, index = 3, female = true, age = 44, raceKey = 1, factionKey = 0, ideoKey = 0 },
                new LinkedPerson { pawnId = 901, tile = 800, index = 0, female = false, age = 30, raceKey = -1, factionKey = 1, ideoKey = -1 },
            };
            var snap = PopulationSnapshot.From(Seed, rows, new[] { 77, 78 }, profiles, new[] { "Baseliner", "Hussar" }, new[] { "Empire \"the\" Great", "Tribe" }, new[] { "Creed\nof\tTabs" }, densityVersion: 9, linked: linked);
            return PopulationBuilder.Build(snap);
        }

        private static string Json(PopulationDataset ds) { var w = new StringWriter(); PopulationExport.WriteJson(ds, w); return w.ToString(); }

        private static int IndexOf(PopulationDataset ds, int tile, int index) => ds.TryTileRange(tile, out int s, out _) ? s + index : -1;

        private static bool SamePeople(PopulationDataset a, PopulationDataset b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                Individual x = a.people[i], y = b.people[i];
                if (x.tile != y.tile || x.index != y.index || x.female != y.female || x.age != y.age || x.ageBucket != y.ageBucket || x.education != y.education
                    || x.ses != y.ses || x.wealth != y.wealth || x.work != y.work || x.sector != y.sector || x.raceKey != y.raceKey || x.factionKey != y.factionKey
                    || x.ideoKey != y.ideoKey || x.pawnId != y.pawnId) return false;
            }
            return true;
        }

        private static int Sum(int[] a) { int s = 0; foreach (int x in a) s += x; return s; }
        private static bool Throws(Action f) { try { f(); return false; } catch (Exception) { return true; } }

        private static void Section(string name) => Console.WriteLine("-- " + name);

        private static void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + name);
            if (!ok) failures++;
        }
    }
}
