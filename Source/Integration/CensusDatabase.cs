using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RegionsAndSocieties.PersistentWorld.Db;
using RegionsAndSocieties.PersistentWorld.Population;
using Verse;

namespace RegionsAndSocieties.PersistentWorld.Integration
{
    /// <summary>
    /// The per-world database on disk (#17): <c>Saves/PersistentWorld/census_&lt;worldId&gt;.db</c>, named by the
    /// world's persistent random value so it follows the world through every save of it. Connections are
    /// opened per operation (pooled by the driver), so the main thread's lineage writes and the background
    /// census writes never share one. Every method is a no-op that returns null/false when SQLite is not
    /// available, so callers need no second code path.
    /// </summary>
    public sealed class CensusDatabase
    {
        public const string Folder = "PersistentWorld";

        public readonly string worldId;
        public readonly int worldSeed;
        public readonly string path;
        private readonly string connectionString;
        private bool ready;
        private bool warned;

        public bool Ready => ready;

        public CensusDatabase(string worldId, int worldSeed)
        {
            this.worldId = worldId;
            this.worldSeed = worldSeed;
            string dir = Directory();
            path = dir == null ? null : Path.Combine(dir, "census_" + Safe(worldId) + ".db");
            connectionString = path == null ? null : new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true }.ToString();
        }

        /// <summary>Create or migrate the file. False (with one warning) if the database cannot be used.</summary>
        public bool Open()
        {
            if (ready) return true;
            if (!SqliteRuntime.Available || connectionString == null) return false;
            try
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (SqliteConnection c = Connect())
                    CensusSchema.Ensure(c, worldId, worldSeed);
                ready = true;
                return true;
            }
            catch (Exception e)
            {
                Warn("open", e);
                return false;
            }
        }

        /// <summary>A fresh open connection. Dispose it when done. Null when not ready.</summary>
        public SqliteConnection Connect()
        {
            if (connectionString == null) return null;
            var c = new SqliteConnection(connectionString);
            c.Open();
            return c;
        }

        /// <summary>Run <paramref name="work"/> against a connection on the calling thread. Exceptions are
        /// logged once and swallowed: the database is never allowed to take the game down.</summary>
        public bool Run(string what, Action<SqliteConnection> work)
        {
            if (!ready) return false;
            try
            {
                using (SqliteConnection c = Connect()) work(c);
                return true;
            }
            catch (Exception e)
            {
                Warn(what, e);
                return false;
            }
        }

        public T Run<T>(string what, Func<SqliteConnection, T> work, T fallback = default)
        {
            if (!ready) return fallback;
            try
            {
                using (SqliteConnection c = Connect()) return work(c);
            }
            catch (Exception e)
            {
                Warn(what, e);
                return fallback;
            }
        }

        private Task pending;

        /// <summary>True while a background census write is in flight.</summary>
        public bool IsWriting => pending != null && !pending.IsCompleted;

        /// <summary>Write the census tables for <paramref name="ds"/> on a background task (#20). A write in
        /// flight is left to finish; the next swap writes again.</summary>
        public void WriteCensusAsync(PopulationDataset ds, int tick)
        {
            if (!ready || ds == null || IsWriting) return;
            pending = Task.Run(() => Run("census write", c => CensusStore.Write(c, ds, tick)));
        }

        /// <summary>Read the stored census as a dataset (background-safe). Null when none matches this world.</summary>
        public PopulationDataset ReadCensus() => Run("census read", c => CensusStore.Read(c, worldSeed), null);

        public static string Directory()
        {
            try { return Path.Combine(GenFilePaths.SaveDataFolderPath, Folder); }
            catch (Exception) { return null; }
        }

        /// <summary>The names (no extension) of every saved game on disk.</summary>
        public static List<string> SavedGameNames()
        {
            var names = new List<string>();
            try
            {
                foreach (FileInfo f in GenFilePaths.AllSavedGameFiles)
                    if (f != null) names.Add(Path.GetFileNameWithoutExtension(f.Name));
            }
            catch (Exception) { }
            return names;
        }

        private void Warn(string what, Exception e)
        {
            if (warned) return;
            warned = true;
            Log.Warning($"[R&S PersistentWorld] Database {what} failed; continuing without it this session ({e.GetType().Name}: {e.Message}).");
        }

        private static string Safe(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in s ?? "world") sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            return sb.Length == 0 ? "world" : sb.ToString();
        }
    }
}
