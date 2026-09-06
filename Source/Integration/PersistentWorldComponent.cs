using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RegionsAndSocieties.PersistentWorld.Db;
using RegionsAndSocieties.PersistentWorld.Population;
using RimWorld.Planet;
using Verse;

namespace RegionsAndSocieties.PersistentWorld.Integration
{
    /// <summary>
    /// The game-side owner of the materialization loop (#3) and of the world's database (#17–#20). Ticks
    /// the cadence, takes the snapshot on the main thread, hands it to the background build, polls for
    /// completion and publishes the swap. RimWorld instantiates every <see cref="WorldComponent"/> in a
    /// loaded assembly, so this exists even without a Core edition — in which case it does nothing: every
    /// R&amp;S-typed call sits behind <see cref="PersistentWorldInit.Enabled"/> in its own method.
    ///
    /// <para><b>State.</b> The database is the source of truth for per-person state, as a lineage of
    /// commits — one per save file, each holding only what changed since its parent. The .rws carries the
    /// world id, this save's commit id and its parent's, plus a packed copy of the overlay as a recovery
    /// seed for when the database has no commit for the save (moved machine, deleted database). The
    /// database wins whenever it has the commit.</para>
    /// </summary>
    public class PersistentWorldComponent : WorldComponent
    {
        /// <summary>Rebuild cadence: every two in-game days (60000 ticks per day).</summary>
        public const int CadenceTicks = 120000;

        /// <summary>How long a save file may be missing before its commit is collected (#19).</summary>
        public static readonly TimeSpan MissingGrace = TimeSpan.FromDays(7);

        private readonly MaterializationLoop loop = new MaterializationLoop();
        private readonly PopulationCatalogue catalogue = new PopulationCatalogue();
        private readonly WorldPawnLinks links = new WorldPawnLinks();
        private readonly PopulationOverlay overlay = new PopulationOverlay();
        private CensusDatabase db;
        private string workingId;                         // the commit play writes into; becomes the save id on save
        private string savedId;                           // scribed: this save's commit
        private string parentId;                          // scribed: the commit this save was loaded from
        private string worldIdScribed;                    // scribed: guards against a database of another world
        private List<WorldPawnLinkRecord> linkRecords;    // scribe buffer for <see cref="links"/>
        private string overlayBlob;                       // scribe buffer for <see cref="overlay"/> (base64), the recovery copy
        private bool rebuildRequested;
        private int lastStartTick = int.MinValue;
        private Task<PopulationDataset> restore;          // the census read kicked off on load (#20)
        private bool loadedFromSave;

        public PersistentWorldComponent(World world) : base(world)
        {
            loop.Swapped += ds => { if (loop.Swaps > 0) db?.WriteCensusAsync(ds, Find.TickManager?.TicksGame ?? 0); };
            overlay.OnStored = d => db?.Run("delta write", c => new LineageStore(c).Upsert(workingId, in d));
            overlay.OnDropped = d => db?.Run("delta clear", c => new LineageStore(c).Clear(workingId, d.id, d.birthTile, d.birthIndex));
        }

        /// <summary>The component of the current world, or null when no world is loaded.</summary>
        public static PersistentWorldComponent Instance => Find.World?.GetComponent<PersistentWorldComponent>();

        /// <summary>The last completed dataset of the current world; the empty dataset when there is none.</summary>
        public static PopulationDataset Dataset => Instance?.loop.Current ?? PopulationDataset.Empty;

        public PopulationCatalogue Catalogue => catalogue;
        public MaterializationLoop Loop => loop;
        public WorldPawnLinks Links => links;
        public PopulationOverlay Overlay => overlay;
        public CensusDatabase Database => db;
        public string WorkingCommit => workingId;
        public string SavedCommit => savedId;
        public string ParentCommit => parentId;

        public void RequestRebuild() => rebuildRequested = true;
        public static void Request() => Instance?.RequestRebuild();

        // ---------------------------------------------------------------- lifecycle

        public override void FinalizeInit(bool fromLoad)
        {
            base.FinalizeInit(fromLoad);
            rebuildRequested = true;   // first build as soon as the world is live
            if (!PersistentWorldInit.Enabled) return;
            OpenDatabase(fromLoad);
            BeginRestore();
        }

        // Kept separate so the database-typed code only JITs when Core is present.
        private void OpenDatabase(bool fromLoad)
        {
            string worldId = PopulationSidecar.WorldId();
            int seed = Find.World?.info?.Seed ?? 0;
            db = new CensusDatabase(worldId, seed);
            if (!db.Open()) { db = null; workingId = null; return; }

            int tick = Find.TickManager?.TicksGame ?? 0;
            db.Run("lineage open", c =>
            {
                var store = new LineageStore(c);
                if (loadedFromSave && !string.IsNullOrEmpty(savedId))
                {
                    if (store.Exists(savedId) && worldIdScribed == worldId)
                    {
                        // Database wins: the save's effective overlay is its lineage.
                        overlay.Clear();
                        overlay.Load(store.Resolve(savedId));
                    }
                    else
                    {
                        // Recovery: the .rws copy seeds a root commit under this save's id.
                        store.Open(null, tick, savedId);
                        store.UpsertAll(savedId, overlay.Records());
                        store.Seal(savedId, SaveLineageHooks.LoadedFileName ?? "recovered", tick);
                        Log.Message($"[R&S PersistentWorld] No lineage for this save in the database; seeded it from the save's own overlay ({overlay.Count} records).");
                    }
                    workingId = store.Open(savedId, tick);
                }
                else
                {
                    workingId = store.Open(null, tick);   // a new world, or a save from before the database
                    if (overlay.Count > 0) store.UpsertAll(workingId, overlay.Records());
                }
                store.ObserveFiles(CensusDatabase.SavedGameNames(), DateTime.UtcNow);
                int dropped = store.Collect(workingId, DateTime.UtcNow, MissingGrace);
                if (dropped > 0) Log.Message($"[R&S PersistentWorld] Collected {dropped} orphaned save lineage(s).");
            });
        }

        // Read the stored census off the main thread so queries have a planet before the first build
        // finishes. It is only ever adopted while nothing has been built (MaterializationLoop.Restore).
        private void BeginRestore()
        {
            CensusDatabase d = db;
            if (d == null) return;
            restore = Task.Run(() => d.ReadCensus());
        }

        /// <summary>The player deleted these save files in the dialog (#19): drop their lineages now.</summary>
        public void OnSavesDeleted(List<string> fileNames)
        {
            db?.Run("delete cleanup", c =>
            {
                var store = new LineageStore(c);
                store.MarkDeleted(fileNames);
                int dropped = store.Collect(workingId, DateTime.UtcNow, MissingGrace);
                if (dropped > 0) Log.Message($"[R&S PersistentWorld] Collected {dropped} lineage commit(s) of deleted saves.");
            });
        }

        // ---------------------------------------------------------------- tick

        public override void WorldComponentTick()
        {
            base.WorldComponentTick();
            if (!PersistentWorldInit.Enabled) return;

            loop.Poll();

            if (restore != null && restore.IsCompleted)
            {
                Task<PopulationDataset> r = restore;
                restore = null;
                if (r.Status == TaskStatus.RanToCompletion && r.Result != null && loop.Restore(r.Result))
                    Log.Message($"[R&S PersistentWorld] Restored {r.Result.Count:N0} people from the database; a fresh build follows.");
            }

            int tick = Find.TickManager?.TicksGame ?? 0;
            bool due = tick - lastStartTick >= CadenceTicks;
            if (!rebuildRequested && !due) return;
            if (loop.IsBuilding) return;

            StartBuild(tick);
        }

        private void StartBuild(int tick)
        {
            PopulationSnapshot snapshot = PopulationSnapshotBuilder.Take(catalogue, links, overlay);
            if (loop.TryStart(snapshot))
            {
                rebuildRequested = false;
                lastStartTick = tick;
            }
        }

        /// <summary>Snapshot, build and publish on the calling thread. For the debug dump; the game loop
        /// never blocks on this.</summary>
        public PopulationDataset BuildNow()
        {
            if (!PersistentWorldInit.Enabled) return PopulationDataset.Empty;
            loop.Cancel();
            loop.Wait();
            loop.Poll();
            loop.BuildNow(PopulationSnapshotBuilder.Take(catalogue, links, overlay));
            lastStartTick = Find.TickManager?.TicksGame ?? 0;
            rebuildRequested = false;
            return loop.Current;
        }

        // ---------------------------------------------------------------- scribe

        public override void ExposeData()
        {
            base.ExposeData();

            if (Scribe.mode == LoadSaveMode.Saving)
            {
                int tick = Find.TickManager?.TicksGame ?? 0;
                // This save IS the working commit: seal it under the file's name, and play continues in a child.
                if (db != null && workingId != null)
                {
                    string sealedId = workingId;
                    parentId = ParentOf(sealedId);
                    savedId = sealedId;
                    db.Run("lineage seal", c =>
                    {
                        var store = new LineageStore(c);
                        store.Seal(sealedId, SaveLineageHooks.SavingFileName ?? "unknown", tick);
                        workingId = store.Open(sealedId, tick);
                    });
                }
                worldIdScribed = PopulationSidecar.WorldId();
                linkRecords = new List<WorldPawnLinkRecord>();
                foreach (PawnLink link in links.Records()) linkRecords.Add(new WorldPawnLinkRecord(link));
                byte[] packed = overlay.ToBytes();
                overlayBlob = packed.Length == 0 ? null : Convert.ToBase64String(packed);
            }

            Scribe_Values.Look(ref worldIdScribed, "worldId");
            Scribe_Values.Look(ref savedId, "saveId");
            Scribe_Values.Look(ref parentId, "parentId");
            Scribe_Collections.Look(ref linkRecords, "worldPawnLinks", LookMode.Deep);
            Scribe_Values.Look(ref overlayBlob, "overlay");

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                loadedFromSave = true;
                var loaded = new List<PawnLink>();
                if (linkRecords != null)
                    foreach (WorldPawnLinkRecord r in linkRecords)
                        if (r != null && r.id != 0 && r.birthTile >= 0 && r.birthIndex >= 0) loaded.Add(r.ToLink());
                links.Load(loaded);
                linkRecords = null;

                byte[] packed = null;
                try { if (!string.IsNullOrEmpty(overlayBlob)) packed = Convert.FromBase64String(overlayBlob); }
                catch (FormatException) { Log.Warning("[R&S PersistentWorld] The saved overlay copy was unreadable; relying on the database."); }
                overlay.Load(packed);   // the recovery copy; the database replaces it in FinalizeInit when it has the commit
                overlayBlob = null;
            }
        }

        private string ParentOf(string commitId)
        {
            return db?.Run("lineage parent", c => new LineageStore(c).Info(commitId)?.parentId, null);
        }
    }
}
