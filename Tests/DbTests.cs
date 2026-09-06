// Behaviour tests for the database layer (#17-#20): the schema creates and re-ensures cleanly; the save
// lineage resolves nearest-record-wins along a chain, honours tombstones, keeps branches apart, seals and
// supersedes on overwrite, observes missing files and collects only what nothing can load; the census
// tables round-trip a dataset person for person; the SQL door reads and refuses writes. Runs on a temp
// file with the stock Microsoft.Data.Sqlite bundle (the mod's own native loader is game-side).
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using RegionsAndSocieties.PersistentWorld.Db;
using RegionsAndSocieties.PersistentWorld.Population;

namespace DbTests
{
    public static class Program
    {
        private static int failures;
        private const int Seed = 5150;

        public static int Main()
        {
            SQLitePCL.Batteries_V2.Init();
            string path = Path.Combine(Path.GetTempPath(), "pw-dbtests-" + Guid.NewGuid().ToString("N") + ".db");
            string cs = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
            try
            {
                Section("schema");
                using (var c = Open(cs))
                {
                    CensusSchema.Ensure(c, "abc", Seed);
                    Check("version and meta written", CensusSchema.CurrentVersion(c) == CensusSchema.Version && CensusSchema.Get(c, null, "world_id") == "abc" && CensusSchema.Get(c, null, "world_seed") == Seed.ToString());
                    CensusSchema.Ensure(c, "abc", Seed);
                    Check("ensure is idempotent", CensusSchema.CurrentVersion(c) == CensusSchema.Version && CensusStore.Count(c, "commits") == 0);
                    Check("wal mode", (string)Scalar(c, "PRAGMA journal_mode") == "wal");
                }

                Section("lineage: chain, nearest wins, tombstones");
                using (var c = Open(cs))
                {
                    var s = new LineageStore(c);
                    string root = s.Open(null, 0);
                    s.UpsertAll(root, new[] { D(1, 100), D(2, 100), D(3, 100) });
                    s.Seal(root, "first", 10);
                    string mid = s.Open(root, 10);
                    s.Upsert(mid, D(2, 200));               // person 2 moved again
                    s.Clear(mid, 3, 10, 3);                 // person 3 went home
                    s.Upsert(mid, D(4, 300));               // person 4 first moved here
                    s.Seal(mid, "second", 20);
                    string tip = s.Open(mid, 20);
                    s.Upsert(tip, D(1, 400));
                    PersonDelta[] atRoot = s.Resolve(root), atMid = s.Resolve(mid), atTip = s.Resolve(tip);
                    Check("root sees its own three", atRoot.Length == 3 && Home(atRoot, 1) == 100 && Home(atRoot, 2) == 100 && Home(atRoot, 3) == 100);
                    Check("mid: nearest wins, tombstone drops, new appears", atMid.Length == 3 && Home(atMid, 1) == 100 && Home(atMid, 2) == 200 && Home(atMid, 3) == int.MinValue && Home(atMid, 4) == 300);
                    Check("tip inherits mid and overrides person 1", atTip.Length == 3 && Home(atTip, 1) == 400 && Home(atTip, 2) == 200 && Home(atTip, 4) == 300);
                    Check("resolve is sorted by id and identity records are never returned", Sorted(atTip));
                    Check("unknown save resolves to nothing", s.Resolve("nope").Length == 0 && s.Resolve(null).Length == 0);
                    Check("info", s.Info(mid).Value.parentId == root && s.Info(mid).Value.fileName == "second" && s.Info(mid).Value.sealed_ && s.Info(mid).Value.deltaCount == 3 && s.Info(tip).Value.sealed_ == false);
                    Check("exists", s.Exists(root) && !s.Exists("x") && !s.Exists(null));

                    Section("lineage: branches stay apart");
                    string branch = s.Open(root, 10);      // the player loaded "first" again and played on
                    s.Upsert(branch, D(2, 999));
                    s.Seal(branch, "branchsave", 15);
                    Check("the branch sees root plus its own change", Home(s.Resolve(branch), 2) == 999 && Home(s.Resolve(branch), 4) == int.MinValue);
                    Check("the original chain is untouched", Home(s.Resolve(tip), 2) == 200);
                    Check("upsert replaces within a commit", Replace(s, branch) == 1);

                    Section("lineage: overwrite supersedes, delete marks, collect unwinds");
                    string again = s.Open(tip, 30);
                    s.Seal(again, "second", 30);           // overwrote the file "second": mid's file claim is gone
                    Check("the overwritten commit is superseded but kept (it has descendants)", s.Info(mid).Value.superseded && s.Info(mid).Value.fileName == null && s.Exists(mid));
                    s.ObserveFiles(new[] { "first", "second" }, Now(0));
                    Check("a sealed commit whose file is absent is stamped missing", s.Info(branch).Value.missingSince != null && s.Info(root).Value.missingSince == null);
                    s.ObserveFiles(new[] { "first", "second", "branchsave" }, Now(1));
                    Check("a file that reappears clears the stamp", s.Info(branch).Value.missingSince == null);
                    s.ObserveFiles(new[] { "first", "second" }, Now(1));
                    Check("nothing collected inside the grace period", s.Collect(again, Now(2), TimeSpan.FromDays(7)) == 0 && s.Exists(branch));
                    Check("collected after the grace period", s.Collect(again, Now(9), TimeSpan.FromDays(7)) == 1 && !s.Exists(branch) && s.Exists(root));
                    Check("an in-dialog delete is collected at once, but not the ancestors a live save needs", s.MarkDeleted(new[] { "first" }) == 1 && s.Collect(again, Now(9), TimeSpan.FromDays(7)) == 0 && s.Exists(root));
                    Check("the working commit is never collected", s.Collect(again, Now(99), TimeSpan.Zero) == 0 && s.Exists(again));
                    string abandoned = s.Open(again, 40);  // a session that quit without saving
                    Check("an abandoned working commit is collected once it is not current", s.Collect(again, Now(99), TimeSpan.Zero) == 1 && !s.Exists(abandoned));
                    s.MarkDeleted(new[] { "second" });
                    int dropped = s.Collect(null, Now(99), TimeSpan.Zero);
                    Check("deleting the last live save unwinds the whole dead chain", dropped == 4 && CensusStore.Count(c, "commits") == 0 && CensusStore.Count(c, "deltas") == 0);
                }

                Section("census tables round-trip");
                PopulationDataset ds = Planet();
                using (var c = Open(cs))
                {
                    CensusStore.Write(c, ds, 1234);
                    Check("counts", CensusStore.Count(c, "people") == ds.Count && CensusStore.Count(c, "households") == ds.HouseholdCount && CensusStore.Count(c, "links") == 2 && CensusStore.Count(c, "builds") == 1 && CensusStore.Count(c, "tiles") == ds.TileCount);
                    PopulationDataset back = CensusStore.Read(c, Seed);
                    Check("reads back", back != null && back.Count == ds.Count && back.TileCount == ds.TileCount && back.LinkedCount == 2 && back.HouseholdCount == ds.HouseholdCount);
                    Check("person for person", back != null && SamePeople(ds, back));
                    Check("labels and regions", back != null && back.snapshot.raceLabels[1] == "Hussar" && back.RegionIds[1] == 78 && back.TryGetBorn(800, 2, out Individual q) && back.RegionOf(in q) == 78);
                    Check("a moved person is under the new home", back != null && back.TryGetBorn(400, 7, out Individual m) && m.tile == 800 && m.Moved);
                    Check("another seed reads nothing", CensusStore.Read(c, Seed + 1) == null);
                    CensusStore.Write(c, ds, 1235);
                    Check("write replaces in place", CensusStore.Count(c, "people") == ds.Count && CensusStore.Count(c, "builds") == 1);
                    Check("empty dataset writes an empty census", Write(c, PopulationDataset.Empty) && CensusStore.Count(c, "people") == 0);
                }

                Section("the SQL door");
                using (var c = Open(cs))
                {
                    CensusStore.Write(c, ds, 1);
                    var rows = CensusStore.Query(c, "SELECT home, COUNT(*) AS n FROM people GROUP BY home ORDER BY home");
                    Check("a grouped query", rows.Count == ds.TileCount && Convert.ToInt64(rows[0]["n"]) > 0 && rows[0].ContainsKey("home"));
                    Check("limit", CensusStore.Query(c, "SELECT id FROM people", 5).Count == 5);
                    bool refused = false;
                    try { CensusStore.Query(c, "DELETE FROM people"); } catch (SqliteException) { refused = true; }
                    Check("writes are refused", refused && CensusStore.Count(c, "people") == ds.Count);
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try { File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); } catch (Exception) { }
            }

            Console.WriteLine(failures == 0 ? "db: all checks passed" : $"db: {failures} FAILED");
            return failures == 0 ? 0 : 1;
        }

        private static SqliteConnection Open(string cs) { var c = new SqliteConnection(cs); c.Open(); return c; }
        private static object Scalar(SqliteConnection c, string sql) { using (var cmd = c.CreateCommand()) { cmd.CommandText = sql; return cmd.ExecuteScalar(); } }
        private static DateTime Now(int days) => new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(days);
        private static PersonDelta D(long id, int home) => new PersonDelta { id = id, birthTile = 10, birthIndex = (int)id, homeTile = home };
        private static int Home(PersonDelta[] ds, long id) { foreach (PersonDelta d in ds) if (d.id == id) return d.homeTile; return int.MinValue; }
        private static bool Sorted(PersonDelta[] ds) { for (int i = 1; i < ds.Length; i++) if (ds[i].id <= ds[i - 1].id) return false; foreach (PersonDelta d in ds) if (d.IsIdentity) return false; return true; }
        private static int Replace(LineageStore s, string id) { s.Upsert(id, D(2, 998)); s.Upsert(id, D(2, 997)); return s.Info(id).Value.deltaCount; }
        private static bool Write(SqliteConnection c, PopulationDataset ds) { CensusStore.Write(c, ds, 0); return true; }

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
            var overlay = new PopulationOverlay();
            overlay.Move(PersonId.Make(Seed, 400, 7), 400, 7, 800);
            var linked = new[]
            {
                new LinkedPerson { id = PersonId.Make(Seed, 400, 3), birthTile = 400, birthIndex = 3, pawnId = 900, female = true, age = 44, raceKey = 1, factionKey = 0, ideoKey = 0 },
                new LinkedPerson { id = PersonId.Make(Seed, 800, 0), birthTile = 800, birthIndex = 0, pawnId = 901, female = false, age = 30, raceKey = -1, factionKey = 1, ideoKey = -1 },
            };
            return PopulationBuilder.Build(PopulationSnapshot.From(Seed, rows, new[] { 77, 78 }, profiles, new[] { "Baseliner", "Hussar" }, new[] { "Empire", "Tribe" }, new[] { "Creed" }, deltas: overlay.Records(), linked: linked));
        }

        private static bool SamePeople(PopulationDataset a, PopulationDataset b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                Individual x = a.people[i], y = b.people[i];
                if (x.id != y.id || x.tile != y.tile || x.birthTile != y.birthTile || x.birthIndex != y.birthIndex || x.female != y.female || x.age != y.age || x.ageBucket != y.ageBucket
                    || x.education != y.education || x.ses != y.ses || x.wealth != y.wealth || x.work != y.work || x.sector != y.sector || x.raceKey != y.raceKey
                    || x.factionKey != y.factionKey || x.ideoKey != y.ideoKey || x.pawnId != y.pawnId || x.household != y.household || x.householdSize != y.householdSize) return false;
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
