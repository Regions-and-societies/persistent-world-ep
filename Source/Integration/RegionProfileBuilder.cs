using System.Collections.Generic;
using RegionsAndSocieties.Demographics;
using RegionsAndSocieties.PersistentWorld.Population;
using RimWorld;
using Verse;

namespace RegionsAndSocieties.PersistentWorld.Integration
{
    /// <summary>
    /// The one façade between Core's <see cref="RegionDemographics"/> (game types, main thread only) and
    /// the pure <see cref="RegionProfile"/> the sampler reads. Copies the share arrays and turns every
    /// Def-keyed dictionary into a parallel keys/weights pair through the <see cref="PopulationCatalogue"/>.
    /// Does not duplicate Core's model: it only re-shapes what Core already derived.
    /// </summary>
    public static class RegionProfileBuilder
    {
        /// <summary>Build the profile for a region. A null or unsettled aggregate yields
        /// <see cref="RegionProfile.Empty"/> (the sampler's default path).</summary>
        public static RegionProfile Build(RegionDemographics demo, PopulationCatalogue catalogue)
        {
            if (demo == null || demo.settledTiles <= 0) return RegionProfile.Empty();

            var profile = new RegionProfile
            {
                femaleFraction = demo.femaleFraction,
                ageShares = Copy(demo.ageShares),
                educationShares = Copy(demo.educationShares),
                sesShares = Copy(demo.sesShares),
                occupationShares = Copy(demo.occupationShares),
                employmentRate = demo.employmentRate,
                elderMaxAge = ElderCeilingFor(demo),
            };

            Split(demo.raceShares, catalogue.KeyOf, out profile.raceKeys, out profile.raceWeights);
            Split(demo.factionShares, catalogue.KeyOf, out profile.factionKeys, out profile.factionWeights);
            Split(demo.ideoShares, catalogue.KeyOf, out profile.ideoKeys, out profile.ideoWeights);
            return profile;
        }

        // Core does not expose the region's longevity, only the median age it produced. When that median
        // already sits above the base elder ceiling the region is long-lived, so stretch the ceiling to
        // keep the median reachable; otherwise the base ceiling stands.
        private static int ElderCeilingFor(RegionDemographics demo)
        {
            int ceiling = RegionProfile.DefaultElderMaxAge;
            if (demo.medianAge > ceiling) ceiling = demo.medianAge + 10;
            return ceiling;
        }

        private static float[] Copy(float[] src)
        {
            if (src == null) return null;
            var dst = new float[src.Length];
            System.Array.Copy(src, dst, src.Length);
            return dst;
        }

        private delegate int KeyFn<T>(T value);

        private static void Split<T>(Dictionary<T, float> shares, KeyFn<T> keyOf, out int[] keys, out float[] weights)
        {
            if (shares == null || shares.Count == 0) { keys = null; weights = null; return; }
            var k = new List<int>(shares.Count);
            var w = new List<float>(shares.Count);
            foreach (var kv in shares)
            {
                if (kv.Key == null || kv.Value <= 0f) continue;
                k.Add(keyOf(kv.Key));
                w.Add(kv.Value);
            }
            keys = k.Count > 0 ? k.ToArray() : null;
            weights = w.Count > 0 ? w.ToArray() : null;
        }
    }
}
