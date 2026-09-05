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

        [DebugAction(Category, "R&S PW: linked world pawns", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap | AllowedGameStates.PlayingOnWorld)]
        private static void LinkedPawns()
        {
            var comp = PersistentWorldComponent.Instance;
            if (comp == null) { Log.Message("[R&S PersistentWorld] No world."); return; }
            PopulationDataset ds = comp.Loop.Current;
            var sb = new StringBuilder();
            sb.AppendLine("--- R&S PW: linked world pawns (#4) ---");
            List<PawnSlot> records = comp.Links.Records();
            sb.AppendLine($"{records.Count} link(s) in the scribed table; {ds.LinkedCount} applied in the current dataset.");
            int shown = 0;
            foreach (PawnSlot r in records)
            {
                if (shown++ >= 60) { sb.AppendLine("  ..."); break; }
                if (ds.TryGet(r.tile, r.index, out Individual p) && p.pawnId == r.pawnId)
                    sb.AppendLine($"  pawn {r.pawnId,-8} tile {r.tile,-6} #{r.index,-5} {(p.female ? "F" : "M")} age {p.age,3} {p.ageBucket,-10} {p.education,-10} {p.ses,-11} {p.work,-10} faction={PersistentWorldApi.Label(Dimension.Faction, p.factionKey)} race={PersistentWorldApi.Label(Dimension.Xenotype, p.raceKey)}");
                else
                    sb.AppendLine($"  pawn {r.pawnId,-8} tile {r.tile,-6} #{r.index,-5} (not applied in the current dataset — awaiting the next build)");
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
            sb.AppendLine($"build #{ds.buildSerial}  people={ds.Count:N0}  households={ds.HouseholdCount:N0}  tiles={ds.TileCount:N0}  regions={ds.RegionIds.Length}  linked={ds.LinkedCount}  build={ds.buildMillis} ms  seed={ds.snapshot.worldSeed}  densityVersion={ds.snapshot.densityVersion}");
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
