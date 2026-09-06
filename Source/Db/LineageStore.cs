using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using RegionsAndSocieties.PersistentWorld.Population;

namespace RegionsAndSocieties.PersistentWorld.Db
{
    /// <summary>One save file's place in the lineage.</summary>
    public struct CommitInfo
    {
        public string saveId;
        public string parentId;
        public int tick;
        public string fileName;
        public bool sealed_;
        public bool superseded;
        public DateTime? missingSince;
        public int deltaCount;
    }

    /// <summary>
    /// The save lineage (#18): per-person state as a tree of diffs, one commit per save file. Loading a
    /// save resolves its effective overlay by walking its ancestry, nearest record wins. Play writes into
    /// a <i>working</i> commit whose parent is the loaded save; saving seals it under the file's name and
    /// opens a child. So an autosave, a manual save, and a branch the player loads from an older save each
    /// carry exactly what changed since their parent, and none can trample another.
    ///
    /// <para>A delta with <see cref="FlagCleared"/> is a tombstone: "this person is back to birth state",
    /// which a child commit needs to undo an ancestor's record. Pure over an open connection.</para>
    /// </summary>
    public sealed class LineageStore
    {
        /// <summary>Flag bit on a stored delta meaning "no delta any more" (overrides an ancestor's record).</summary>
        public const int FlagCleared = 0x80;

        private readonly SqliteConnection c;

        public LineageStore(SqliteConnection connection) { c = connection; }

        /// <summary>A fresh commit id.</summary>
        public static string NewId() => Guid.NewGuid().ToString("N");

        // ---------------------------------------------------------------- commits

        public bool Exists(string saveId)
        {
            if (string.IsNullOrEmpty(saveId)) return false;
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT 1 FROM commits WHERE save_id = $id";
                cmd.Parameters.AddWithValue("$id", saveId);
                return cmd.ExecuteScalar() != null;
            }
        }

        /// <summary>Open a working commit under <paramref name="parentId"/> (null for a root). Returns its id.</summary>
        public string Open(string parentId, int tick, string saveId = null)
        {
            saveId = saveId ?? NewId();
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO commits(save_id, parent_id, tick, created) VALUES ($id, $p, $t, $now)";
                cmd.Parameters.AddWithValue("$id", saveId);
                cmd.Parameters.AddWithValue("$p", (object)parentId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$t", tick);
                cmd.Parameters.AddWithValue("$now", Now());
                cmd.ExecuteNonQuery();
            }
            return saveId;
        }

        /// <summary>Seal the working commit as save file <paramref name="fileName"/> at <paramref name="tick"/>.
        /// Any other commit that claimed the same file name is now superseded (the game overwrote it).</summary>
        public void Seal(string saveId, string fileName, int tick)
        {
            using (SqliteTransaction tx = c.BeginTransaction())
            {
                using (SqliteCommand cmd = c.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "UPDATE commits SET superseded = 1, file_name = NULL WHERE file_name = $f AND save_id != $id";
                    cmd.Parameters.AddWithValue("$f", fileName);
                    cmd.Parameters.AddWithValue("$id", saveId);
                    cmd.ExecuteNonQuery();
                }
                using (SqliteCommand cmd = c.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "UPDATE commits SET sealed = 1, file_name = $f, tick = $t, missing_since = NULL WHERE save_id = $id";
                    cmd.Parameters.AddWithValue("$f", fileName);
                    cmd.Parameters.AddWithValue("$t", tick);
                    cmd.Parameters.AddWithValue("$id", saveId);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }

        public CommitInfo? Info(string saveId)
        {
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT save_id, parent_id, tick, file_name, sealed, superseded, missing_since, (SELECT COUNT(*) FROM deltas d WHERE d.save_id = commits.save_id) FROM commits WHERE save_id = $id";
                cmd.Parameters.AddWithValue("$id", saveId);
                using (SqliteDataReader r = cmd.ExecuteReader())
                    return r.Read() ? Read(r) : (CommitInfo?)null;
            }
        }

        public List<CommitInfo> All()
        {
            var list = new List<CommitInfo>();
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT save_id, parent_id, tick, file_name, sealed, superseded, missing_since, (SELECT COUNT(*) FROM deltas d WHERE d.save_id = commits.save_id) FROM commits ORDER BY created";
                using (SqliteDataReader r = cmd.ExecuteReader())
                    while (r.Read()) list.Add(Read(r));
            }
            return list;
        }

        private static CommitInfo Read(SqliteDataReader r) => new CommitInfo
        {
            saveId = r.GetString(0),
            parentId = r.IsDBNull(1) ? null : r.GetString(1),
            tick = r.GetInt32(2),
            fileName = r.IsDBNull(3) ? null : r.GetString(3),
            sealed_ = r.GetInt64(4) != 0,
            superseded = r.GetInt64(5) != 0,
            missingSince = r.IsDBNull(6) ? (DateTime?)null : DateTime.Parse(r.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind),
            deltaCount = r.GetInt32(7),
        };

        // ---------------------------------------------------------------- deltas

        /// <summary>Record a person's current state in the working commit (replacing any earlier record there).</summary>
        public void Upsert(string saveId, in PersonDelta d)
        {
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = @"INSERT INTO deltas(save_id, person_id, birth_tile, birth_index, home_tile, flags) VALUES ($s, $p, $bt, $bi, $h, $f)
                    ON CONFLICT(save_id, person_id) DO UPDATE SET birth_tile = excluded.birth_tile, birth_index = excluded.birth_index, home_tile = excluded.home_tile, flags = excluded.flags";
                cmd.Parameters.AddWithValue("$s", saveId);
                cmd.Parameters.AddWithValue("$p", d.id);
                cmd.Parameters.AddWithValue("$bt", d.birthTile);
                cmd.Parameters.AddWithValue("$bi", d.birthIndex);
                cmd.Parameters.AddWithValue("$h", d.homeTile);
                cmd.Parameters.AddWithValue("$f", (int)d.flags);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Record that a person is back to birth state: a tombstone that hides ancestors' records.</summary>
        public void Clear(string saveId, long id, int birthTile, int birthIndex)
        {
            Upsert(saveId, new PersonDelta { id = id, birthTile = birthTile, birthIndex = birthIndex, homeTile = birthTile, flags = FlagCleared });
        }

        /// <summary>Write many records in one transaction (the recovery root, or a bulk commit).</summary>
        public void UpsertAll(string saveId, IEnumerable<PersonDelta> deltas)
        {
            using (SqliteTransaction tx = c.BeginTransaction())
            {
                using (SqliteCommand cmd = c.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT INTO deltas(save_id, person_id, birth_tile, birth_index, home_tile, flags) VALUES ($s, $p, $bt, $bi, $h, $f)
                        ON CONFLICT(save_id, person_id) DO UPDATE SET birth_tile = excluded.birth_tile, birth_index = excluded.birth_index, home_tile = excluded.home_tile, flags = excluded.flags";
                    SqliteParameter s = cmd.Parameters.Add("$s", SqliteType.Text), p = cmd.Parameters.Add("$p", SqliteType.Integer),
                        bt = cmd.Parameters.Add("$bt", SqliteType.Integer), bi = cmd.Parameters.Add("$bi", SqliteType.Integer),
                        h = cmd.Parameters.Add("$h", SqliteType.Integer), f = cmd.Parameters.Add("$f", SqliteType.Integer);
                    s.Value = saveId;
                    foreach (PersonDelta d in deltas)
                    {
                        p.Value = d.id; bt.Value = d.birthTile; bi.Value = d.birthIndex; h.Value = d.homeTile; f.Value = (int)d.flags;
                        cmd.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }
        }

        /// <summary>The effective overlay of a save: walk its ancestry, nearest record per person wins,
        /// tombstones drop the person. Empty for an unknown save.</summary>
        public PersonDelta[] Resolve(string saveId)
        {
            var result = new List<PersonDelta>();
            if (string.IsNullOrEmpty(saveId)) return result.ToArray();
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = @"
                    WITH RECURSIVE chain(save_id, depth) AS (
                        SELECT $id, 0
                        UNION ALL
                        SELECT c.parent_id, chain.depth + 1 FROM commits c JOIN chain ON c.save_id = chain.save_id
                        WHERE c.parent_id IS NOT NULL AND chain.depth < 100000)
                    SELECT d.person_id, d.birth_tile, d.birth_index, d.home_tile, d.flags
                    FROM deltas d JOIN chain ON d.save_id = chain.save_id
                    ORDER BY d.person_id, chain.depth";
                cmd.Parameters.AddWithValue("$id", saveId);
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    long last = 0; bool first = true;
                    while (r.Read())
                    {
                        long id = r.GetInt64(0);
                        if (!first && id == last) continue;   // an ancestor's record for a person already decided
                        first = false; last = id;
                        int flags = r.GetInt32(4);
                        if ((flags & FlagCleared) != 0) continue;
                        var d = new PersonDelta { id = id, birthTile = r.GetInt32(1), birthIndex = r.GetInt32(2), homeTile = r.GetInt32(3), flags = (byte)flags };
                        if (!d.IsIdentity) result.Add(d);
                    }
                }
            }
            return result.ToArray();
        }

        // ---------------------------------------------------------------- cleanup (#19)

        /// <summary>Tell the store which save files exist right now. Sealed commits whose file is gone get a
        /// missing-since stamp (once); commits whose file reappeared get it cleared.</summary>
        public void ObserveFiles(ICollection<string> existingFileNames, DateTime now)
        {
            var present = new HashSet<string>(existingFileNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            using (SqliteTransaction tx = c.BeginTransaction())
            {
                foreach (CommitInfo ci in All())
                {
                    if (!ci.sealed_ || ci.fileName == null) continue;
                    bool here = present.Contains(ci.fileName);
                    if (!here && ci.missingSince == null) Stamp(tx, ci.saveId, now);
                    else if (here && ci.missingSince != null) Stamp(tx, ci.saveId, null);
                }
                tx.Commit();
            }
        }

        /// <summary>The player deleted these files in the game's dialog: their commits are gone for good.</summary>
        public int MarkDeleted(IEnumerable<string> fileNames)
        {
            int n = 0;
            using (SqliteTransaction tx = c.BeginTransaction())
            {
                foreach (string f in fileNames)
                {
                    using (SqliteCommand cmd = c.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "UPDATE commits SET superseded = 1, file_name = NULL WHERE file_name = $f";
                        cmd.Parameters.AddWithValue("$f", f);
                        n += cmd.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }
            return n;
        }

        /// <summary>
        /// Drop every commit nothing can ever load again: not the working commit, not backed by a live file,
        /// superseded or missing for longer than <paramref name="grace"/>, and with no children. Repeats until
        /// nothing more qualifies, so a dead chain unwinds from its tip. Returns the number dropped.
        /// </summary>
        public int Collect(string workingId, DateTime now, TimeSpan grace)
        {
            int dropped = 0;
            while (true)
            {
                var doomed = new List<string>();
                foreach (CommitInfo ci in All())
                {
                    if (ci.saveId == workingId) continue;
                    if (HasChildren(ci.saveId)) continue;
                    bool dead = ci.superseded
                        || (!ci.sealed_ && ci.fileName == null)                       // an abandoned working commit
                        || (ci.missingSince != null && now - ci.missingSince.Value >= grace);
                    if (dead) doomed.Add(ci.saveId);
                }
                if (doomed.Count == 0) return dropped;
                using (SqliteTransaction tx = c.BeginTransaction())
                {
                    foreach (string id in doomed)
                    {
                        using (SqliteCommand cmd = c.CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText = "DELETE FROM deltas WHERE save_id = $id; DELETE FROM commits WHERE save_id = $id";
                            cmd.Parameters.AddWithValue("$id", id);
                            cmd.ExecuteNonQuery();
                        }
                        dropped++;
                    }
                    tx.Commit();
                }
            }
        }

        private bool HasChildren(string saveId)
        {
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT 1 FROM commits WHERE parent_id = $id LIMIT 1";
                cmd.Parameters.AddWithValue("$id", saveId);
                return cmd.ExecuteScalar() != null;
            }
        }

        private void Stamp(SqliteTransaction tx, string saveId, DateTime? when)
        {
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE commits SET missing_since = $m WHERE save_id = $id";
                cmd.Parameters.AddWithValue("$m", when.HasValue ? (object)when.Value.ToString("o") : DBNull.Value);
                cmd.Parameters.AddWithValue("$id", saveId);
                cmd.ExecuteNonQuery();
            }
        }

        private static string Now() => DateTime.UtcNow.ToString("o");
    }
}
