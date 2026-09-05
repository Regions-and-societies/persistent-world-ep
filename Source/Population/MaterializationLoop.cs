using System;
using System.Threading;
using System.Threading.Tasks;

namespace RegionsAndSocieties.PersistentWorld.Population
{
    /// <summary>
    /// The double-buffered snapshot → compute → swap state machine (#3), free of game types so it can be
    /// exercised without RimWorld. The game façade calls <see cref="TryStart"/> with a fresh snapshot on
    /// its cadence (or on request) and <see cref="Poll"/> every tick; nothing else is needed.
    ///
    /// <list type="bullet">
    /// <item><b>Start</b> hands the snapshot to a background <see cref="Task"/>. If a build is already
    /// running the request is refused (returns false) — the caller simply tries again next cadence.</item>
    /// <item><b>Poll</b> (main thread) checks whether the task has finished and, if so, publishes its
    /// result as <see cref="Current"/> in one reference assignment. A failed or cancelled build publishes
    /// nothing and leaves the previous dataset in place.</item>
    /// <item><b>Current</b> is always the last <i>completed</i> build — never null, never half-built.</item>
    /// </list>
    /// </summary>
    public sealed class MaterializationLoop
    {
        private volatile PopulationDataset current = PopulationDataset.Empty;
        private Task<PopulationDataset> running;
        private CancellationTokenSource cancel;
        private int swaps;

        /// <summary>The last completed dataset. Safe to read from any thread; the reference is swapped atomically.</summary>
        public PopulationDataset Current => current;

        /// <summary>True while a build is in flight.</summary>
        public bool IsBuilding => running != null && !running.IsCompleted;

        /// <summary>How many builds have been published so far.</summary>
        public int Swaps => swaps;

        /// <summary>The last build failure, if the most recent build threw; cleared by the next success.</summary>
        public Exception LastError { get; private set; }

        /// <summary>Raised on the polling thread right after <see cref="Current"/> changes.</summary>
        public event Action<PopulationDataset> Swapped;

        /// <summary>Begin a background build of <paramref name="snapshot"/>. Returns false, doing nothing,
        /// if a build is already running.</summary>
        public bool TryStart(PopulationSnapshot snapshot)
        {
            if (snapshot == null || IsBuilding) return false;
            cancel = new CancellationTokenSource();
            CancellationToken token = cancel.Token;
            running = Task.Run(() => PopulationBuilder.Build(snapshot, token), token);
            return true;
        }

        /// <summary>Main-thread step: publish a finished build. Returns true when <see cref="Current"/> changed.</summary>
        public bool Poll()
        {
            Task<PopulationDataset> task = running;
            if (task == null || !task.IsCompleted) return false;
            running = null;

            if (task.Status == TaskStatus.RanToCompletion && task.Result != null)
            {
                current = task.Result;
                LastError = null;
                swaps++;
                Swapped?.Invoke(current);
                return true;
            }

            if (task.IsFaulted) LastError = task.Exception?.GetBaseException() ?? task.Exception;
            return false;   // cancelled or failed: keep the previous dataset
        }

        /// <summary>Publish a dataset that did not come from a build — a sidecar restored on load (#6).
        /// Only honoured while nothing has been built yet, so a restored cache never overwrites live data.
        /// Returns true when it became current.</summary>
        public bool Restore(PopulationDataset restored)
        {
            if (restored == null || swaps > 0) return false;
            current = restored;
            Swapped?.Invoke(current);
            return true;
        }

        /// <summary>Abandon the in-flight build, if any. The previous dataset stays current.</summary>
        public void Cancel()
        {
            cancel?.Cancel();
        }

        /// <summary>Block until the in-flight build finishes (for tests and synchronous debug paths). Does
        /// not publish — call <see cref="Poll"/> afterwards.</summary>
        public void Wait()
        {
            Task<PopulationDataset> task = running;
            if (task == null) return;
            try { task.Wait(); } catch (AggregateException) { /* surfaced by Poll as LastError */ }
        }

        /// <summary>Build and publish synchronously on the calling thread. For the debug dump and tests;
        /// the game loop uses <see cref="TryStart"/> + <see cref="Poll"/>.</summary>
        public bool BuildNow(PopulationSnapshot snapshot)
        {
            if (!TryStart(snapshot)) return false;
            Wait();
            return Poll();
        }
    }
}
