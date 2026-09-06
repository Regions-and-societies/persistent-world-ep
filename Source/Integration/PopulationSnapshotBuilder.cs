using System.Collections.Generic;
using RegionsAndSocieties.Demographics;
using RegionsAndSocieties.PersistentWorld.Population;
using RimWorld.Planet;
using Verse;

namespace RegionsAndSocieties.PersistentWorld.Integration
{
    /// <summary>
    /// The snapshot step of snapshot → compute → swap (#3): the one place that reads Core's region manager,
    /// demographics aggregates and per-tile population, and copies them out as plain numbers. Main thread
    /// only. Everything it touches is already cached by Core (<c>RegionDemographicsUtility.ForRegion</c>
    /// is warmed on load), so a snapshot costs O(regions + populated tiles) of copying, not aggregation.
    /// </summary>
    public static class PopulationSnapshotBuilder
    {
        /// <summary>Take a snapshot of the current world, or <see cref="PopulationSnapshot.Empty"/> when
        /// there is no world or no Core region manager. <paramref name="links"/> is reconciled against the
        /// current world pawns first (which may move people through <paramref name="overlay"/>), so the
        /// snapshot carries the up-to-date link and overlay tables (#4, #9).</summary>
        public static PopulationSnapshot Take(PopulationCatalogue catalogue, WorldPawnLinks links = null, PopulationOverlay overlay = null)
        {
            World world = Find.World;
            WorldGrid grid = Find.WorldGrid;
            if (world == null || grid == null) return PopulationSnapshot.Empty();
            var mgr = world.GetComponent<SynapseRegionManager>();
            if (mgr?.Provinces == null) return PopulationSnapshot.Empty(world.info?.Seed ?? 0);

            PopulationDensityUtility.EnsureCache();
            int seed = world.info?.Seed ?? 0;

            var regionIds = new List<int>();
            var profiles = new List<RegionProfile>();
            var rows = new List<TileSlot>();

            foreach (GeographicProvince province in mgr.Provinces)
            {
                if (province == null || province.provinceType != ProvinceType.Land || province.tiles == null) continue;

                int slot = -1;   // assigned lazily: regions with nobody born in them take no profile
                for (int i = 0; i < province.tiles.Count; i++)
                {
                    int tile = province.tiles[i];
                    int pop = PopulationDensityUtility.GetSourcePopulationAtTile(tile);
                    if (pop <= 0) continue;
                    if (slot < 0)
                    {
                        slot = regionIds.Count;
                        regionIds.Add(province.id);
                        profiles.Add(RegionProfileBuilder.Build(RegionDemographicsUtility.ForRegion(province), catalogue));
                    }
                    rows.Add(new TileSlot { tile = tile, region = slot, population = pop });
                }
            }

            LinkedPerson[] linked = links?.Reconcile(seed, WorldPawnLinker.Candidates(catalogue), WorldPawnLinker.BirthsOn, overlay);
            PersonDelta[] deltas = overlay?.Records();

            return PopulationSnapshot.From(seed, rows, regionIds.ToArray(), profiles.ToArray(),
                catalogue.RaceLabels(), catalogue.FactionLabels(), catalogue.IdeoLabels(),
                PopulationDensityUtility.CacheVersion, deltas, linked);
        }
    }
}
