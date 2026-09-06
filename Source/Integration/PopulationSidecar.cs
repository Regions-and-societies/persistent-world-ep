using System;
using System.IO;
using System.Text;
using RegionsAndSocieties.PersistentWorld.Population;
using RimWorld.Planet;
using Verse;

namespace RegionsAndSocieties.PersistentWorld.Integration
{
    /// <summary>
    /// The world's stable id and the on-demand CSV export. The database (#17–#20) is the store; the CSV is a
    /// spreadsheet-friendly dump a debug action writes next to it under <c>Saves/PersistentWorld/</c>.
    /// </summary>
    public static class PopulationSidecar
    {
        /// <summary>The stable id of the current world: its persistent random value (fixed at worldgen and
        /// carried in every save of that world) in hex, or the seed when that is unavailable.</summary>
        public static string WorldId()
        {
            WorldInfo info = Find.World?.info;
            if (info == null) return "world";
            int prv = info.persistentRandomValue;
            if (prv != 0) return prv.ToString("x8");
            return info.Seed.ToString("x8");
        }

        public static string CsvPath()
        {
            string dir = CensusDatabase.Directory();
            return dir == null ? null : Path.Combine(dir, PopulationExport.FileName(WorldId(), "csv"));
        }

        /// <summary>Write the CSV for <paramref name="ds"/> now, on the calling thread. Returns the path or null.</summary>
        public static string WriteCsv(PopulationDataset ds)
        {
            string path = CsvPath();
            if (path == null || ds == null) return null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false), 1 << 16)) PopulationExport.WriteCsv(ds, w);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return path;
            }
            catch (Exception e)
            {
                Log.Warning("[R&S PersistentWorld] CSV export failed: " + e.Message);
                return null;
            }
        }
    }
}
