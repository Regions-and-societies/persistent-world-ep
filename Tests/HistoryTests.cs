// Behaviour tests for schema v2 and the history layer: a v1 file migrates in place with its data intact;
// the built-in vocabulary seeds every factor and sector of Core's locked model and never overwrites a Def
// row; region/cohort years, levels, flows, births and settlements upsert idempotently; person events are a
// save's history along its lineage (branches excluded) and go away with a collected commit.
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using RegionsAndSocieties.PersistentWorld.Db;
using RegionsAndSocieties.PersistentWorld.Population;

namespace HistoryTests
{
    public static class Program
    {
        private static int failures;

        public static int Main()
        {
            SQLitePCL.Batteries_V2.Init();
            string path = Path.Combine(Path.GetTempPath(), "pw-history-" + Guid.NewGuid().ToString("N") + ".db");
            string cs = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
            try
            {
                Section("v1 migrates in place");
                using (var c = Open(cs))
                {
                    CreateV1(c);
                    CensusSchema.Exec(c, "INSERT INTO commits(save_id, created) VALUES ('old', 'x'); INSERT INTO deltas VALUES ('old', 5, 1, 2, 3, 0); INSERT INTO people VALUES (5,1,2,3,-1,0,30,1,2,1,300,1,0,-1,-1,-1,0,0,1)");
                    CensusSchema.Ensure(c, "w", 1);
                    Check("version bumped", CensusSchema.CurrentVersion(c) == CensusSchema.Version && CensusSchema.Get(c, null, "model_version") == CensusSchema.ModelVersion);
                    Check("old rows survive", CensusStore.Count(c, "commits") == 1 && CensusStore.Count(c, "deltas") == 1 && CensusStore.Count(c, "people") == 1);
                    Check("new columns have defaults", (long)Scalar(c, "SELECT strata FROM people WHERE id = 5") == -1 && (long)Scalar(c, "SELECT partner FROM deltas WHERE person_id = 5") == -1);
                    Check("history tables exist and are empty", CensusStore.Count(c, "person_events") == 0 && CensusStore.Count(c, "cohort_years") == 0 && CensusStore.Count(c, "factors") == 0);
                    CensusSchema.Ensure(c, "w", 1);
                    Check("ensure again is a no-op", CensusSchema.CurrentVersion(c) == CensusSchema.Version && CensusStore.Count(c, "people") == 1);
                }

                Section("vocabulary");
                using (var c = Open(cs))
                {
                    var v = new VocabularyStore(c);
                    v.SeedBuiltin();
                    Check("every built-in factor and sector is there", CensusStore.Count(c, "factors") == BuiltinVocabulary.Factors.Length && CensusStore.Count(c, "sectors") == BuiltinVocabulary.Sectors.Length);
                    Check("categories cover the taxonomy", v.FactorNames("Stock").Count == 4 && v.FactorNames("Flow").Count == 5 && v.FactorNames("Context").Count >= 15 && v.FactorNames("Derived").Count >= 6);
                    Check("tiers", Join(v.TiersOf("RS_Education")) == "Illiterate,Primary,Secondary,Undergrad,Postgrad" && Join(v.TiersOf("RS_Strata")) == "Elite,Middle,Underclass" && v.TiersOf("RS_Wealth").Length == 0);
                    Check("sector tree", (long)Scalar(c, "SELECT COUNT(*) FROM sectors WHERE parent IS NULL") == 6 && (long)Scalar(c, "SELECT COUNT(*) FROM sectors WHERE parent = 'RS_Manufacturing'") == 10 && (long)Scalar(c, "SELECT labor_tier FROM sectors WHERE def_name = 'RS_Mfg_Fabrication'") == 4);
                    v.SeedBuiltin();
                    Check("seeding twice does not duplicate", CensusStore.Count(c, "factors") == BuiltinVocabulary.Factors.Length && CensusStore.Count(c, "factor_tiers") > 0);
                    v.UpsertFactors(new[] { new FactorRow { defName = "RS_Wealth", label = "from a def", category = "Mutable", kind = "Scalar", velocity = 0.5f, source = "def" } });
                    v.SeedBuiltin();
                    Check("a Def row is never overwritten by the seed", Scalar(c, "SELECT label FROM factors WHERE def_name = 'RS_Wealth'").ToString() == "from a def" && (double)Scalar(c, "SELECT velocity FROM factors WHERE def_name = 'RS_Wealth'") == 0.5);
                    v.UpsertEdges(new[] { new EdgeRow { factor = "RS_Wealth", source = "RS_EmploymentRate", weight = 0.12f }, new EdgeRow { factor = "RS_Education", source = "RS_SlaveryEnslavedShare", targetTier = "Secondary", weight = 1f, mode = "Ceiling", spill = "Primary" } });
                    Check("edges with modes", CensusStore.Count(c, "factor_edges") == 2 && Scalar(c, "SELECT spill FROM factor_edges WHERE mode = 'Ceiling'").ToString() == "Primary");
                    v.UpsertCohortKinds(new[] { new CohortKindRow { key = -1, defName = "Baseliner", label = "Human", inheritable = true, lifespan = 80 }, new CohortKindRow { key = 0, defName = "Sanguophage", label = "Sanguophage", inheritable = false, lifespan = 1000 } });
                    Check("cohort kinds", CensusStore.Count(c, "cohort_kinds") == 2 && (long)Scalar(c, "SELECT inheritable FROM cohort_kinds WHERE key = 0") == 0);
                }

                Section("years, levels, flows, births, settlements upsert idempotently");
                using (var c = Open(cs))
                {
                    var ls = new LineageStore(c);
                    string root = ls.Open(null, 0); ls.Seal(root, "first", 10);
                    string work = ls.Open(root, 10);
                    var h = new HistoryStore(c);
                    h.WriteRegionYear(root, new RegionYear { year = 1, region = 77, pop = 1000, wealth = 0.5f, secAgriculture = 0.4f });
                    h.WriteRegionYear(root, new RegionYear { year = 1, region = 77, pop = 1010, wealth = 0.55f });
                    h.WriteRegionYear(work, new RegionYear { year = 2, region = 77, pop = 1020 });
                    Check("region year replaces on the same key", CensusStore.Count(c, "region_years") == 2 && (double)Scalar(c, "SELECT pop FROM region_years WHERE year = 1") == 1010);
                    h.WriteCohortYear(root, new CohortYear { year = 1, region = 77, cohort = 0, pop = 700, share = 0.7f, lifeExpectancy = 62, leadingCause = "disease", eduSecondary = 0.44f });
                    h.WriteCohortYear(root, new CohortYear { year = 1, region = 77, cohort = 1, pop = 300, share = 0.3f, lifeExpectancy = 785, leadingCause = "disease", incomeSource = "services/public" });
                    Check("cohort years", CensusStore.Count(c, "cohort_years") == 2 && (double)Scalar(c, "SELECT life_expectancy FROM cohort_years WHERE cohort = 1") == 785 && Scalar(c, "SELECT income_source FROM cohort_years WHERE cohort = 1").ToString() == "services/public");
                    h.WriteLevels(root, new[] { new FactorLevel { year = 1, region = 77, cohort = -1, factor = "RS_Wealth", tier = "", current = 0.5f, target = 0.6f, moving = true }, new FactorLevel { year = 1, region = 77, cohort = 0, factor = "RS_Education", tier = "Secondary", current = 0.44f, target = 0.45f } });
                    h.WriteLevels(root, new[] { new FactorLevel { year = 1, region = 77, cohort = -1, factor = "RS_Wealth", tier = "", current = 0.51f, target = 0.6f, moving = true } });
                    Check("levels, long format, replaced on key", CensusStore.Count(c, "factor_levels") == 2 && (double)Scalar(c, "SELECT current FROM factor_levels WHERE factor = 'RS_Wealth'") > 0.505);
                    h.WriteFlow(root, new FlowRow { year = 1, region = 77, cohort = 0, birthsGross = 20, birthsEffective = 18, deaths = 12, dDisease = 5, dOldAge = 4, immigration = 3, emigration = 1 });
                    Check("flows", CensusStore.Count(c, "flows") == 1 && (double)Scalar(c, "SELECT d_disease FROM flows") == 5);
                    h.WriteBirths(root, new[] { new BirthAssignment { year = 1, region = 77, motherCohort = 0, fatherCohort = 0, childCohort = 0, count = 15 }, new BirthAssignment { year = 1, region = 77, motherCohort = 0, fatherCohort = 1, childCohort = 2, count = 3 } });
                    Check("births assigned (a cross pairing yields a hybrid cohort)", CensusStore.Count(c, "births_assigned") == 2 && (long)Scalar(c, "SELECT child_cohort FROM births_assigned WHERE father_cohort = 1") == 2);
                    h.WriteSettlement(root, new SettlementYear { year = 1, region = 77, tile = 400, kind = "colony", headcount = 12, wealth = 0.7f, education = 0.6f, ideo = 0, xenotypes = "0:10,1:2" });
                    Check("settlement actor", CensusStore.Count(c, "settlement_years") == 1 && Scalar(c, "SELECT kind FROM settlement_years").ToString() == "colony");
                    Check("last year along the lineage", h.LastYear(work, 77) == 2 && h.LastYear(root, 77) == 1 && h.LastYear(work, 78) == -1);

                    Section("person events are a lineage's history");
                    long alice = PersonId.Make(1, 400, 7), bob = PersonId.Make(1, 400, 8);
                    long s1 = h.Append(root, new PersonEvent { year = 0, tick = 0, person = alice, kind = EventKind.Linked, toValue = 900, region = 77 });
                    h.AppendAll(work, new[]
                    {
                        new PersonEvent { year = 1, tick = 3600000, person = alice, kind = EventKind.Moved, fromValue = 400, toValue = 800, cause = "war", region = 77 },
                        new PersonEvent { year = 1, tick = 3600000, person = bob, kind = EventKind.Died, cause = "disease", region = 77 },
                    });
                    string branch = ls.Open(root, 10); ls.Seal(branch, "branch", 12);
                    h.Append(branch, new PersonEvent { year = 1, tick = 1, person = alice, kind = EventKind.Enslaved, region = 77 });
                    List<PersonEvent> a = h.EventsOf(alice, work);
                    Check("oldest first, ancestors included, branches excluded", a.Count == 2 && a[0].kind == EventKind.Linked && a[0].seq == s1 && a[1].kind == EventKind.Moved && a[1].cause == "war" && a[1].fromValue == 400 && a[1].toValue == 800);
                    Check("the branch sees the root and its own", h.EventsOf(alice, branch).Count == 2 && h.EventsOf(alice, branch)[1].kind == EventKind.Enslaved);
                    Check("counts by kind along the lineage", h.EventCounts(work)[EventKind.Moved] == 1 && h.EventCounts(work)[EventKind.Died] == 1 && !h.EventCounts(work).ContainsKey(EventKind.Enslaved));
                    Check("unknown save has no history", h.EventsOf(alice, "nope").Count == 0 && h.EventsOf(alice, null).Count == 0);

                    Section("a collected commit takes its history with it");
                    ls.MarkDeleted(new[] { "branch" });
                    int dropped = ls.Collect(work, DateTime.UtcNow, TimeSpan.Zero);
                    // Two: the deleted branch, and the unsealed 'old' commit planted by the migration section above.
                    Check("the branch commit and the abandoned v1 commit were collected, nothing else", dropped == 2 && !ls.Exists(branch) && !ls.Exists("old") && ls.Exists(root) && ls.Exists(work));
                    Check("the branch's event is gone", h.EventsOf(alice, branch).Count == 0);
                    Check("the other events stay", CensusStore.Count(c, "person_events") == 3);
                    Check("the other years stay", CensusStore.Count(c, "region_years") == 2);
                }

                Section("the SQL door reaches the history");
                using (var c = Open(cs))
                {
                    var rows = CensusStore.Query(c, "SELECT kind, COUNT(*) AS n FROM person_events GROUP BY kind ORDER BY kind");
                    Check("grouped over events", rows.Count == 3 && rows[0]["kind"].ToString() == EventKind.Died);
                    Check("joins cohort years to cohort kinds", CensusStore.Query(c, "SELECT k.label, y.life_expectancy FROM cohort_years y JOIN cohort_kinds k ON k.key = y.cohort WHERE y.cohort = 0").Count == 1);
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try { File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); } catch (Exception) { }
            }

            Console.WriteLine(failures == 0 ? "history: all checks passed" : $"history: {failures} FAILED");
            return failures == 0 ? 0 : 1;
        }

        // The version-1 layout, verbatim, so the migration path is exercised against what shipped.
        private static void CreateV1(SqliteConnection c)
        {
            CensusSchema.Exec(c, "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL); INSERT INTO meta VALUES ('schema_version', '1')");
            CensusSchema.Exec(c, "CREATE TABLE commits (save_id TEXT PRIMARY KEY, parent_id TEXT, tick INTEGER NOT NULL DEFAULT 0, file_name TEXT, created TEXT NOT NULL, sealed INTEGER NOT NULL DEFAULT 0, superseded INTEGER NOT NULL DEFAULT 0, missing_since TEXT)");
            CensusSchema.Exec(c, "CREATE TABLE deltas (save_id TEXT NOT NULL, person_id INTEGER NOT NULL, birth_tile INTEGER NOT NULL, birth_index INTEGER NOT NULL, home_tile INTEGER NOT NULL, flags INTEGER NOT NULL, PRIMARY KEY (save_id, person_id)) WITHOUT ROWID");
            CensusSchema.Exec(c, "CREATE TABLE builds (serial INTEGER PRIMARY KEY, tick INTEGER NOT NULL, seed INTEGER NOT NULL, people INTEGER NOT NULL, households INTEGER NOT NULL, linked INTEGER NOT NULL, moved INTEGER NOT NULL, millis INTEGER NOT NULL, created TEXT NOT NULL)");
            CensusSchema.Exec(c, "CREATE TABLE people (id INTEGER PRIMARY KEY, birth_tile INTEGER NOT NULL, birth_index INTEGER NOT NULL, home INTEGER NOT NULL, region INTEGER NOT NULL, female INTEGER NOT NULL, age INTEGER NOT NULL, age_bucket INTEGER NOT NULL, education INTEGER NOT NULL, class INTEGER NOT NULL, wealth INTEGER NOT NULL, work INTEGER NOT NULL, sector INTEGER NOT NULL, race INTEGER NOT NULL, faction INTEGER NOT NULL, ideo INTEGER NOT NULL, pawn INTEGER NOT NULL, household INTEGER NOT NULL, household_size INTEGER NOT NULL)");
            CensusSchema.Exec(c, "CREATE TABLE households (tile INTEGER NOT NULL, idx INTEGER NOT NULL, size INTEGER NOT NULL, head INTEGER NOT NULL, PRIMARY KEY (tile, idx)) WITHOUT ROWID");
            CensusSchema.Exec(c, "CREATE TABLE links (pawn INTEGER PRIMARY KEY, person INTEGER NOT NULL, birth_tile INTEGER NOT NULL, birth_index INTEGER NOT NULL)");
            CensusSchema.Exec(c, "CREATE TABLE labels (kind TEXT NOT NULL, key INTEGER NOT NULL, label TEXT NOT NULL, PRIMARY KEY (kind, key)) WITHOUT ROWID");
            CensusSchema.Exec(c, "CREATE TABLE regions (slot INTEGER PRIMARY KEY, province INTEGER NOT NULL)");
            CensusSchema.Exec(c, "CREATE TABLE tiles (tile INTEGER PRIMARY KEY, region INTEGER NOT NULL, residents INTEGER NOT NULL)");
        }

        private static SqliteConnection Open(string cs) { var c = new SqliteConnection(cs); c.Open(); return c; }
        private static object Scalar(SqliteConnection c, string sql) { using (var cmd = c.CreateCommand()) { cmd.CommandText = sql; cmd.Parameters.AddWithValue("$b", "-"); return cmd.ExecuteScalar(); } }
        private static string Join(string[] a) => string.Join(",", a);

        private static void Section(string name) => Console.WriteLine("-- " + name);

        private static void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + name);
            if (!ok) failures++;
        }
    }
}
