using Microsoft.Data.Sqlite;

namespace RegionsAndSocieties.PersistentWorld.Db
{
    /// <summary>
    /// The per-world database's tables (#17), created and migrated in place. One database per world:
    /// <list type="bullet">
    /// <item><c>meta</c> — schema version, world id and seed, so a database is never applied to the wrong world.</item>
    /// <item><c>commits</c> / <c>deltas</c> — the save lineage: each commit is one save file's diff against its
    /// parent (#18). This is the authoritative per-person state.</item>
    /// <item><c>builds</c>, <c>people</c>, <c>households</c>, <c>links</c>, <c>labels</c>, <c>regions</c> — the
    /// latest materialized census, replaced per build (#20). The analysis store and the SQL door.</item>
    /// </list>
    /// No game types; every method takes an open connection, so the harness can run it against a temp file.
    /// </summary>
    public static class CensusSchema
    {
        public const int Version = 1;

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

        private static void CreateAll(SqliteConnection c, SqliteTransaction tx)
        {
            // ---- lineage (#18): authoritative per-person state, one diff per save file ----
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

            // ---- census (#20): the latest materialized planet ----
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

        // Future versions add migrations here, oldest first. Version 1 is the first shipped schema.
        private static void Migrate(SqliteConnection c, SqliteTransaction tx, int from)
        {
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
