using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using RegionsAndSocieties.PersistentWorld.Population;
using RimWorld.Planet;
using Verse;

namespace RegionsAndSocieties.PersistentWorld.Integration
{
    /// <summary>
    /// Where the sidecar lives and when it is written (#6). Files go under the game's save-data folder in
    /// a <c>PersistentWorld</c> subfolder, named by a stable world id, so they follow the world (not the
    /// save file's name, which the player renames and copies freely). Writing happens on a background
    /// task over the immutable dataset, so the game loop never waits on disk; reading happens once per
    /// load to restore the previous view until the first fresh build lands.
    /// </summary>
    public static class PopulationSidecar
    {
        public const string Folder = "PersistentWorld";

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

        public static string Directory()
        {
            try { return Path.Combine(GenFilePaths.SaveDataFolderPath, Folder); }
            catch (Exception) { return null; }
        }

        public static string JsonPath() => PathFor("json");
        public static string CsvPath() => PathFor("csv");

        private static string PathFor(string ext)
        {
            string dir = Directory();
            return dir == null ? null : Path.Combine(dir, PopulationExport.FileName(WorldId(), ext));
        }

        private static Task pending;

        /// <summary>True while a write is in flight.</summary>
        public static bool IsWriting => pending != null && !pending.IsCompleted;

        /// <summary>Write both sidecars for <paramref name="ds"/> on a background task. A write already in
        /// flight is left to finish; the next swap writes again. Failures are logged once per session and
        /// never surface to the player — the file is a cache.</summary>
        public static void WriteAsync(PopulationDataset ds)
        {
            if (ds == null || IsWriting) return;
            string json = JsonPath(), csv = CsvPath();
            if (json == null) return;
            pending = Task.Run(() => WriteBoth(ds, json, csv));
        }

        private static bool warned;

        private static void WriteBoth(PopulationDataset ds, string jsonPath, string csvPath)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(jsonPath));
                WriteAtomic(jsonPath, w => PopulationExport.WriteJson(ds, w));
                WriteAtomic(csvPath, w => PopulationExport.WriteCsv(ds, w));
            }
            catch (Exception e)
            {
                if (!warned) { warned = true; Log.Warning("[R&S PersistentWorld] Sidecar write failed (the file is only a cache): " + e.Message); }
            }
        }

        // Write to a temp file and move it over the old one, so a crash mid-write never leaves a torn file.
        private static void WriteAtomic(string path, Action<TextWriter> write)
        {
            string tmp = path + ".tmp";
            using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false), 1 << 16)) write(w);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        /// <summary>Read the JSON sidecar of the current world, or null if there is none or it is not
        /// trustworthy (wrong schema, wrong seed, torn). Synchronous; call it from a background task.</summary>
        public static PopulationDataset TryRead(string path, int expectedSeed)
        {
            try
            {
                if (path == null || !File.Exists(path)) return null;
                using (var r = new StreamReader(path, Encoding.UTF8))
                {
                    PopulationDataset ds = PopulationExport.ReadJson(r);
                    if (ds == null || ds.snapshot.worldSeed != expectedSeed) return null;
                    return ds;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
