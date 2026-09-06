using Microsoft.Data.Sqlite;

namespace RegionsAndSocieties.PersistentWorld.Db
{
    /// <summary>
    /// The per-world database's tables, created and migrated in place. Version 2 mirrors Core's locked
    /// demographic model (Core-MMF <c>Design/DEMOGRAPHIC_MODEL.md</c>, 0.4.0 keystone): xenotype cohorts are
    /// the unit, the region is an aggregate, the graph steps once per demographic year, and every axis is
    /// tracked per cohort. Four groups of tables:
    /// <list type="bullet">
    /// <item><b>Vocabulary</b> — <c>factors</c> / <c>factor_tiers</c> / <c>factor_edges</c> (the influence
    /// graph, patchable XML in Core), <c>sectors</c> (the economic sector tree), <c>cohort_kinds</c> (xenotypes
    /// with their gene-derived constants). Data, not schema, so a patch's factor needs no migration.</item>
    /// <item><b>Lineage</b> — <c>commits</c> / <c>deltas</c>: authoritative per-person state, one diff per
    /// save file (#18). Every history row below carries the <c>save_id</c> it was written under, so a
    /// branch's history is the union along its ancestry and a collected commit takes its rows with it.</item>
    /// <item><b>History</b> — per demographic year: <c>region_years</c> (the shared stage + aggregates + geo),
    /// <c>cohort_years</c> (every per-cohort axis and the mechanism outputs: vitals, wealth as
    /// income/cost/assets, indicators), <c>factor_levels</c> (any factor's current/target, long format),
    /// <c>flows</c> (births, deaths by cause, migration, combat), <c>births_assigned</c> (the mating model's
    /// answer: which cohort the children of two cohorts belong to), <c>settlement_years</c> (the inbound
    /// SettlementActor hook), and <c>person_events</c> — what happened to whom, the persistent demographic
    /// history itself.</item>
    /// <item><b>Census</b> — <c>builds</c>, <c>people</c>, <c>households</c>, <c>links</c>, <c>labels</c>,
    /// <c>regions</c>, <c>tiles</c>: the latest materialized planet, replaced per build (#20).</item>
    /// </list>
    /// No game types; every method takes an open connection, so the harness runs it against a temp file.
    /// </summary>
    public static class CensusSchema
    {
        public const int Version = 2;

        /// <summary>The Core design this schema mirrors; stored in <c>meta</c> so an analysis knows what it reads.</summary>
        public const string ModelVersion = "0.4.0 keystone (topology locked 2026-09-06)";

        public static void Ensure(SqliteConnection c, string worldId, int worldSeed)
        {
            Exec(c, "PRAGMA journal_mode=WAL");
            Exec(c, "PRAGMA synchronous=NORMAL");
            using (SqliteTransaction tx = c.BeginTransaction())
            {
                Exec(c, @"CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)", tx);
                int have = CurrentVersion(c, tx);
                if (have == 0) CreateAll(c, tx);
                else if (have < Version) Migrate(c, tx, have);
                Set(c, tx, "schema_version", Version.ToString());
                Set(c, tx, "model_version", ModelVersion);
                Set(c, tx, "world_id", worldId ?? "");
                Set(c, tx, "world_seed", worldSeed.ToString());
                tx.Commit();
            }
        }

        public static int CurrentVersion(SqliteConnection c, SqliteTransaction tx = null)
        {
            string v = Get(c, tx, "schema_version");
            return v != null && int.TryParse(v, out int n) ? n : 0;
        }

        public static string Get(SqliteConnection c, SqliteTransaction tx, string key)
        {
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
                cmd.Parameters.AddWithValue("$k", key);
                object o = cmd.ExecuteScalar();
                return o == null || o is System.DBNull ? null : (string)o;
            }
        }

        public static void Set(SqliteConnection c, SqliteTransaction tx, string key, string value)
        {
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO meta(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$v", value ?? "");
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Every history table that carries a <c>save_id</c>; a collected commit drops its rows from each.</summary>
        public static readonly string[] HistoryTables =
        {
            "region_years", "cohort_years", "factor_levels", "flows", "births_assigned", "settlement_years", "person_events",
        };

        private static void CreateAll(SqliteConnection c, SqliteTransaction tx)
        {
            CreateLineage(c, tx);
            CreateCensus(c, tx);
            CreateVocabulary(c, tx);
            CreateHistory(c, tx);
            AddPersonStateColumns(c, tx);
        }

        // Version 1 had lineage + census only; version 2 adds the vocabulary, the history, and the per-person
        // mutable-state columns. Additive only, so a v1 file upgrades in place with its data intact.
        private static void Migrate(SqliteConnection c, SqliteTransaction tx, int from)
        {
            if (from < 2)
            {
                CreateVocabulary(c, tx);
                CreateHistory(c, tx);
                AddPersonStateColumns(c, tx);
            }
        }

        // ---------------------------------------------------------------- lineage (#18)

        private static void CreateLineage(SqliteConnection c, SqliteTransaction tx)
        {
            Exec(c, @"CREATE TABLE commits (
                save_id       TEXT PRIMARY KEY,
                parent_id     TEXT,
                tick          INTEGER NOT NULL DEFAULT 0,
                file_name     TEXT,
                created       TEXT NOT NULL,
                sealed        INTEGER NOT NULL DEFAULT 0,
                superseded    INTEGER NOT NULL DEFAULT 0,
                missing_since TEXT)", tx);
            Exec(c, "CREATE INDEX commits_parent ON commits(parent_id)", tx);
            Exec(c, "CREATE INDEX commits_file ON commits(file_name)", tx);
            Exec(c, @"CREATE TABLE deltas (
                save_id     TEXT NOT NULL,
                person_id   INTEGER NOT NULL,
                birth_tile  INTEGER NOT NULL,
                birth_index INTEGER NOT NULL,
                home_tile   INTEGER NOT NULL,
                flags       INTEGER NOT NULL,
                PRIMARY KEY (save_id, person_id)) WITHOUT ROWID", tx);
        }

        // ---------------------------------------------------------------- census (#20)

        private static void CreateCensus(SqliteConnection c, SqliteTransaction tx)
        {
            Exec(c, @"CREATE TABLE builds (
                serial     INTEGER PRIMARY KEY,
                tick       INTEGER NOT NULL,
                seed       INTEGER NOT NULL,
                people     INTEGER NOT NULL,
                households INTEGER NOT NULL,
                linked     INTEGER NOT NULL,
                moved      INTEGER NOT NULL,
                millis     INTEGER NOT NULL,
                created    TEXT NOT NULL)", tx);
            Exec(c, @"CREATE TABLE people (
                id             INTEGER PRIMARY KEY,
                birth_tile     INTEGER NOT NULL,
                birth_index    INTEGER NOT NULL,
                home           INTEGER NOT NULL,
                region         INTEGER NOT NULL,
                female         INTEGER NOT NULL,
                age            INTEGER NOT NULL,
                age_bucket     INTEGER NOT NULL,
                education      INTEGER NOT NULL,
                class          INTEGER NOT NULL,
                wealth         INTEGER NOT NULL,
                work           INTEGER NOT NULL,
                sector         INTEGER NOT NULL,
                race           INTEGER NOT NULL,
                faction        INTEGER NOT NULL,
                ideo           INTEGER NOT NULL,
                pawn           INTEGER NOT NULL,
                household      INTEGER NOT NULL,
                household_size INTEGER NOT NULL)", tx);
            Exec(c, "CREATE INDEX people_home ON people(home)", tx);
            Exec(c, "CREATE INDEX people_region ON people(region)", tx);
            Exec(c, "CREATE INDEX people_pawn ON people(pawn) WHERE pawn != 0", tx);
            Exec(c, @"CREATE TABLE households (
                tile INTEGER NOT NULL,
                idx  INTEGER NOT NULL,
                size INTEGER NOT NULL,
                head INTEGER NOT NULL,
                PRIMARY KEY (tile, idx)) WITHOUT ROWID", tx);
            Exec(c, @"CREATE TABLE links (
                pawn        INTEGER PRIMARY KEY,
                person      INTEGER NOT NULL,
                birth_tile  INTEGER NOT NULL,
                birth_index INTEGER NOT NULL)", tx);
            Exec(c, @"CREATE TABLE labels (
                kind  TEXT NOT NULL,
                key   INTEGER NOT NULL,
                label TEXT NOT NULL,
                PRIMARY KEY (kind, key)) WITHOUT ROWID", tx);
            Exec(c, @"CREATE TABLE regions (
                slot     INTEGER PRIMARY KEY,
                province INTEGER NOT NULL)", tx);
            Exec(c, @"CREATE TABLE tiles (
                tile      INTEGER PRIMARY KEY,
                region    INTEGER NOT NULL,
                residents INTEGER NOT NULL)", tx);
        }

        // The person-level state the model makes mutable (§3 strata and slavery, §5 wealth as income / cost /
        // assets, §7 indicators, §8 partners and parents) plus the life record. Defaults mean "not yet
        // resolved"; the conditional model (0.2.0) and the dynamics (0.3.0) fill them.
        private static void AddPersonStateColumns(SqliteConnection c, SqliteTransaction tx)
        {
            string[] cols =
            {
                "strata INTEGER NOT NULL DEFAULT -1",          // Elite 0 / Middle 1 / Underclass 2
                "enslaved INTEGER NOT NULL DEFAULT 0",
                "partner INTEGER NOT NULL DEFAULT 0",          // person id, 0 = none
                "mother INTEGER NOT NULL DEFAULT 0",
                "father INTEGER NOT NULL DEFAULT 0",
                "born_year INTEGER NOT NULL DEFAULT -1",       // demographic year, -1 = born at worldgen
                "died_year INTEGER NOT NULL DEFAULT -1",
                "cause_of_death TEXT",
                "income REAL NOT NULL DEFAULT 0",              // silver per day
                "cost_living REAL NOT NULL DEFAULT 0",
                "assets REAL NOT NULL DEFAULT 0",
                "contentment REAL NOT NULL DEFAULT -1",
                "substance_use REAL NOT NULL DEFAULT -1",
                "healthcare REAL NOT NULL DEFAULT -1",
            };
            foreach (string col in cols) Exec(c, "ALTER TABLE people ADD COLUMN " + col, tx);
            // The lineage diff grows the same way: what a branch changed about a person beyond home and death.
            foreach (string col in new[] { "strata INTEGER NOT NULL DEFAULT -1", "enslaved INTEGER NOT NULL DEFAULT -1", "partner INTEGER NOT NULL DEFAULT -1", "education INTEGER NOT NULL DEFAULT -1", "class INTEGER NOT NULL DEFAULT -1" })
                Exec(c, "ALTER TABLE deltas ADD COLUMN " + col, tx);
        }

        // ---------------------------------------------------------------- vocabulary (model §1–§2, §9)

        private static void CreateVocabulary(SqliteConnection c, SqliteTransaction tx)
        {
            Exec(c, @"CREATE TABLE factors (
                def_name     TEXT PRIMARY KEY,
                label        TEXT NOT NULL DEFAULT '',
                category     TEXT NOT NULL,      -- Stock | Flow | Mutable | Context | Derived
                kind         TEXT NOT NULL,      -- Scalar | Distribution
                tiers_from   TEXT,               -- a Def type whose entries are the tiers (XenotypeDef, MemeDef), else NULL
                velocity     REAL NOT NULL DEFAULT 0,
                inertia      REAL NOT NULL DEFAULT 0,
                deadband     REAL NOT NULL DEFAULT 0,
                release_band REAL NOT NULL DEFAULT 0,
                max_step     REAL NOT NULL DEFAULT 0,
                per_cohort   INTEGER NOT NULL DEFAULT 1,
                source       TEXT NOT NULL DEFAULT 'builtin')   -- builtin | def (synced from Core's Defs)", tx);
            Exec(c, @"CREATE TABLE factor_tiers (
                factor  TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                tier    TEXT NOT NULL,
                PRIMARY KEY (factor, ordinal)) WITHOUT ROWID", tx);
            Exec(c, @"CREATE TABLE factor_edges (
                factor      TEXT NOT NULL,
                source      TEXT NOT NULL,
                source_tier TEXT NOT NULL DEFAULT '',
                target_tier TEXT NOT NULL DEFAULT '',
                weight      REAL NOT NULL,
                curve       TEXT NOT NULL DEFAULT 'Linear',    -- Linear | Saturating | Threshold
                threshold   REAL,
                lag_years   INTEGER NOT NULL DEFAULT 0,
                mode        TEXT NOT NULL DEFAULT 'Stress',    -- Stress | Ceiling | Suppress
                spill       TEXT,
                PRIMARY KEY (factor, source, source_tier, target_tier)) WITHOUT ROWID", tx);
            Exec(c, @"CREATE TABLE sectors (
                def_name     TEXT PRIMARY KEY,
                parent       TEXT,
                label        TEXT NOT NULL DEFAULT '',
                labor_tier   INTEGER NOT NULL DEFAULT 0,   -- EducationTier ordinal at which it produces
                requires_dlc TEXT,
                source       TEXT NOT NULL DEFAULT 'builtin')", tx);
            Exec(c, @"CREATE TABLE cohort_kinds (
                key         INTEGER PRIMARY KEY,           -- the catalogue race key (-1 = plain human)
                def_name    TEXT NOT NULL,
                label       TEXT NOT NULL DEFAULT '',
                inheritable INTEGER NOT NULL DEFAULT 1,    -- germline breeds true; implanted (Sanguophage) does not
                lifespan    REAL NOT NULL DEFAULT 80,      -- from Longevity / Ageless genes
                dependency  REAL NOT NULL DEFAULT 0,       -- genetic drug dependency 0..1 (ChemicalDependency gene)
                fragility   REAL NOT NULL DEFAULT 0,       -- frailty genes -> infant mortality add
                fertility   REAL NOT NULL DEFAULT 0.45)", tx);
        }

        // ---------------------------------------------------------------- history (the persistent record)

        private static void CreateHistory(SqliteConnection c, SqliteTransaction tx)
        {
            // The shared stage (§1), the geo read (§6), and the population-weighted aggregates.
            Exec(c, @"CREATE TABLE region_years (
                save_id TEXT NOT NULL, year INTEGER NOT NULL, tick INTEGER NOT NULL, region INTEGER NOT NULL,
                pop REAL NOT NULL, carrying_capacity REAL NOT NULL DEFAULT 0, food_capacity REAL NOT NULL DEFAULT 0,
                area_km2 REAL NOT NULL DEFAULT 0, density REAL NOT NULL DEFAULT 0, food_self_suff REAL NOT NULL DEFAULT 0,
                wealth REAL NOT NULL DEFAULT 0, urbanisation REAL NOT NULL DEFAULT 0, employment REAL NOT NULL DEFAULT 0,
                health REAL NOT NULL DEFAULT 0, crime REAL NOT NULL DEFAULT 0, contentment REAL NOT NULL DEFAULT 0,
                substance_use REAL NOT NULL DEFAULT 0, housing REAL NOT NULL DEFAULT 0, dependency REAL NOT NULL DEFAULT 0,
                dev_level REAL NOT NULL DEFAULT 0, sanitation REAL NOT NULL DEFAULT 0, pollution REAL NOT NULL DEFAULT 0,
                conflict REAL NOT NULL DEFAULT 0, roads REAL NOT NULL DEFAULT 0, rigidity REAL NOT NULL DEFAULT 0,
                slavery_stance REAL NOT NULL DEFAULT 0, tolerance REAL NOT NULL DEFAULT 0, natalism REAL NOT NULL DEFAULT 0,
                balance REAL NOT NULL DEFAULT 0, inequality REAL NOT NULL DEFAULT 0, drug_burden_agg REAL NOT NULL DEFAULT 0,
                migration_agg REAL NOT NULL DEFAULT 0, birth_agg REAL NOT NULL DEFAULT 0, growth REAL NOT NULL DEFAULT 0,
                sec_agriculture REAL NOT NULL DEFAULT 0, sec_extraction REAL NOT NULL DEFAULT 0, sec_manufacturing REAL NOT NULL DEFAULT 0,
                sec_services REAL NOT NULL DEFAULT 0, sec_military REAL NOT NULL DEFAULT 0, sec_public REAL NOT NULL DEFAULT 0,
                PRIMARY KEY (save_id, year, region)) WITHOUT ROWID", tx);

            // Every per-cohort axis (§1) and the mechanism outputs (§3 standing/slavery/fight-flight, §4 vitals,
            // §5 income/cost/assets, §7 indicators, §8 births). One row per region x cohort x demographic year.
            Exec(c, @"CREATE TABLE cohort_years (
                save_id TEXT NOT NULL, year INTEGER NOT NULL, tick INTEGER NOT NULL, region INTEGER NOT NULL, cohort INTEGER NOT NULL,
                pop REAL NOT NULL, share REAL NOT NULL DEFAULT 0,
                standing REAL NOT NULL DEFAULT 0, base_preference REAL NOT NULL DEFAULT 0, drug_burden REAL NOT NULL DEFAULT 0,
                fertility REAL NOT NULL DEFAULT 0, birth_rate REAL NOT NULL DEFAULT 0, birth_mult REAL NOT NULL DEFAULT 1,
                migration_net REAL NOT NULL DEFAULT 0, fight REAL NOT NULL DEFAULT 0, flight REAL NOT NULL DEFAULT 0,
                wealth REAL NOT NULL DEFAULT 0, edu_index REAL NOT NULL DEFAULT 0, slave_share REAL NOT NULL DEFAULT 0,
                income REAL NOT NULL DEFAULT 0, cost_living REAL NOT NULL DEFAULT 0, net_daily REAL NOT NULL DEFAULT 0,
                assets REAL NOT NULL DEFAULT 0, income_source TEXT,
                edu_illiterate REAL NOT NULL DEFAULT 0, edu_primary REAL NOT NULL DEFAULT 0, edu_secondary REAL NOT NULL DEFAULT 0,
                edu_undergrad REAL NOT NULL DEFAULT 0, edu_postgrad REAL NOT NULL DEFAULT 0,
                strata_elite REAL NOT NULL DEFAULT 0, strata_middle REAL NOT NULL DEFAULT 0, strata_underclass REAL NOT NULL DEFAULT 0,
                ses_subsistence REAL NOT NULL DEFAULT 0, ses_modest REAL NOT NULL DEFAULT 0, ses_prosperous REAL NOT NULL DEFAULT 0, ses_affluent REAL NOT NULL DEFAULT 0,
                female_frac REAL NOT NULL DEFAULT 0.5, gender_diverse REAL NOT NULL DEFAULT 0,
                age_child REAL NOT NULL DEFAULT 0, age_working REAL NOT NULL DEFAULT 0, age_elder REAL NOT NULL DEFAULT 0, dependency REAL NOT NULL DEFAULT 0,
                employment REAL NOT NULL DEFAULT 0, healthcare REAL NOT NULL DEFAULT 0, housing REAL NOT NULL DEFAULT 0,
                contentment REAL NOT NULL DEFAULT 0, substance_use REAL NOT NULL DEFAULT 0, crime REAL NOT NULL DEFAULT 0,
                mortality REAL NOT NULL DEFAULT 0,
                hz_old_age REAL NOT NULL DEFAULT 0, hz_malnutrition REAL NOT NULL DEFAULT 0, hz_disease REAL NOT NULL DEFAULT 0,
                hz_violence REAL NOT NULL DEFAULT 0, hz_war REAL NOT NULL DEFAULT 0, hz_addiction REAL NOT NULL DEFAULT 0, hz_xenophobia REAL NOT NULL DEFAULT 0,
                life_expectancy REAL NOT NULL DEFAULT 0, infant_mortality REAL NOT NULL DEFAULT 0, leading_cause TEXT,
                PRIMARY KEY (save_id, year, region, cohort)) WITHOUT ROWID", tx);

            // Any factor's level, long format: a patch's factor is stored without a schema change. cohort -1 = region level.
            Exec(c, @"CREATE TABLE factor_levels (
                save_id TEXT NOT NULL, year INTEGER NOT NULL, region INTEGER NOT NULL, cohort INTEGER NOT NULL,
                factor TEXT NOT NULL, tier TEXT NOT NULL DEFAULT '',
                current REAL NOT NULL, target REAL NOT NULL DEFAULT 0, moving INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (save_id, year, region, cohort, factor, tier)) WITHOUT ROWID", tx);

            // The Flows (§2 taxonomy): the only thing that moves Stocks. Deaths decomposed by cause (§4).
            Exec(c, @"CREATE TABLE flows (
                save_id TEXT NOT NULL, year INTEGER NOT NULL, region INTEGER NOT NULL, cohort INTEGER NOT NULL,
                births_gross REAL NOT NULL DEFAULT 0, births_effective REAL NOT NULL DEFAULT 0, deaths REAL NOT NULL DEFAULT 0,
                d_old_age REAL NOT NULL DEFAULT 0, d_malnutrition REAL NOT NULL DEFAULT 0, d_disease REAL NOT NULL DEFAULT 0,
                d_violence REAL NOT NULL DEFAULT 0, d_war REAL NOT NULL DEFAULT 0, d_addiction REAL NOT NULL DEFAULT 0, d_xenophobia REAL NOT NULL DEFAULT 0,
                immigration REAL NOT NULL DEFAULT 0, emigration REAL NOT NULL DEFAULT 0, combat_losses REAL NOT NULL DEFAULT 0,
                enslaved REAL NOT NULL DEFAULT 0, freed REAL NOT NULL DEFAULT 0,
                PRIMARY KEY (save_id, year, region, cohort)) WITHOUT ROWID", tx);

            // Reproduction and inheritance (§8): endogamy vs exogamy resolved into which cohort the children join.
            Exec(c, @"CREATE TABLE births_assigned (
                save_id TEXT NOT NULL, year INTEGER NOT NULL, region INTEGER NOT NULL,
                mother_cohort INTEGER NOT NULL, father_cohort INTEGER NOT NULL, child_cohort INTEGER NOT NULL,
                count REAL NOT NULL,
                PRIMARY KEY (save_id, year, region, mother_cohort, father_cohort, child_cohort)) WITHOUT ROWID", tx);

            // The inbound SettlementActor hook (§9): what a colony / outpost / NPC base presented to its region.
            Exec(c, @"CREATE TABLE settlement_years (
                save_id TEXT NOT NULL, year INTEGER NOT NULL, tick INTEGER NOT NULL, region INTEGER NOT NULL, tile INTEGER NOT NULL,
                kind TEXT NOT NULL,                         -- colony | outpost | npc
                headcount REAL NOT NULL DEFAULT 0, wealth REAL NOT NULL DEFAULT 0, education REAL NOT NULL DEFAULT 0,
                sector_output TEXT, ideo INTEGER NOT NULL DEFAULT -1, xenotypes TEXT,
                PRIMARY KEY (save_id, year, tile)) WITHOUT ROWID", tx);

            // What happened to whom: the persistent demographic history at the person level.
            Exec(c, @"CREATE TABLE person_events (
                seq        INTEGER PRIMARY KEY AUTOINCREMENT,
                save_id    TEXT NOT NULL,
                year       INTEGER NOT NULL,
                tick       INTEGER NOT NULL,
                person     INTEGER NOT NULL,
                kind       TEXT NOT NULL,                   -- born | died | moved | enslaved | freed | partnered | separated | schooled | class | employed | unemployed | converted | linked
                from_value INTEGER NOT NULL DEFAULT 0,
                to_value   INTEGER NOT NULL DEFAULT 0,
                cause      TEXT,
                region     INTEGER NOT NULL DEFAULT -1)", tx);
            Exec(c, "CREATE INDEX person_events_person ON person_events(person, seq)", tx);
            Exec(c, "CREATE INDEX person_events_save ON person_events(save_id)", tx);
            Exec(c, "CREATE INDEX person_events_year ON person_events(year, kind)", tx);
            Exec(c, "CREATE INDEX region_years_region ON region_years(region, year)", tx);
            Exec(c, "CREATE INDEX cohort_years_region ON cohort_years(region, cohort, year)", tx);
        }

        public static void Exec(SqliteConnection c, string sql, SqliteTransaction tx = null)
        {
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        public static long Scalar(SqliteConnection c, string sql, SqliteTransaction tx = null)
        {
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                object o = cmd.ExecuteScalar();
                return o == null || o is System.DBNull ? 0 : (long)o;
            }
        }
    }
}
