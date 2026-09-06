using System.Collections.Generic;
using System.Text;
using LudeonTK;
using RegionsAndSocieties.PersistentWorld.Integration;
using RegionsAndSocieties.PersistentWorld.Population;
using Verse;

namespace RegionsAndSocieties.PersistentWorld.UI
{
    /// <summary>
    /// Dev-mode entries under "Regions and Societies" (#5): the population dump is the in-game proof that
    /// the planet materialized and what it is made of. Each action just logs a report, so the numbers can
    /// be read from the log, compared across saves, or pasted into a bug report.
    /// </summary>
    public static class DebugActions_PersistentWorld
    {
        private const string Category = "Regions and Societies";

        [DebugAction(Category, "R&S PW: population dump", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap | AllowedGameStates.PlayingOnWorld)]
        private static void PopulationDump()
        {
            Log.Message(Report(PersistentWorldApi.Dataset));
        }

        [DebugAction(Category, "R&S PW: rebuild now + dump", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap | AllowedGameStates.PlayingOnWorld)]
        private static void RebuildAndDump()
        {
            if (!PersistentWorldApi.IsAvailable) { Log.Message("[R&S PersistentWorld] Not available: no Core edition or no world."); return; }
            PopulationDataset ds = PersistentWorldApi.RebuildNow();
            Log.Message(Report(ds));
        }

        [DebugAction(Category, "R&S PW: database status", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.Entry | AllowedGameStates.PlayingOnMap | AllowedGameStates.PlayingOnWorld)]
        private static void DatabaseStatus()
        {
            var sb = new StringBuilder();
            sb.AppendLine("--- R&S PW: database status (#17-#19) ---");
            sb.AppendLine($"SQLite runtime: {(SqliteRuntime.Available ? "loaded " + SqliteRuntime.Version + " from " + SqliteRuntime.LoadedFrom : "unavailable (" + SqliteRuntime.Error + ")")}");
            var comp = PersistentWorldComponent.Instance;
            var db = comp?.Database;
            if (db == null) { sb.AppendLine("no world database open"); Log.Message(sb.ToString()); return; }
            sb.AppendLine($"database: {db.path}  world={db.worldId} seed={db.worldSeed}  writing={db.IsWriting}");
            sb.AppendLine($"commits: working={comp.WorkingCommit} saved={comp.SavedCommit} parent={comp.ParentCommit}  overlay in memory={comp.Overlay.Count}");
            db.Run("status", c =>
            {
                foreach (string t in new[] { "commits", "deltas", "builds", "people", "households", "links", "tiles", "factors", "factor_edges", "sectors", "cohort_kinds", "region_years", "cohort_years", "factor_levels", "flows", "births_assigned", "settlement_years", "person_events" })
                    sb.Append("  ").Append(t).Append('=').Append(Db.CensusStore.Count(c, t));
                sb.AppendLine();
                sb.AppendLine($"  schema v{Db.CensusSchema.CurrentVersion(c)}  model: {Db.CensusSchema.Get(c, null, "model_version")}");
                var counts = new Db.HistoryStore(c).EventCounts(comp.WorkingCommit);
                if (counts.Count > 0) { sb.Append("  events along this lineage:"); foreach (var kv in counts) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value); sb.AppendLine(); }
                sb.AppendLine("  lineage:");
                foreach (Db.CommitInfo ci in new Db.LineageStore(c).All())
                    sb.AppendLine($"    {ci.saveId}  parent={ci.parentId ?? "-"}  file={(ci.fileName ?? "-")}  tick={ci.tick}  deltas={ci.deltaCount}  {(ci.sealed_ ? "sealed" : "working")}{(ci.superseded ? " superseded" : "")}{(ci.missingSince != null ? " missing since " + ci.missingSince : "")}{(ci.saveId == comp.WorkingCommit ? "  <- current" : "")}");
            });
            Log.Message(sb.ToString());
        }

        [DebugAction(Category, "R&S PW: collect orphaned lineages", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap | AllowedGameStates.PlayingOnWorld)]
        private static void CollectLineages()
        {
            var comp = PersistentWorldComponent.Instance;
            if (comp?.Database == null) { Log.Message("[R&S PersistentWorld] No database."); return; }
            comp.Database.Run("collect", c =>
            {
                var store = new Db.LineageStore(c);
                store.ObserveFiles(CensusDatabase.SavedGameNames(), System.DateTime.UtcNow);
                int n = store.Collect(comp.WorkingCommit, System.DateTime.UtcNow, PersistentWorldComponent.MissingGrace);
                Log.Message($"[R&S PersistentWorld] Collected {n} orphaned commit(s). Commits whose file has been missing under {PersistentWorldComponent.MissingGrace.TotalDays} days are kept.");
            });
        }

        [DebugAction(Category, "R&S PW: export CSV", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap | AllowedGameStates.PlayingOnWorld)]
        private static void ExportCsv()
        {
            string p = PopulationSidecar.WriteCsv(PersistentWorldApi.Dataset);
            Log.Message(p == null ? "[R&S PersistentWorld] CSV export failed." : "[R&S PersistentWorld] Wrote " + p);
        }

        [DebugAction(Category, "R&S PW: linked world pawns", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap | AllowedGameStates.PlayingOnWorld)]
        private static void LinkedPawns()
        {
            var comp = PersistentWorldComponent.Instance;
            if (comp == null) { Log.Message("[R&S PersistentWorld] No world."); return; }
            PopulationDataset ds = comp.Loop.Current;
            var sb = new StringBuilder();
            sb.AppendLine("--- R&S PW: linked world pawns (#4) ---");
            List<PawnLink> records = comp.Links.Records();
            sb.AppendLine($"{records.Count} link(s) in the scribed table; {ds.LinkedCount} applied in the current dataset; overlay holds {comp.Overlay.Count} changed people.");
            int shown = 0;
            foreach (PawnLink r in records)
            {
                if (shown++ >= 60) { sb.AppendLine("  ..."); break; }
                if (ds.TryGetById(r.id, out Individual p) && p.pawnId == r.pawnId)
                    sb.AppendLine($"  pawn {r.pawnId,-8} person {PersonId.ToHex(r.id)} born {r.birthTile}#{r.birthIndex} home {p.tile,-6} {(p.female ? "F" : "M")} age {p.age,3} {p.ageBucket,-10} {p.education,-10} {p.ses,-11} {p.work,-10} faction={PersistentWorldApi.Label(Dimension.Faction, p.factionKey)} race={PersistentWorldApi.Label(Dimension.Xenotype, p.raceKey)}");
                else
                    sb.AppendLine($"  pawn {r.pawnId,-8} person {PersonId.ToHex(r.id)} born {r.birthTile}#{r.birthIndex} (not applied in the current dataset — awaiting the next build)");
            }
            Log.Message(sb.ToString());
        }

        /// <summary>The whole-planet report: build stats, then every dimension's breakdown, then the ten
        /// most populous regions with their age / education / class mix. One method, so a headless bridge
        /// can log the same text a human reads from the menu.</summary>
        public static string Report(PopulationDataset ds)
        {
            var sb = new StringBuilder();
            sb.AppendLine("--- R&S PW: population dump (#5) ---");
            if (!PersistentWorldApi.IsAvailable) sb.AppendLine("(not available: no Core edition or no world)");
            sb.AppendLine($"build #{ds.buildSerial}  people={ds.Count:N0}  households={ds.HouseholdCount:N0}  homeTiles={ds.TileCount:N0}  regions={ds.RegionIds.Length}  linked={ds.LinkedCount}  moved={ds.MovedCount}  overlay={ds.snapshot.deltas.Length}  build={ds.buildMillis} ms  seed={ds.snapshot.worldSeed}  densityVersion={ds.snapshot.densityVersion}");
            if (ds.Count == 0) { sb.AppendLine("(no people — nothing built yet, or the world has no source population)"); return sb.ToString(); }

            foreach (Dimension d in new[] { Dimension.Sex, Dimension.AgeBucket, Dimension.Education, Dimension.Class, Dimension.WorkStatus, Dimension.Sector, Dimension.Xenotype, Dimension.Faction, Dimension.Ideoligion, Dimension.HouseholdSize, Dimension.Linked })
                AppendBreakdown(sb, d, PersistentWorldApi.Breakdown(PopulationFilter.All, d), ds.Count);

            sb.AppendLine("  top regions by population:");
            int[] byRegion = ds.index.byRegion;
            var order = new List<int>();
            for (int r = 0; r < ds.RegionIds.Length; r++) if (byRegion[r + 1] > 0) order.Add(r);
            order.Sort((a, b) => byRegion[b + 1].CompareTo(byRegion[a + 1]));
            for (int k = 0; k < order.Count && k < 10; k++)
            {
                int r = order[k];
                var f = new PopulationFilter { regionId = ds.RegionIds[r] };
                sb.AppendLine($"    region {ds.RegionIds[r],-6} {byRegion[r + 1],8:N0}  age {Ratio(PopulationQuery.Breakdown(ds, f, Dimension.AgeBucket))}  edu {Ratio(PopulationQuery.Breakdown(ds, f, Dimension.Education))}  class {Ratio(PopulationQuery.Breakdown(ds, f, Dimension.Class))}");
            }
            if (byRegion[0] > 0) sb.AppendLine($"    (no region)   {byRegion[0],8:N0}");
            return sb.ToString();
        }

        private static void AppendBreakdown(StringBuilder sb, Dimension d, Dictionary<string, int> counts, int total)
        {
            sb.Append("  ").Append(d).Append(": ");
            var keys = new List<string>(counts.Keys);
            keys.Sort((a, b) => counts[b].CompareTo(counts[a]));
            int shown = 0;
            foreach (string k in keys)
            {
                if (shown++ >= 12) { sb.Append("…"); break; }
                sb.Append(k).Append('=').Append(counts[k].ToString("N0")).Append(" (").Append((100f * counts[k] / total).ToString("0.#")).Append("%)  ");
            }
            sb.AppendLine();
        }

        private static string Ratio(int[] counts)
        {
            int total = 0; foreach (int c in counts) total += c;
            if (total == 0) return "-";
            var parts = new List<string>(counts.Length);
            foreach (int c in counts) parts.Add((100 * c / total).ToString());
            return string.Join("/", parts);
        }
    }
}
