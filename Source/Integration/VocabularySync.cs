using System;
using System.Collections.Generic;
using RegionsAndSocieties.PersistentWorld.Db;
using RimWorld;
using Verse;

namespace RegionsAndSocieties.PersistentWorld.Integration
{
    /// <summary>
    /// Fills the vocabulary tables from the game: the built-in seed always, the xenotype cohorts from the
    /// Def database with their gene-derived constants (model §1), and — once Core ships its
    /// <c>DemographicFactorDef</c> / <c>EconomicSectorDef</c> loaders — the factors, edges and sectors from
    /// Core's Defs, found by reflection so this EP needs no reference to types that do not exist yet.
    /// Main thread only; runs once per database open.
    /// </summary>
    public static class VocabularySync
    {
        public static void Run(CensusDatabase db, PopulationCatalogue catalogue)
        {
            if (db == null) return;
            db.Run("vocabulary sync", c =>
            {
                var store = new VocabularyStore(c);
                store.SeedBuiltin();
                store.UpsertCohortKinds(CohortKinds(catalogue));
                int fromDefs = SyncCoreDefs(store);
                if (fromDefs > 0) Log.Message($"[R&S PersistentWorld] Synced {fromDefs} factor/sector Def(s) from Core into the vocabulary.");
            });
        }

        /// <summary>Every xenotype the game knows, keyed through the catalogue, with what its genes say about it.
        /// Without Biotech there is only the plain human (key -1).</summary>
        public static List<CohortKindRow> CohortKinds(PopulationCatalogue catalogue)
        {
            var list = new List<CohortKindRow> { new CohortKindRow { key = -1, defName = "Baseliner", label = "Human", inheritable = true, lifespan = 80f, fertility = 0.45f } };
            if (!ModsConfig.BiotechActive) return list;
            foreach (XenotypeDef def in DefDatabase<XenotypeDef>.AllDefsListForReading)
            {
                if (def == null) continue;
                var row = new CohortKindRow
                {
                    key = catalogue.KeyOf(def),
                    defName = def.defName,
                    label = def.LabelCap.ToString().NullOrEmpty() ? def.defName : def.LabelCap.ToString(),
                    inheritable = def.inheritable,
                    lifespan = 80f, fertility = 0.45f,
                };
                ReadGenes(def, ref row);
                list.Add(row);
            }
            return list;
        }

        // Lifespan from Longevity/Ageless genes, drug dependency from ChemicalDependency genes, fragility from
        // frailty-type genes, fertility from Fertility genes — by defName pattern, so a modded xenotype gets
        // sensible values with zero knowledge of it.
        private static void ReadGenes(XenotypeDef def, ref CohortKindRow row)
        {
            if (def.genes == null) return;
            foreach (GeneDef g in def.genes)
            {
                if (g == null) continue;
                string n = g.defName ?? "";
                if (n.IndexOf("Ageless", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Deathless", StringComparison.OrdinalIgnoreCase) >= 0) row.lifespan = 1000f;
                else if (n.IndexOf("Longevity", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Lifespan", StringComparison.OrdinalIgnoreCase) >= 0) row.lifespan = Math.Max(row.lifespan, 140f);
                if (n.IndexOf("ChemicalDependency", StringComparison.OrdinalIgnoreCase) >= 0 || g.chemical != null) row.dependency = Math.Min(1f, row.dependency + 0.5f);
                if (n.IndexOf("Frail", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Sickly", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Immunity_Weak", StringComparison.OrdinalIgnoreCase) >= 0) row.fragility += 0.03f;
                if (n.IndexOf("Fertility", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Fertile", StringComparison.OrdinalIgnoreCase) >= 0)
                    row.fertility = n.IndexOf("Sterile", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Low", StringComparison.OrdinalIgnoreCase) >= 0 ? row.fertility * 0.5f : row.fertility * 1.3f;
                if (n.IndexOf("Sterile", StringComparison.OrdinalIgnoreCase) >= 0) row.fertility = 0f;
            }
        }

        // Core's Def types arrive with its 0.5.0 model; when they exist, read them by reflection and mark the
        // rows source='def' so the built-in seed never overwrites them again.
        private static int SyncCoreDefs(VocabularyStore store)
        {
            int n = 0;
            try
            {
                Type factorType = GenTypes.GetTypeInAnyAssembly("RegionsAndSocieties.Demographics.DemographicFactorDef");
                if (factorType != null) n += SyncFactors(store, factorType);
                Type sectorType = GenTypes.GetTypeInAnyAssembly("RegionsAndSocieties.Economy.EconomicSectorDef");
                if (sectorType != null) n += SyncSectors(store, sectorType);
            }
            catch (Exception e)
            {
                Log.Warning("[R&S PersistentWorld] Reading Core's demographic Defs failed; keeping the built-in vocabulary (" + e.Message + ").");
            }
            return n;
        }

        private static IEnumerable<Def> AllDefs(Type defType)
        {
            Type db = typeof(DefDatabase<>).MakeGenericType(defType);
            var prop = db.GetProperty("AllDefsListForReading");
            var list = prop?.GetValue(null) as System.Collections.IEnumerable;
            if (list == null) yield break;
            foreach (object o in list) if (o is Def d) yield return d;
        }

        private static object Field(object o, string name)
        {
            var f = o.GetType().GetField(name);
            return f?.GetValue(o);
        }

        private static float F(object o, string name, float fallback = 0f) { object v = Field(o, name); return v is float f ? f : v is double d ? (float)d : v is int i ? i : fallback; }
        private static string S(object o, string name) => Field(o, name)?.ToString();

        private static int SyncFactors(VocabularyStore store, Type factorType)
        {
            var factors = new List<FactorRow>();
            var edges = new List<EdgeRow>();
            foreach (Def d in AllDefs(factorType))
            {
                var row = new FactorRow
                {
                    defName = d.defName, label = d.label ?? "", category = S(d, "category") ?? "Mutable", kind = S(d, "kind") ?? "Scalar",
                    tiersFrom = S(d, "tiersFrom"), velocity = F(d, "velocity"), inertia = F(d, "inertia"), deadband = F(d, "deadband"),
                    releaseBand = F(d, "releaseBand"), maxStep = F(d, "maxStepPerYear"), perCohort = true, source = "def",
                };
                if (Field(d, "tiers") is System.Collections.IEnumerable tiers)
                {
                    var t = new List<string>();
                    foreach (object x in tiers) if (x != null) t.Add(x.ToString());
                    row.tiers = t.ToArray();
                }
                factors.Add(row);
                if (Field(d, "influences") is System.Collections.IEnumerable infl)
                    foreach (object e in infl)
                    {
                        if (e == null) continue;
                        edges.Add(new EdgeRow
                        {
                            factor = d.defName, source = S(e, "source") ?? "", sourceTier = S(e, "tier") ?? "", targetTier = S(e, "targetTier") ?? "",
                            weight = F(e, "weight"), curve = S(e, "curve") ?? "Linear", lagYears = (int)F(e, "lagYears"), mode = S(e, "mode") ?? "Stress", spill = S(e, "spill"),
                            threshold = Field(e, "threshold") is float th ? th : (float?)null,
                        });
                    }
            }
            store.UpsertFactors(factors);
            store.UpsertEdges(edges);
            return factors.Count;
        }

        private static int SyncSectors(VocabularyStore store, Type sectorType)
        {
            var rows = new List<SectorRow>();
            foreach (Def d in AllDefs(sectorType))
            {
                object parent = Field(d, "parent");
                object tier = Field(d, "laborTier");
                rows.Add(new SectorRow
                {
                    defName = d.defName, label = d.label ?? "", parent = parent is Def pd ? pd.defName : parent?.ToString(),
                    laborTier = tier is Enum ? Convert.ToInt32(tier) : (int)F(d, "laborTier"), requiresDlc = S(d, "requiresDlc"), source = "def",
                });
            }
            store.UpsertSectors(rows);
            return rows.Count;
        }
    }
}
