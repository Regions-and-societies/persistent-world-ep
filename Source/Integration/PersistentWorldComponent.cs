using System.Collections.Generic;
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
    /// <para>The scribed tracked overlay (#4) lives here too, so it travels inside the .rws.</para>
    /// </summary>
    public class PersistentWorldComponent : WorldComponent
    {
        /// <summary>Rebuild cadence: every two in-game days (60000 ticks per day).</summary>
        public const int CadenceTicks = 120000;

        private readonly MaterializationLoop loop = new MaterializationLoop();
        private readonly PopulationCatalogue catalogue = new PopulationCatalogue();
        private readonly WorldPawnLinks links = new WorldPawnLinks();
        private List<WorldPawnLinkRecord> linkRecords;   // scribe buffer for <see cref="links"/>
        private bool rebuildRequested;
        private int lastStartTick = int.MinValue;

        public PersistentWorldComponent(World world) : base(world) { }

        /// <summary>The component of the current world, or null when no world is loaded.</summary>
        public static PersistentWorldComponent Instance => Find.World?.GetComponent<PersistentWorldComponent>();

        /// <summary>The last completed dataset of the current world; the empty dataset when there is none.</summary>
        public static PopulationDataset Dataset => Instance?.loop.Current ?? PopulationDataset.Empty;

        /// <summary>The Def ↔ key lookup the current world's snapshots were built with.</summary>
        public PopulationCatalogue Catalogue => catalogue;

        public MaterializationLoop Loop => loop;

        /// <summary>The scribed world-pawn → slot table (#4). Reconciled on every snapshot.</summary>
        public WorldPawnLinks Links => links;

        /// <summary>Ask for a rebuild at the next tick instead of waiting for the cadence (an event just
        /// changed the population and a consumer wants a fresh dataset). Coalesces: many requests, one build.</summary>
        public void RequestRebuild() => rebuildRequested = true;

        /// <summary>Static convenience for consumers holding no component reference. No-op without a world.</summary>
        public static void Request() => Instance?.RequestRebuild();

        public override void FinalizeInit(bool fromLoad)
        {
            base.FinalizeInit(fromLoad);
            rebuildRequested = true;   // first build as soon as the world is live
        }

        public override void WorldComponentTick()
        {
            base.WorldComponentTick();
            if (!PersistentWorldInit.Enabled) return;

            // Publish a finished build first, so a rebuild started this tick never races the swap.
            loop.Poll();

            int tick = Find.TickManager?.TicksGame ?? 0;
            bool due = tick - lastStartTick >= CadenceTicks;
            if (!rebuildRequested && !due) return;
            if (loop.IsBuilding) return;   // skip; the cadence or the request fires again next tick

            StartBuild(tick);
        }

        // Kept separate so the R&S-typed snapshot code only JITs when Core is present and a build starts.
        private void StartBuild(int tick)
        {
            PopulationSnapshot snapshot = PopulationSnapshotBuilder.Take(catalogue, links);
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
            loop.BuildNow(PopulationSnapshotBuilder.Take(catalogue, links));
            lastStartTick = Find.TickManager?.TicksGame ?? 0;
            rebuildRequested = false;
            return loop.Current;
        }

        public override void ExposeData()
        {
            base.ExposeData();

            // The tracked overlay (#4) rides inside the .rws: three ints per linked world pawn, nothing else.
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                linkRecords = new List<WorldPawnLinkRecord>();
                foreach (PawnSlot slot in links.Records()) linkRecords.Add(new WorldPawnLinkRecord(slot));
            }
            Scribe_Collections.Look(ref linkRecords, "worldPawnLinks", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                var slots = new List<PawnSlot>();
                if (linkRecords != null)
                    foreach (WorldPawnLinkRecord r in linkRecords)
                        if (r != null && r.tile >= 0 && r.index >= 0) slots.Add(r.ToSlot());
                links.Load(slots);
                linkRecords = null;
            }
        }
    }
}
