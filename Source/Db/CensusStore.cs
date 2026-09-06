using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using RegionsAndSocieties.Demographics;
using RegionsAndSocieties.PersistentWorld.Population;

namespace RegionsAndSocieties.PersistentWorld.Db
{
    /// <summary>
    /// The census tables (#20): the latest materialized planet, written whole after every published build
    /// and read back on load so queries have a planet before the first fresh build lands. Also the SQL
    /// door: a read-only query over those tables for consumers and tuning. Pure over an open connection;
    /// the writer is meant for the background thread, over the immutable dataset.
    /// </summary>
    public static class CensusStore
    {
        /// <summary>Replace the census with <paramref name="ds"/> in one transaction.</summary>
        public static void Write(SqliteConnection c, PopulationDataset ds, int tick)
        {
            ds = ds ?? PopulationDataset.Empty;
            using (SqliteTransaction tx = c.BeginTransaction())
            {
                foreach (string t in new[] { "people", "households", "links", "labels", "regions", "tiles", "builds" })
                    CensusSchema.Exec(c, "DELETE FROM " + t, tx);

                using (SqliteCommand cmd = c.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO builds(serial, tick, seed, people, households, linked, moved, millis, created) VALUES ($s, $t, $seed, $p, $h, $l, $m, $ms, $now)";
                    cmd.Parameters.AddWithValue("$s", ds.buildSerial);
                    cmd.Parameters.AddWithValue("$t", tick);
                    cmd.Parameters.AddWithValue("$seed", ds.snapshot.worldSeed);
                    cmd.Parameters.AddWithValue("$p", ds.Count);
                    cmd.Parameters.AddWithValue("$h", ds.HouseholdCount);
                    cmd.Parameters.AddWithValue("$l", ds.LinkedCount);
                    cmd.Parameters.AddWithValue("$m", ds.MovedCount);
                    cmd.Parameters.AddWithValue("$ms", ds.buildMillis);
                    cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
                    cmd.ExecuteNonQuery();
                }

                using (SqliteCommand cmd = c.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO regions(slot, province) VALUES ($s, $p)";
                    SqliteParameter s = cmd.Parameters.Add("$s", SqliteType.Integer), p = cmd.Parameters.Add("$p", SqliteType.Integer);
                    for (int r = 0; r < ds.RegionIds.Length; r++) { s.Value = r; p.Value = ds.RegionIds[r]; cmd.ExecuteNonQuery(); }
                }
                using (SqliteCommand cmd = c.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO tiles(tile, region, residents) VALUES ($t, $r, $n)";
                    SqliteParameter t = cmd.Parameters.Add("$t", SqliteType.Integer), r = cmd.Parameters.Add("$r", SqliteType.Integer), n = cmd.Parameters.Add("$n", SqliteType.Integer);
                    foreach (TileSlot slot in ds.tiles) { t.Value = slot.tile; r.Value = slot.region; n.Value = slot.population; cmd.ExecuteNonQuery(); }
                }
                using (SqliteCommand cmd = c.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO labels(kind, key, label) VALUES ($k, $i, $l)";
                    SqliteParameter k = cmd.Parameters.Add("$k", SqliteType.Text), i = cmd.Parameters.Add("$i", SqliteType.Integer), l = cmd.Parameters.Add("$l", SqliteType.Text);
                    WriteLabels(cmd, k, i, l, "race", ds.snapshot.raceLabels);
                    WriteLabels(cmd, k, i, l, "faction", ds.snapshot.factionLabels);
                    WriteLabels(cmd, k, i, l, "ideo", ds.snapshot.ideoLabels);
                }

                using (SqliteCommand cmd = c.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT INTO people(id, birth_tile, birth_index, home, region, female, age, age_bucket, education, class, wealth, work, sector, race, faction, ideo, pawn, household, household_size)
                        VALUES ($id, $bt, $bi, $home, $region, $f, $age, $ab, $edu, $cls, $w, $work, $sec, $race, $fac, $ideo, $pawn, $hh, $hs)";
                    var ps = new SqliteParameter[19];
                    string[] names = { "$id", "$bt", "$bi", "$home", "$region", "$f", "$age", "$ab", "$edu", "$cls", "$w", "$work", "$sec", "$race", "$fac", "$ideo", "$pawn", "$hh", "$hs" };
                    for (int i = 0; i < ps.Length; i++) ps[i] = cmd.Parameters.Add(names[i], SqliteType.Integer);
                    for (int i = 0; i < ds.people.Length; i++)
                    {
                        ref readonly Individual p = ref ds.people[i];
                        ps[0].Value = p.id; ps[1].Value = p.birthTile; ps[2].Value = p.birthIndex; ps[3].Value = p.tile; ps[4].Value = ds.RegionOf(in p);
                        ps[5].Value = p.female ? 1 : 0; ps[6].Value = p.age; ps[7].Value = (int)p.ageBucket; ps[8].Value = (int)p.education; ps[9].Value = (int)p.ses;
                        ps[10].Value = p.wealth; ps[11].Value = (int)p.work; ps[12].Value = (int)p.sector; ps[13].Value = p.raceKey; ps[14].Value = p.factionKey;
                        ps[15].Value = p.ideoKey; ps[16].Value = p.pawnId; ps[17].Value = p.household; ps[18].Value = p.householdSize;
                        cmd.ExecuteNonQuery();
                    }
                }

                using (SqliteCommand cmd = c.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO households(tile, idx, size, head) VALUES ($t, $i, $s, $h)";
                    SqliteParameter t = cmd.Parameters.Add("$t", SqliteType.Integer), i = cmd.Parameters.Add("$i", SqliteType.Integer), s = cmd.Parameters.Add("$s", SqliteType.Integer), h = cmd.Parameters.Add("$h", SqliteType.Integer);
                    foreach (TileSlot slot in ds.tiles)
                    {
                        int n = ds.HouseholdsOnTile(slot.tile);
                        for (int k = 0; k < n; k++)
                        {
                            if (!ds.TryHousehold(slot.tile, k, out _, out int size) || !ds.TryHouseholdHead(slot.tile, k, out Individual head)) continue;
                            t.Value = slot.tile; i.Value = k; s.Value = size; h.Value = head.id;
                            cmd.ExecuteNonQuery();
                        }
                    }
                }

                using (SqliteCommand cmd = c.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO links(pawn, person, birth_tile, birth_index) VALUES ($p, $id, $bt, $bi)";
                    SqliteParameter p = cmd.Parameters.Add("$p", SqliteType.Integer), id = cmd.Parameters.Add("$id", SqliteType.Integer), bt = cmd.Parameters.Add("$bt", SqliteType.Integer), bi = cmd.Parameters.Add("$bi", SqliteType.Integer);
                    foreach (LinkedPerson l in ds.snapshot.linked) { p.Value = l.pawnId; id.Value = l.id; bt.Value = l.birthTile; bi.Value = l.birthIndex; cmd.ExecuteNonQuery(); }
                }

                tx.Commit();
            }
        }

        private static void WriteLabels(SqliteCommand cmd, SqliteParameter k, SqliteParameter i, SqliteParameter l, string kind, string[] labels)
        {
            for (int n = 0; n < labels.Length; n++) { k.Value = kind; i.Value = n; l.Value = labels[n] ?? ""; cmd.ExecuteNonQuery(); }
        }

        /// <summary>The stored census as a dataset, or null when there is none or it belongs to another seed.
        /// The rebuilt snapshot carries regions and labels but no births or profiles — a restored result.</summary>
        public static PopulationDataset Read(SqliteConnection c, int expectedSeed)
        {
            int seed; long serial, millis;
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT seed, serial, millis FROM builds ORDER BY serial DESC LIMIT 1";
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return null;
                    seed = r.GetInt32(0); serial = r.GetInt64(1); millis = r.GetInt64(2);
                }
            }
            if (seed != expectedSeed) return null;

            int[] regionIds = ReadInts(c, "SELECT province FROM regions ORDER BY slot");
            string[] races = ReadLabels(c, "race"), factions = ReadLabels(c, "faction"), ideos = ReadLabels(c, "ideo");

            var tiles = new List<TileSlot>();
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT tile, region, residents FROM tiles ORDER BY tile";
                using (SqliteDataReader r = cmd.ExecuteReader())
                    while (r.Read()) tiles.Add(new TileSlot { tile = r.GetInt32(0), region = r.GetInt32(1), population = r.GetInt32(2) });
            }

            var people = new List<Individual>();
            int linked = 0;
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT id, birth_tile, birth_index, home, female, age, age_bucket, education, class, wealth, work, sector, race, faction, ideo, pawn, household, household_size FROM people ORDER BY home, birth_tile, birth_index";
                using (SqliteDataReader r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        var p = new Individual
                        {
                            id = r.GetInt64(0), birthTile = r.GetInt32(1), birthIndex = r.GetInt32(2), tile = r.GetInt32(3),
                            female = r.GetInt32(4) != 0, age = r.GetInt32(5), ageBucket = (AgeBucket)r.GetInt32(6), education = (EducationTier)r.GetInt32(7),
                            ses = (SesTier)r.GetInt32(8), wealth = r.GetInt32(9), work = (WorkStatus)r.GetInt32(10), sector = (OccupationSector)r.GetInt32(11),
                            raceKey = r.GetInt32(12), factionKey = r.GetInt32(13), ideoKey = r.GetInt32(14), pawnId = r.GetInt32(15),
                            household = r.GetInt32(16), householdSize = r.GetInt32(17),
                        };
                        if (p.id != PersonId.Make(seed, p.birthTile, p.birthIndex)) return null;
                        if (p.pawnId != 0) linked++;
                        people.Add(p);
                    }
            }

            // Rows must fill the tile table exactly, home tile by home tile.
            var tileStart = new int[tiles.Count + 1];
            for (int i = 0; i < tiles.Count; i++) tileStart[i + 1] = tileStart[i] + Math.Max(0, tiles[i].population);
            if (tileStart[tiles.Count] != people.Count) return null;
            for (int t = 0; t < tiles.Count; t++)
                for (int i = tileStart[t]; i < tileStart[t + 1]; i++)
                    if (people[i].tile != tiles[t].tile) return null;

            var snapshot = new PopulationSnapshot(seed, null, regionIds, null, races, factions, ideos, -1);
            var arr = tiles.ToArray();
            HouseholdTable households = HouseholdTable.Build(seed, arr);
            for (int t = 0; t < arr.Length; t++)
                for (int i = tileStart[t]; i < tileStart[t + 1]; i++)
                    if (households.HouseholdOf(t, i - tileStart[t]) != people[i].household) return null;

            return new PopulationDataset(snapshot, people.ToArray(), arr, tileStart, millis, linked, null, households);
        }

        /// <summary>The SQL door: run one read-only statement and return its rows as column → value maps,
        /// at most <paramref name="limit"/> of them (0 = no cap). Writes are refused by the connection.</summary>
        public static List<Dictionary<string, object>> Query(SqliteConnection c, string sql, int limit = 0)
        {
            var rows = new List<Dictionary<string, object>>();
            CensusSchema.Exec(c, "PRAGMA query_only = 1");
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = sql;
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        if (limit > 0 && rows.Count >= limit) break;
                        var row = new Dictionary<string, object>(r.FieldCount);
                        for (int i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
                        rows.Add(row);
                    }
                }
            }
            return rows;
        }

        public static long Count(SqliteConnection c, string table) => CensusSchema.Scalar(c, "SELECT COUNT(*) FROM " + table);

        private static int[] ReadInts(SqliteConnection c, string sql)
        {
            var list = new List<int>();
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = sql;
                using (SqliteDataReader r = cmd.ExecuteReader()) while (r.Read()) list.Add(r.GetInt32(0));
            }
            return list.ToArray();
        }

        private static string[] ReadLabels(SqliteConnection c, string kind)
        {
            var list = new List<string>();
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT label FROM labels WHERE kind = $k ORDER BY key";
                cmd.Parameters.AddWithValue("$k", kind);
                using (SqliteDataReader r = cmd.ExecuteReader()) while (r.Read()) list.Add(r.GetString(0));
            }
            return list.ToArray();
        }
    }
}
