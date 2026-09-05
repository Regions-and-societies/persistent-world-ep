// Behaviour tests for the Core-presence rule: the EP must switch on for either Regions and Societies
// edition and stay off (never throw) when neither is loaded or the mod list cannot be read.
using System;
using System.Collections.Generic;
using RegionsAndSocieties.PersistentWorld.Integration;

namespace CorePresenceTests
{
    public static class Program
    {
        private static int failures;

        public static int Main()
        {
            Section("either edition counts as present");
            Check("MMF edition alone", CorePresence.IsPresent(Active(CorePresence.CoreMmfId)));
            Check("RP2 edition alone", CorePresence.IsPresent(Active(CorePresence.CoreRp2Id)));
            Check("both (should never happen, but must not break)", CorePresence.IsPresent(Active(CorePresence.CoreMmfId, CorePresence.CoreRp2Id)));

            Section("absence is absence");
            Check("empty mod list", !CorePresence.IsPresent(Active()));
            Check("unrelated mods only", !CorePresence.IsPresent(Active("brrainz.harmony", "NozoMe.MapModeFramework", "RegionsAndSocieties.WorldMapExport")));
            Check("null probe reads as absent, not a throw", !CorePresence.IsPresent(null));

            Section("edition ids are what About.xml loadAfter names");
            Check("MMF id", CorePresence.CoreMmfId == "RegionsAndSocieties.Core");
            Check("RP2 id", CorePresence.CoreRp2Id == "RegionsAndSocieties.CoreRP2");
            Check("exactly two editions", CorePresence.EditionIds.Length == 2);

            Section("probe is asked for each edition until one hits");
            var asked = new List<string>();
            CorePresence.IsPresent(id => { asked.Add(id); return false; });
            Check("both ids probed on a miss", asked.Count == 2 && asked.Contains(CorePresence.CoreMmfId) && asked.Contains(CorePresence.CoreRp2Id));
            asked.Clear();
            CorePresence.IsPresent(id => { asked.Add(id); return true; });
            Check("stops at the first hit", asked.Count == 1);

            Section("the absent warning names the mod and says what is off");
            Check("tagged", CorePresence.AbsentWarning.StartsWith("[R&S PersistentWorld]"));
            Check("says disabled", CorePresence.AbsentWarning.Contains("disabled"));

            Console.WriteLine(failures == 0 ? "corepresence: all checks passed" : $"corepresence: {failures} FAILED");
            return failures == 0 ? 0 : 1;
        }

        private static Func<string, bool> Active(params string[] ids)
        {
            var set = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            return id => set.Contains(id);
        }

        private static void Section(string name) => Console.WriteLine("-- " + name);

        private static void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + name);
            if (!ok) failures++;
        }
    }
}
