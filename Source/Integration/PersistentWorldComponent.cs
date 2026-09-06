using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RegionsAndSocieties.PersistentWorld.Population;
using RimWorld.Planet;
using Verse;

namespace RegionsAndSocieties.PersistentWorld.Integration
{
    /// <summary>
    /// The game-side owner of the materialization loop (#3). Ticks the cadence, takes the snapshot on the
    /// main thread, hands it to the background build, polls for completion and publishes the swap.
    /// RimWorld instantiates every <see cref="WorldComponent"/> in a loaded assembly, so this exists even
    /// without a Core edition — in which case it does nothing: every R&amp;S-typed call sits behind
    /// <see cref="PersistentWorldInit.Enabled"/> in its own method, so the JIT never touches a missing type.
    ///
    /// <para>The only authoritative per-person state lives here and travels inside the .rws: the sparse
    /// overlay (#9, packed bytes) and the world-pawn link table (#4).</para>
    /// </summary>
    public class PersistentWorldComponent : WorldComponent
    {
        /// <summary>Rebuild cadence: every two in-game days (60000 ticks per day).</summary>
        public const int CadenceTicks = 120000;

        private readonly MaterializationLoop loop = new MaterializationLoop();
        private readonly PopulationCatalogue catalogue = new PopulationCatalogue();
        private readonly WorldPawnLinks links = new WorldPawnLinks();
        private readonly PopulationOverlay overlay = new PopulationOverlay();
        private List<WorldPawnLinkRecord> linkRecords;   // scribe buffer for <see cref="links"/>
        private string overlayBlob;                       // scribe buffer for <see cref="overlay"/> (base64)
        private bool rebuildRequested;
        private int lastStartTick = int.MinValue;
        private Task<PopulationDataset> restore;          // the sidecar read kicked off on load (#6)

        public PersistentWorldComponent(World world) : base(world)
        {
            // Every published build refreshes the sidecar; a restored one does not (it came from there).
            loop.Swapped += ds => { if (loop.Swaps > 0) PopulationSidecar.WriteAsync(ds); };
        }

        /// <summary>The component of the current world, or null when no world is loaded.</summary>
        public static PersistentWorldComponent Instance => Find.World?.GetComponent<PersistentWorldComponent>();

        /// <summary>The last completed dataset of the current world; the empty dataset when there is none.</summary>
        public static PopulationDataset Dataset => Instance?.loop.Current ?? PopulationDataset.Empty;

        /// <summary>The Def ↔ key lookup the current world's snapshots were built with.</summary>
        public PopulationCatalogue Catalogue => catalogue;

        public MaterializationLoop Loop => loop;

        /// <summary>The scribed world-pawn → person table (#4). Reconciled on every snapshot.</summary>
        public WorldPawnLinks Links => links;

        /// <summary>The scribed sparse overlay (#9): every person whose state differs from birth.</summary>
        public PopulationOverlay Overlay => overlay;

        /// <summary>Ask for a rebuild at the next tick instead of waiting for the cadence (an event just
        /// changed the population and a consumer wants a fresh dataset). Coalesces: many requests, one build.</summary>
        public void RequestRebuild() => rebuildRequested = true;

        /// <summary>Static convenience for consumers holding no component reference. No-op without a world.</summary>
        public static void Request() => Instance?.RequestRebuild();

        public override void FinalizeInit(bool fromLoad)
        {
            base.FinalizeInit(fromLoad);
            rebuildRequested = true;   // first build as soon as the world is live
            if (PersistentWorldInit.Enabled) BeginRestore();
        }

        // Read last session's sidecar off the main thread so queries have a planet before the first build
        // finishes. It is only ever adopted while nothing has been built (MaterializationLoop.Restore).
        private void BeginRestore()
        {
            string path = PopulationSidecar.JsonPath();
            int seed = Find.World?.info?.Seed ?? 0;
            if (path == null) return;
            restore = Task.Run(() => PopulationSidecar.TryRead(path, seed));
        }

        public override void WorldComponentTick()
        {
            base.WorldComponentTick();
            if (!PersistentWorldInit.Enabled) return;

            // Publish a finished build first, so a rebuild started this tick never races the swap.
            loop.Poll();

            if (restore != null && restore.IsCompleted)
            {
                Task<PopulationDataset> r = restore;
                restore = null;
                if (r.Status == TaskStatus.RanToCompletion && r.Result != null && loop.Restore(r.Result))
                    Log.Message($"[R&S PersistentWorld] Restored {r.Result.Count:N0} people from the sidecar; a fresh build follows.");
            }

            int tick = Find.TickManager?.TicksGame ?? 0;
            bool due = tick - lastStartTick >= CadenceTicks;
            if (!rebuildRequested && !due) return;
            if (loop.IsBuilding) return;   // skip; the cadence or the request fires again next tick

            StartBuild(tick);
        }

        // Kept separate so the R&S-typed snapshot code only JITs when Core is present and a build starts.
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

        public override void ExposeData()
        {
            base.ExposeData();

            if (Scribe.mode == LoadSaveMode.Saving)
            {
                linkRecords = new List<WorldPawnLinkRecord>();
                foreach (PawnLink link in links.Records()) linkRecords.Add(new WorldPawnLinkRecord(link));
                byte[] packed = overlay.ToBytes();
                overlayBlob = packed.Length == 0 ? null : Convert.ToBase64String(packed);
            }

            // Three ints and a long per linked pawn; 21 packed bytes per changed person. Nothing else.
            Scribe_Collections.Look(ref linkRecords, "worldPawnLinks", LookMode.Deep);
            Scribe_Values.Look(ref overlayBlob, "overlay");

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                var loaded = new List<PawnLink>();
                if (linkRecords != null)
                    foreach (WorldPawnLinkRecord r in linkRecords)
                        if (r != null && r.id != 0 && r.birthTile >= 0 && r.birthIndex >= 0) loaded.Add(r.ToLink());
                links.Load(loaded);
                linkRecords = null;

                byte[] packed = null;
                try { if (!string.IsNullOrEmpty(overlayBlob)) packed = Convert.FromBase64String(overlayBlob); }
                catch (FormatException) { Log.Warning("[R&S PersistentWorld] The saved overlay was unreadable; starting from the birth baseline."); }
                overlay.Load(packed);
                overlayBlob = null;
            }
        }
    }
}
