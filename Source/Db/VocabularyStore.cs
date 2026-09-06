using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace RegionsAndSocieties.PersistentWorld.Db
{
    /// <summary>
    /// The model's vocabulary as data: factors and their tiers and edges, the sector tree, the xenotype
    /// cohorts. Seeded with the built-in vocabulary from Core's locked design (the factor and sector
    /// templates) so the tables are never empty, then overwritten by a sync from Core's Defs once Core ships
    /// them — a patch's factor or sector appears here without a schema change. Pure over an open connection.
    /// </summary>
    public sealed class VocabularyStore
    {
        private readonly SqliteConnection c;
        public VocabularyStore(SqliteConnection connection) { c = connection; }

        public void UpsertFactors(IEnumerable<FactorRow> factors)
        {
            using (SqliteTransaction tx = c.BeginTransaction())
            {
                foreach (FactorRow f in factors)
                {
                    using (SqliteCommand cmd = c.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = @"INSERT INTO factors(def_name, label, category, kind, tiers_from, velocity, inertia, deadband, release_band, max_step, per_cohort, source)
                            VALUES ($d, $l, $c, $k, $tf, $v, $i, $db, $rb, $ms, $pc, $s)
                            ON CONFLICT(def_name) DO UPDATE SET label = excluded.label, category = excluded.category, kind = excluded.kind, tiers_from = excluded.tiers_from,
                            velocity = excluded.velocity, inertia = excluded.inertia, deadband = excluded.deadband, release_band = excluded.release_band, max_step = excluded.max_step,
                            per_cohort = excluded.per_cohort, source = excluded.source";
                        cmd.Parameters.AddWithValue("$d", f.defName); cmd.Parameters.AddWithValue("$l", f.label ?? ""); cmd.Parameters.AddWithValue("$c", f.category ?? "Mutable");
                        cmd.Parameters.AddWithValue("$k", f.kind ?? "Scalar"); cmd.Parameters.AddWithValue("$tf", (object)f.tiersFrom ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("$v", (double)f.velocity); cmd.Parameters.AddWithValue("$i", (double)f.inertia); cmd.Parameters.AddWithValue("$db", (double)f.deadband);
                        cmd.Parameters.AddWithValue("$rb", (double)f.releaseBand); cmd.Parameters.AddWithValue("$ms", (double)f.maxStep); cmd.Parameters.AddWithValue("$pc", f.perCohort ? 1 : 0);
                        cmd.Parameters.AddWithValue("$s", f.source ?? "builtin");
                        cmd.ExecuteNonQuery();
                    }
                    using (SqliteCommand cmd = c.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "DELETE FROM factor_tiers WHERE factor = $d";
                        cmd.Parameters.AddWithValue("$d", f.defName);
                        cmd.ExecuteNonQuery();
                    }
                    if (f.tiers != null)
                        for (int i = 0; i < f.tiers.Length; i++)
                            using (SqliteCommand cmd = c.CreateCommand())
                            {
                                cmd.Transaction = tx;
                                cmd.CommandText = "INSERT INTO factor_tiers(factor, ordinal, tier) VALUES ($d, $o, $t)";
                                cmd.Parameters.AddWithValue("$d", f.defName); cmd.Parameters.AddWithValue("$o", i); cmd.Parameters.AddWithValue("$t", f.tiers[i]);
                                cmd.ExecuteNonQuery();
                            }
                }
                tx.Commit();
            }
        }

        public void UpsertEdges(IEnumerable<EdgeRow> edges)
        {
            using (SqliteTransaction tx = c.BeginTransaction())
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT OR REPLACE INTO factor_edges(factor, source, source_tier, target_tier, weight, curve, threshold, lag_years, mode, spill)
                    VALUES ($f, $s, $st, $tt, $w, $c, $th, $l, $m, $sp)";
                SqliteParameter f = cmd.Parameters.Add("$f", SqliteType.Text), s = cmd.Parameters.Add("$s", SqliteType.Text), st = cmd.Parameters.Add("$st", SqliteType.Text),
                    tt = cmd.Parameters.Add("$tt", SqliteType.Text), w = cmd.Parameters.Add("$w", SqliteType.Real), cu = cmd.Parameters.Add("$c", SqliteType.Text),
                    th = cmd.Parameters.Add("$th", SqliteType.Real), l = cmd.Parameters.Add("$l", SqliteType.Integer), m = cmd.Parameters.Add("$m", SqliteType.Text), sp = cmd.Parameters.Add("$sp", SqliteType.Text);
                foreach (EdgeRow e in edges)
                {
                    f.Value = e.factor; s.Value = e.source; st.Value = e.sourceTier ?? ""; tt.Value = e.targetTier ?? ""; w.Value = (double)e.weight;
                    cu.Value = e.curve ?? "Linear"; th.Value = e.threshold.HasValue ? (object)(double)e.threshold.Value : DBNull.Value; l.Value = e.lagYears;
                    m.Value = e.mode ?? "Stress"; sp.Value = (object)e.spill ?? DBNull.Value;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }

        public void UpsertSectors(IEnumerable<SectorRow> sectors)
        {
            using (SqliteTransaction tx = c.BeginTransaction())
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO sectors(def_name, parent, label, labor_tier, requires_dlc, source) VALUES ($d, $p, $l, $t, $r, $s)
                    ON CONFLICT(def_name) DO UPDATE SET parent = excluded.parent, label = excluded.label, labor_tier = excluded.labor_tier, requires_dlc = excluded.requires_dlc, source = excluded.source";
                SqliteParameter d = cmd.Parameters.Add("$d", SqliteType.Text), p = cmd.Parameters.Add("$p", SqliteType.Text), l = cmd.Parameters.Add("$l", SqliteType.Text),
                    t = cmd.Parameters.Add("$t", SqliteType.Integer), r = cmd.Parameters.Add("$r", SqliteType.Text), s = cmd.Parameters.Add("$s", SqliteType.Text);
                foreach (SectorRow x in sectors)
                {
                    d.Value = x.defName; p.Value = (object)x.parent ?? DBNull.Value; l.Value = x.label ?? ""; t.Value = x.laborTier; r.Value = (object)x.requiresDlc ?? DBNull.Value; s.Value = x.source ?? "builtin";
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }

        public void UpsertCohortKinds(IEnumerable<CohortKindRow> kinds)
        {
            using (SqliteTransaction tx = c.BeginTransaction())
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT OR REPLACE INTO cohort_kinds(key, def_name, label, inheritable, lifespan, dependency, fragility, fertility) VALUES ($k, $d, $l, $i, $ls, $dp, $fr, $fe)";
                SqliteParameter k = cmd.Parameters.Add("$k", SqliteType.Integer), d = cmd.Parameters.Add("$d", SqliteType.Text), l = cmd.Parameters.Add("$l", SqliteType.Text),
                    i = cmd.Parameters.Add("$i", SqliteType.Integer), ls = cmd.Parameters.Add("$ls", SqliteType.Real), dp = cmd.Parameters.Add("$dp", SqliteType.Real),
                    fr = cmd.Parameters.Add("$fr", SqliteType.Real), fe = cmd.Parameters.Add("$fe", SqliteType.Real);
                foreach (CohortKindRow x in kinds)
                {
                    k.Value = x.key; d.Value = x.defName ?? ""; l.Value = x.label ?? ""; i.Value = x.inheritable ? 1 : 0; ls.Value = (double)x.lifespan; dp.Value = (double)x.dependency; fr.Value = (double)x.fragility; fe.Value = (double)x.fertility;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }

        public List<string> FactorNames(string category = null)
        {
            var list = new List<string>();
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = category == null ? "SELECT def_name FROM factors ORDER BY def_name" : "SELECT def_name FROM factors WHERE category = $c ORDER BY def_name";
                if (category != null) cmd.Parameters.AddWithValue("$c", category);
                using (SqliteDataReader r = cmd.ExecuteReader()) while (r.Read()) list.Add(r.GetString(0));
            }
            return list;
        }

        public string[] TiersOf(string factor)
        {
            var list = new List<string>();
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT tier FROM factor_tiers WHERE factor = $f ORDER BY ordinal";
                cmd.Parameters.AddWithValue("$f", factor);
                using (SqliteDataReader r = cmd.ExecuteReader()) while (r.Read()) list.Add(r.GetString(0));
            }
            return list.ToArray();
        }

        /// <summary>Seed the built-in vocabulary. Idempotent; never overwrites a row synced from a Def.</summary>
        public void SeedBuiltin()
        {
            var factors = new List<FactorRow>();
            foreach (FactorRow f in BuiltinVocabulary.Factors) if (!IsFromDef("factors", f.defName)) factors.Add(f);
            UpsertFactors(factors);
            var sectors = new List<SectorRow>();
            foreach (SectorRow s in BuiltinVocabulary.Sectors) if (!IsFromDef("sectors", s.defName)) sectors.Add(s);
            UpsertSectors(sectors);
        }

        private bool IsFromDef(string table, string defName)
        {
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT 1 FROM " + table + " WHERE def_name = $d AND source = 'def'";
                cmd.Parameters.AddWithValue("$d", defName);
                return cmd.ExecuteScalar() != null;
            }
        }
    }

    /// <summary>
    /// The locked vocabulary from Core's templates (<c>Design/DemographicFactorDefs/DemographicFactors_Core.xml</c>
    /// and <c>Design/EconomicSectorDefs/EconomicSectors_Core.xml</c>): names, categories, kinds, tiers, and the
    /// sector tree. Weights and hysteresis constants are deliberately absent — those are Core's to tune, and
    /// arrive through the Def sync. Pure data, so a suite can assert the tables match it.
    /// </summary>
    public static class BuiltinVocabulary
    {
        private static FactorRow F(string def, string label, string category, string kind, string[] tiers = null, string tiersFrom = null, bool perCohort = true)
            => new FactorRow { defName = def, label = label, category = category, kind = kind, tiers = tiers, tiersFrom = tiersFrom, perCohort = perCohort, source = "builtin" };

        public static readonly FactorRow[] Factors =
        {
            // Stocks — immutable per person; moved only by Flows.
            F("RS_Population", "population", "Stock", "Scalar"),
            F("RS_XenotypeMix", "xenotype mix", "Stock", "Distribution", tiersFrom: "XenotypeDef", perCohort: false),
            F("RS_SexRatio", "sex ratio", "Stock", "Scalar"),
            F("RS_AgeStructure", "age structure", "Stock", "Distribution", new[] { "Child", "WorkingAge", "Elder" }),
            // Flows — the rates that move the stocks.
            F("RS_Ageing", "ageing", "Flow", "Scalar"),
            F("RS_BirthRate", "birth rate", "Flow", "Scalar"),
            F("RS_MortalityRate", "mortality rate", "Flow", "Scalar"),
            F("RS_MigrationNet", "net migration", "Flow", "Scalar"),
            F("RS_CombatLosses", "combat losses", "Flow", "Scalar"),
            // Fast mutable.
            F("RS_EmploymentRate", "employment rate", "Mutable", "Scalar"),
            F("RS_Sector", "economic sector", "Mutable", "Distribution", new[] { "Agriculture", "Extraction", "Manufacturing", "Services", "Military", "Public" }),
            F("RS_Sector_Agriculture", "agriculture sub-sectors", "Mutable", "Distribution", tiersFrom: "EconomicSectorDef"),
            F("RS_Sector_Extraction", "extraction sub-sectors", "Mutable", "Distribution", tiersFrom: "EconomicSectorDef"),
            F("RS_Sector_Manufacturing", "manufacturing sub-sectors", "Mutable", "Distribution", tiersFrom: "EconomicSectorDef"),
            F("RS_Sector_Services", "services sub-sectors", "Mutable", "Distribution", tiersFrom: "EconomicSectorDef"),
            F("RS_Sector_Military", "military sub-sectors", "Mutable", "Distribution", tiersFrom: "EconomicSectorDef"),
            F("RS_Sector_Public", "public sub-sectors", "Mutable", "Distribution", tiersFrom: "EconomicSectorDef"),
            F("RS_Urbanisation", "urbanisation", "Mutable", "Scalar"),
            F("RS_Wealth", "wealth", "Mutable", "Scalar"),
            F("RS_SES", "socioeconomic class", "Mutable", "Distribution", new[] { "Subsistence", "Modest", "Prosperous", "Affluent" }),
            // Slow mutable.
            F("RS_Education", "education", "Mutable", "Distribution", new[] { "Illiterate", "Primary", "Secondary", "Undergrad", "Postgrad" }),
            F("RS_Ideology", "ideology", "Mutable", "Distribution", tiersFrom: "MemeDef"),
            F("RS_Strata", "stratification", "Mutable", "Distribution", new[] { "Elite", "Middle", "Underclass" }),
            // Context — sources only; patches add their own.
            F("RS_BiomeFertility", "biome fertility", "Context", "Scalar", perCohort: false),
            F("RS_XenotypeFertility", "xenotype fertility", "Context", "Scalar"),
            F("RS_FactionCharacter", "faction character", "Context", "Distribution", new[] { "KnowledgeSkew", "WealthMultiplier", "Militarism" }, perCohort: false),
            F("RS_FactionPressure", "faction pressure", "Context", "Distribution", tiersFrom: "MemeDef", perCohort: false),
            F("RS_ResidualPressure", "residual pressure", "Context", "Distribution", tiersFrom: "MemeDef", perCohort: false),
            F("RS_SurroundingIdeologyShare", "surrounding ideology", "Context", "Distribution", tiersFrom: "MemeDef", perCohort: false),
            F("RS_Stratification", "stratification (context)", "Context", "Scalar", perCohort: false),
            F("RS_Roads", "road connectivity", "Context", "Scalar", perCohort: false),
            F("RS_Conflict", "conflict", "Context", "Scalar", perCohort: false),
            F("RS_Outposts", "outposts", "Context", "Scalar", perCohort: false),
            F("RS_PlayerColonyPull", "player colony pull", "Context", "Scalar", perCohort: false),
            F("RS_BiomeGrazing", "grazing land", "Context", "Scalar", perCohort: false),
            F("RS_BiomeForest", "forest cover", "Context", "Scalar", perCohort: false),
            F("RS_MineralPool", "mineral pool", "Context", "Scalar", perCohort: false),
            F("RS_StonePool", "stone pool", "Context", "Scalar", perCohort: false),
            F("RS_GeothermalPool", "geothermal", "Context", "Scalar", perCohort: false),
            F("RS_RuinsPool", "ruins", "Context", "Scalar", perCohort: false),
            F("RS_Pollution", "pollution", "Context", "Scalar", perCohort: false),
            // Derived — no state; recomputed each step.
            F("RS_Balance", "balance (middle x skilled)", "Derived", "Scalar", perCohort: false),
            F("RS_DrugBurdenAgg", "drug burden (aggregate)", "Derived", "Scalar", perCohort: false),
            F("RS_MigrationAgg", "migration (aggregate)", "Derived", "Scalar", perCohort: false),
            F("RS_SlaveryEnslavedShare", "enslaved share", "Derived", "Scalar", perCohort: false),
            F("RS_FoodSelfSuff", "food self-sufficiency", "Derived", "Scalar", perCohort: false),
            F("RS_Inequality", "inequality", "Derived", "Scalar", perCohort: false),
            F("RS_DependencyRatio", "dependency ratio", "Derived", "Scalar"),
        };

        private static SectorRow S(string def, string label, int laborTier, string parent = null, string dlc = null)
            => new SectorRow { defName = def, label = label, laborTier = laborTier, parent = parent, requiresDlc = dlc, source = "builtin" };

        // laborTier: Illiterate 0 / Primary 1 / Secondary 2 / Undergrad 3 / Postgrad 4 (Core's EducationTier ordinals).
        public static readonly SectorRow[] Sectors =
        {
            S("RS_Agriculture", "agriculture", 0), S("RS_Extraction", "extraction", 1), S("RS_Manufacturing", "manufacturing", 2),
            S("RS_Services", "services", 1), S("RS_Military", "military", 0), S("RS_Public", "public", 2),
            S("RS_Agri_Food", "food crops", 0, "RS_Agriculture"), S("RS_Agri_Livestock", "livestock", 0, "RS_Agriculture"), S("RS_Agri_Fiber", "fibre crops", 1, "RS_Agriculture"),
            S("RS_Agri_Medicinal", "medicinal crops", 1, "RS_Agriculture"), S("RS_Agri_CashCrops", "cash crops", 1, "RS_Agriculture"), S("RS_Agri_Forestry", "forestry", 0, "RS_Agriculture"),
            S("RS_Ext_Mining", "mining", 1, "RS_Extraction"), S("RS_Ext_Quarrying", "quarrying", 0, "RS_Extraction"), S("RS_Ext_Fuel", "fuel", 2, "RS_Extraction"), S("RS_Ext_Salvage", "salvage", 0, "RS_Extraction"),
            S("RS_Mfg_Refining", "refining and smelting", 2, "RS_Manufacturing"), S("RS_Mfg_Construction", "construction materials", 1, "RS_Manufacturing"), S("RS_Mfg_Textiles", "textiles and apparel", 1, "RS_Manufacturing"),
            S("RS_Mfg_Armaments", "armaments", 2, "RS_Manufacturing"), S("RS_Mfg_Components", "components and electronics", 3, "RS_Manufacturing"), S("RS_Mfg_Pharma", "pharmaceuticals", 3, "RS_Manufacturing"),
            S("RS_Mfg_Drugs", "recreational drugs and alcohol", 1, "RS_Manufacturing"), S("RS_Mfg_FoodProcessing", "food processing", 0, "RS_Manufacturing"), S("RS_Mfg_Energy", "energy", 2, "RS_Manufacturing"),
            S("RS_Mfg_Fabrication", "advanced fabrication", 4, "RS_Manufacturing"),
            S("RS_Svc_Trade", "merchants and caravans", 1, "RS_Services"), S("RS_Svc_Transport", "transport and logistics", 1, "RS_Services"), S("RS_Svc_Hospitality", "hospitality and recreation", 0, "RS_Services"),
            S("RS_Svc_Finance", "finance and markets", 3, "RS_Services"), S("RS_Svc_Healthcare", "healthcare", 3, "RS_Services"), S("RS_Svc_Education", "education and research", 3, "RS_Services"),
            S("RS_Svc_SlaveTrade", "slave trade", 0, "RS_Services", "Ideology"),
            S("RS_Mil_Garrison", "garrison", 0, "RS_Military"), S("RS_Mil_Raiding", "raiding and piracy", 0, "RS_Military"), S("RS_Mil_Mercenary", "mercenaries", 1, "RS_Military"),
            S("RS_Pub_Administration", "administration", 2, "RS_Public"), S("RS_Pub_Clergy", "clergy", 1, "RS_Public", "Ideology"),
        };
    }
}
