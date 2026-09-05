using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RegionsAndSocieties.PersistentWorld.Integration
{
    /// <summary>
    /// The main-thread lookup that turns Defs into the small integer keys the pure population layer works
    /// with, and back. Keys are assigned in first-seen order and never reused within a session, so a
    /// profile built earlier stays valid after new factions or xenotypes appear. Labels are captured at
    /// registration so the background worker and the export can name things without touching a Def.
    /// </summary>
    public sealed class PopulationCatalogue
    {
        private readonly Dictionary<XenotypeDef, int> raceKeys = new Dictionary<XenotypeDef, int>();
        private readonly Dictionary<Faction, int> factionKeys = new Dictionary<Faction, int>();
        private readonly Dictionary<Ideo, int> ideoKeys = new Dictionary<Ideo, int>();

        private readonly List<string> raceLabels = new List<string>();
        private readonly List<string> factionLabels = new List<string>();
        private readonly List<string> ideoLabels = new List<string>();

        public int KeyOf(XenotypeDef def)
        {
            if (def == null) return -1;
            if (!raceKeys.TryGetValue(def, out int key))
            {
                key = raceLabels.Count;
                raceKeys[def] = key;
                raceLabels.Add(def.LabelCap.ToString().NullOrEmpty() ? def.defName : def.LabelCap.ToString());
            }
            return key;
        }

        public int KeyOf(Faction faction)
        {
            if (faction == null) return -1;
            if (!factionKeys.TryGetValue(faction, out int key))
            {
                key = factionLabels.Count;
                factionKeys[faction] = key;
                factionLabels.Add(faction.Name ?? faction.def?.defName ?? "faction");
            }
            return key;
        }

        public int KeyOf(Ideo ideo)
        {
            if (ideo == null) return -1;
            if (!ideoKeys.TryGetValue(ideo, out int key))
            {
                key = ideoLabels.Count;
                ideoKeys[ideo] = key;
                ideoLabels.Add(ideo.name ?? "ideoligion");
            }
            return key;
        }

        public string RaceLabel(int key) => Label(raceLabels, key, "Human");
        public string FactionLabel(int key) => Label(factionLabels, key, "unowned");
        public string IdeoLabel(int key) => Label(ideoLabels, key, "none");

        public int RaceCount => raceLabels.Count;
        public int FactionCount => factionLabels.Count;
        public int IdeoCount => ideoLabels.Count;

        /// <summary>A copy of the label tables, safe to hand to the background worker.</summary>
        public string[] RaceLabels() => raceLabels.ToArray();
        public string[] FactionLabels() => factionLabels.ToArray();
        public string[] IdeoLabels() => ideoLabels.ToArray();

        private static string Label(List<string> labels, int key, string none)
            => key < 0 || key >= labels.Count ? none : labels[key];
    }
}
