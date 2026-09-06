using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace RegionsAndSocieties.PersistentWorld.Db
{
    /// <summary>
    /// Writes and reads the history tables. Every row is written under a save id (the working commit), so a
    /// save's history is the union along its lineage and a collected commit takes its rows with it. Appends
    /// are idempotent per key (an upsert), so a re-run of a demographic year overwrites rather than doubles.
    /// Pure over an open connection.
    /// </summary>
    public sealed class HistoryStore
    {
        private readonly SqliteConnection c;
        public HistoryStore(SqliteConnection connection) { c = connection; }

        // ---------------------------------------------------------------- region / cohort years

        public void WriteRegionYear(string saveId, in RegionYear r)
        {
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = @"INSERT OR REPLACE INTO region_years(save_id, year, tick, region, pop, carrying_capacity, food_capacity, area_km2, density, food_self_suff,
                    wealth, urbanisation, employment, health, crime, contentment, substance_use, housing, dependency, dev_level, sanitation, pollution, conflict, roads, rigidity,
                    slavery_stance, tolerance, natalism, balance, inequality, drug_burden_agg, migration_agg, birth_agg, growth,
                    sec_agriculture, sec_extraction, sec_manufacturing, sec_services, sec_military, sec_public)
                    VALUES ($s, $y, $t, $r, $pop, $k, $food, $area, $dens, $ss, $w, $u, $e, $h, $cr, $co, $su, $ho, $dep, $dl, $sa, $po, $cf, $rd, $ri, $sl, $to, $na, $ba, $in, $db, $mi, $bi, $gr,
                    $s1, $s2, $s3, $s4, $s5, $s6)";
                P(cmd, "$s", saveId); P(cmd, "$y", r.year); P(cmd, "$t", r.tick); P(cmd, "$r", r.region);
                P(cmd, "$pop", r.pop); P(cmd, "$k", r.carryingCapacity); P(cmd, "$food", r.foodCapacity); P(cmd, "$area", r.areaKm2); P(cmd, "$dens", r.density); P(cmd, "$ss", r.foodSelfSuff);
                P(cmd, "$w", r.wealth); P(cmd, "$u", r.urbanisation); P(cmd, "$e", r.employment); P(cmd, "$h", r.health); P(cmd, "$cr", r.crime); P(cmd, "$co", r.contentment);
                P(cmd, "$su", r.substanceUse); P(cmd, "$ho", r.housing); P(cmd, "$dep", r.dependency); P(cmd, "$dl", r.devLevel); P(cmd, "$sa", r.sanitation); P(cmd, "$po", r.pollution);
                P(cmd, "$cf", r.conflict); P(cmd, "$rd", r.roads); P(cmd, "$ri", r.rigidity); P(cmd, "$sl", r.slaveryStance); P(cmd, "$to", r.tolerance); P(cmd, "$na", r.natalism);
                P(cmd, "$ba", r.balance); P(cmd, "$in", r.inequality); P(cmd, "$db", r.drugBurdenAgg); P(cmd, "$mi", r.migrationAgg); P(cmd, "$bi", r.birthAgg); P(cmd, "$gr", r.growth);
                P(cmd, "$s1", r.secAgriculture); P(cmd, "$s2", r.secExtraction); P(cmd, "$s3", r.secManufacturing); P(cmd, "$s4", r.secServices); P(cmd, "$s5", r.secMilitary); P(cmd, "$s6", r.secPublic);
                cmd.ExecuteNonQuery();
            }
        }

        public void WriteCohortYear(string saveId, in CohortYear x)
        {
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = @"INSERT OR REPLACE INTO cohort_years(save_id, year, tick, region, cohort, pop, share, standing, base_preference, drug_burden, fertility, birth_rate, birth_mult,
                    migration_net, fight, flight, wealth, edu_index, slave_share, income, cost_living, net_daily, assets, income_source,
                    edu_illiterate, edu_primary, edu_secondary, edu_undergrad, edu_postgrad, strata_elite, strata_middle, strata_underclass,
                    ses_subsistence, ses_modest, ses_prosperous, ses_affluent, female_frac, gender_diverse, age_child, age_working, age_elder, dependency,
                    employment, healthcare, housing, contentment, substance_use, crime, mortality,
                    hz_old_age, hz_malnutrition, hz_disease, hz_violence, hz_war, hz_addiction, hz_xenophobia, life_expectancy, infant_mortality, leading_cause)
                    VALUES ($s, $y, $t, $r, $c, $pop, $sh, $st, $bp, $db, $fe, $br, $bm, $mn, $fi, $fl, $w, $ei, $ss, $inc, $cl, $nd, $as, $is,
                    $e1, $e2, $e3, $e4, $e5, $st1, $st2, $st3, $se1, $se2, $se3, $se4, $ff, $gd, $ac, $aw, $ae, $dep,
                    $emp, $hc, $ho, $co, $su, $cr, $mo, $h1, $h2, $h3, $h4, $h5, $h6, $h7, $le, $im, $lc)";
                P(cmd, "$s", saveId); P(cmd, "$y", x.year); P(cmd, "$t", x.tick); P(cmd, "$r", x.region); P(cmd, "$c", x.cohort);
                P(cmd, "$pop", x.pop); P(cmd, "$sh", x.share); P(cmd, "$st", x.standing); P(cmd, "$bp", x.basePreference); P(cmd, "$db", x.drugBurden); P(cmd, "$fe", x.fertility);
                P(cmd, "$br", x.birthRate); P(cmd, "$bm", x.birthMult); P(cmd, "$mn", x.migrationNet); P(cmd, "$fi", x.fight); P(cmd, "$fl", x.flight);
                P(cmd, "$w", x.wealth); P(cmd, "$ei", x.eduIndex); P(cmd, "$ss", x.slaveShare); P(cmd, "$inc", x.income); P(cmd, "$cl", x.costLiving); P(cmd, "$nd", x.netDaily); P(cmd, "$as", x.assets);
                cmd.Parameters.AddWithValue("$is", (object)x.incomeSource ?? DBNull.Value);
                P(cmd, "$e1", x.eduIlliterate); P(cmd, "$e2", x.eduPrimary); P(cmd, "$e3", x.eduSecondary); P(cmd, "$e4", x.eduUndergrad); P(cmd, "$e5", x.eduPostgrad);
                P(cmd, "$st1", x.strataElite); P(cmd, "$st2", x.strataMiddle); P(cmd, "$st3", x.strataUnderclass);
                P(cmd, "$se1", x.sesSubsistence); P(cmd, "$se2", x.sesModest); P(cmd, "$se3", x.sesProsperous); P(cmd, "$se4", x.sesAffluent);
                P(cmd, "$ff", x.femaleFrac); P(cmd, "$gd", x.genderDiverse); P(cmd, "$ac", x.ageChild); P(cmd, "$aw", x.ageWorking); P(cmd, "$ae", x.ageElder); P(cmd, "$dep", x.dependency);
                P(cmd, "$emp", x.employment); P(cmd, "$hc", x.healthcare); P(cmd, "$ho", x.housing); P(cmd, "$co", x.contentment); P(cmd, "$su", x.substanceUse); P(cmd, "$cr", x.crime); P(cmd, "$mo", x.mortality);
                P(cmd, "$h1", x.hzOldAge); P(cmd, "$h2", x.hzMalnutrition); P(cmd, "$h3", x.hzDisease); P(cmd, "$h4", x.hzViolence); P(cmd, "$h5", x.hzWar); P(cmd, "$h6", x.hzAddiction); P(cmd, "$h7", x.hzXenophobia);
                P(cmd, "$le", x.lifeExpectancy); P(cmd, "$im", x.infantMortality);
                cmd.Parameters.AddWithValue("$lc", (object)x.leadingCause ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        public void WriteLevels(string saveId, IEnumerable<FactorLevel> levels)
        {
            using (SqliteTransaction tx = c.BeginTransaction())
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR REPLACE INTO factor_levels(save_id, year, region, cohort, factor, tier, current, target, moving) VALUES ($s, $y, $r, $c, $f, $ti, $cu, $ta, $m)";
                SqliteParameter s = cmd.Parameters.Add("$s", SqliteType.Text), y = cmd.Parameters.Add("$y", SqliteType.Integer), r = cmd.Parameters.Add("$r", SqliteType.Integer),
                    co = cmd.Parameters.Add("$c", SqliteType.Integer), f = cmd.Parameters.Add("$f", SqliteType.Text), ti = cmd.Parameters.Add("$ti", SqliteType.Text),
                    cu = cmd.Parameters.Add("$cu", SqliteType.Real), ta = cmd.Parameters.Add("$ta", SqliteType.Real), m = cmd.Parameters.Add("$m", SqliteType.Integer);
                s.Value = saveId;
                foreach (FactorLevel l in levels)
                {
                    y.Value = l.year; r.Value = l.region; co.Value = l.cohort; f.Value = l.factor ?? ""; ti.Value = l.tier ?? ""; cu.Value = l.current; ta.Value = l.target; m.Value = l.moving ? 1 : 0;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }

        public void WriteFlow(string saveId, in FlowRow f)
        {
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = @"INSERT OR REPLACE INTO flows(save_id, year, region, cohort, births_gross, births_effective, deaths, d_old_age, d_malnutrition, d_disease, d_violence, d_war, d_addiction, d_xenophobia,
                    immigration, emigration, combat_losses, enslaved, freed) VALUES ($s, $y, $r, $c, $bg, $be, $d, $d1, $d2, $d3, $d4, $d5, $d6, $d7, $im, $em, $cl, $en, $fr)";
                P(cmd, "$s", saveId); P(cmd, "$y", f.year); P(cmd, "$r", f.region); P(cmd, "$c", f.cohort);
                P(cmd, "$bg", f.birthsGross); P(cmd, "$be", f.birthsEffective); P(cmd, "$d", f.deaths);
                P(cmd, "$d1", f.dOldAge); P(cmd, "$d2", f.dMalnutrition); P(cmd, "$d3", f.dDisease); P(cmd, "$d4", f.dViolence); P(cmd, "$d5", f.dWar); P(cmd, "$d6", f.dAddiction); P(cmd, "$d7", f.dXenophobia);
                P(cmd, "$im", f.immigration); P(cmd, "$em", f.emigration); P(cmd, "$cl", f.combatLosses); P(cmd, "$en", f.enslaved); P(cmd, "$fr", f.freed);
                cmd.ExecuteNonQuery();
            }
        }

        public void WriteBirths(string saveId, IEnumerable<BirthAssignment> births)
        {
            using (SqliteTransaction tx = c.BeginTransaction())
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR REPLACE INTO births_assigned(save_id, year, region, mother_cohort, father_cohort, child_cohort, count) VALUES ($s, $y, $r, $m, $f, $ch, $n)";
                SqliteParameter s = cmd.Parameters.Add("$s", SqliteType.Text), y = cmd.Parameters.Add("$y", SqliteType.Integer), r = cmd.Parameters.Add("$r", SqliteType.Integer),
                    m = cmd.Parameters.Add("$m", SqliteType.Integer), f = cmd.Parameters.Add("$f", SqliteType.Integer), ch = cmd.Parameters.Add("$ch", SqliteType.Integer), n = cmd.Parameters.Add("$n", SqliteType.Real);
                s.Value = saveId;
                foreach (BirthAssignment b in births) { y.Value = b.year; r.Value = b.region; m.Value = b.motherCohort; f.Value = b.fatherCohort; ch.Value = b.childCohort; n.Value = b.count; cmd.ExecuteNonQuery(); }
                tx.Commit();
            }
        }

        public void WriteSettlement(string saveId, in SettlementYear s)
        {
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = @"INSERT OR REPLACE INTO settlement_years(save_id, year, tick, region, tile, kind, headcount, wealth, education, sector_output, ideo, xenotypes)
                    VALUES ($s, $y, $t, $r, $ti, $k, $h, $w, $e, $so, $i, $x)";
                P(cmd, "$s", saveId); P(cmd, "$y", s.year); P(cmd, "$t", s.tick); P(cmd, "$r", s.region); P(cmd, "$ti", s.tile);
                cmd.Parameters.AddWithValue("$k", s.kind ?? "npc"); P(cmd, "$h", s.headcount); P(cmd, "$w", s.wealth); P(cmd, "$e", s.education);
                cmd.Parameters.AddWithValue("$so", (object)s.sectorOutput ?? DBNull.Value); P(cmd, "$i", s.ideo);
                cmd.Parameters.AddWithValue("$x", (object)s.xenotypes ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        // ---------------------------------------------------------------- person events

        /// <summary>Append one event. Returns its sequence number.</summary>
        public long Append(string saveId, in PersonEvent e)
        {
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO person_events(save_id, year, tick, person, kind, from_value, to_value, cause, region) VALUES ($s, $y, $t, $p, $k, $f, $to, $ca, $r); SELECT last_insert_rowid()";
                P(cmd, "$s", saveId); P(cmd, "$y", e.year); P(cmd, "$t", e.tick); cmd.Parameters.AddWithValue("$p", e.person);
                cmd.Parameters.AddWithValue("$k", e.kind ?? ""); cmd.Parameters.AddWithValue("$f", e.fromValue); cmd.Parameters.AddWithValue("$to", e.toValue);
                cmd.Parameters.AddWithValue("$ca", (object)e.cause ?? DBNull.Value); P(cmd, "$r", e.region);
                return (long)cmd.ExecuteScalar();
            }
        }

        /// <summary>Append many events in one transaction.</summary>
        public int AppendAll(string saveId, IEnumerable<PersonEvent> events)
        {
            int n = 0;
            using (SqliteTransaction tx = c.BeginTransaction())
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO person_events(save_id, year, tick, person, kind, from_value, to_value, cause, region) VALUES ($s, $y, $t, $p, $k, $f, $to, $ca, $r)";
                SqliteParameter s = cmd.Parameters.Add("$s", SqliteType.Text), y = cmd.Parameters.Add("$y", SqliteType.Integer), t = cmd.Parameters.Add("$t", SqliteType.Integer),
                    p = cmd.Parameters.Add("$p", SqliteType.Integer), k = cmd.Parameters.Add("$k", SqliteType.Text), f = cmd.Parameters.Add("$f", SqliteType.Integer),
                    to = cmd.Parameters.Add("$to", SqliteType.Integer), ca = cmd.Parameters.Add("$ca", SqliteType.Text), r = cmd.Parameters.Add("$r", SqliteType.Integer);
                s.Value = saveId;
                foreach (PersonEvent e in events)
                {
                    y.Value = e.year; t.Value = e.tick; p.Value = e.person; k.Value = e.kind ?? ""; f.Value = e.fromValue; to.Value = e.toValue;
                    ca.Value = (object)e.cause ?? DBNull.Value; r.Value = e.region;
                    cmd.ExecuteNonQuery(); n++;
                }
                tx.Commit();
            }
            return n;
        }

        /// <summary>A person's history along a save's lineage, oldest first. Events in commits off the
        /// lineage (other branches) are not part of this save's history and are excluded.</summary>
        public List<PersonEvent> EventsOf(long person, string saveId)
        {
            var list = new List<PersonEvent>();
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = @"
                    WITH RECURSIVE chain(save_id, depth) AS (
                        SELECT $id, 0
                        UNION ALL
                        SELECT c.parent_id, chain.depth + 1 FROM commits c JOIN chain ON c.save_id = chain.save_id
                        WHERE c.parent_id IS NOT NULL AND chain.depth < 100000)
                    SELECT e.seq, e.year, e.tick, e.person, e.kind, e.from_value, e.to_value, e.cause, e.region
                    FROM person_events e JOIN chain ON e.save_id = chain.save_id
                    WHERE e.person = $p ORDER BY e.seq";
                cmd.Parameters.AddWithValue("$id", saveId ?? "");
                cmd.Parameters.AddWithValue("$p", person);
                using (SqliteDataReader r = cmd.ExecuteReader())
                    while (r.Read())
                        list.Add(new PersonEvent
                        {
                            seq = r.GetInt64(0), year = r.GetInt32(1), tick = r.GetInt32(2), person = r.GetInt64(3), kind = r.GetString(4),
                            fromValue = r.GetInt64(5), toValue = r.GetInt64(6), cause = r.IsDBNull(7) ? null : r.GetString(7), region = r.GetInt32(8),
                        });
            }
            return list;
        }

        /// <summary>How many events a save's lineage holds, by kind.</summary>
        public Dictionary<string, long> EventCounts(string saveId)
        {
            var d = new Dictionary<string, long>();
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = @"
                    WITH RECURSIVE chain(save_id, depth) AS (
                        SELECT $id, 0
                        UNION ALL
                        SELECT c.parent_id, chain.depth + 1 FROM commits c JOIN chain ON c.save_id = chain.save_id
                        WHERE c.parent_id IS NOT NULL AND chain.depth < 100000)
                    SELECT e.kind, COUNT(*) FROM person_events e JOIN chain ON e.save_id = chain.save_id GROUP BY e.kind";
                cmd.Parameters.AddWithValue("$id", saveId ?? "");
                using (SqliteDataReader r = cmd.ExecuteReader()) while (r.Read()) d[r.GetString(0)] = r.GetInt64(1);
            }
            return d;
        }

        /// <summary>The latest demographic year recorded for a region along a lineage, or -1.</summary>
        public int LastYear(string saveId, int region)
        {
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = @"
                    WITH RECURSIVE chain(save_id, depth) AS (
                        SELECT $id, 0
                        UNION ALL
                        SELECT c.parent_id, chain.depth + 1 FROM commits c JOIN chain ON c.save_id = chain.save_id
                        WHERE c.parent_id IS NOT NULL AND chain.depth < 100000)
                    SELECT MAX(y.year) FROM region_years y JOIN chain ON y.save_id = chain.save_id WHERE y.region = $r";
                cmd.Parameters.AddWithValue("$id", saveId ?? "");
                cmd.Parameters.AddWithValue("$r", region);
                object o = cmd.ExecuteScalar();
                return o == null || o is DBNull ? -1 : (int)(long)o;
            }
        }

        /// <summary>Drop every history row written under a save id (used when its commit is collected).</summary>
        public static int DropSave(SqliteConnection c, SqliteTransaction tx, string saveId)
        {
            int n = 0;
            foreach (string table in CensusSchema.HistoryTables)
            {
                using (SqliteCommand cmd = c.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM " + table + " WHERE save_id = $s";
                    cmd.Parameters.AddWithValue("$s", saveId);
                    n += cmd.ExecuteNonQuery();
                }
            }
            return n;
        }

        private static void P(SqliteCommand cmd, string name, int v) => cmd.Parameters.AddWithValue(name, v);
        private static void P(SqliteCommand cmd, string name, float v) => cmd.Parameters.AddWithValue(name, (double)v);
        private static void P(SqliteCommand cmd, string name, string v) => cmd.Parameters.AddWithValue(name, v ?? "");
    }
}
