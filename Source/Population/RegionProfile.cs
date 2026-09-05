using System;

namespace RegionsAndSocieties.PersistentWorld.Population
{
    /// <summary>
    /// A region's demographic distributions as plain numbers — the only thing the individual sampler
    /// reads. Built on the main thread from Core's <c>RegionDemographics</c> (see
    /// <c>Integration.RegionProfileBuilder</c>) and then handed to the background worker as part of an
    /// immutable snapshot. Holds no game types: xenotypes, factions and ideoligions are referred to by
    /// small integer keys into a <c>PopulationCatalogue</c>, so the worker never touches a Def.
    ///
    /// <para>Every share array may be null or all-zero; the sampler degrades to a sensible default in that
    /// case (working-age, illiterate, subsistence, no faction) rather than throwing. A weight array and its
    /// key array are parallel; an empty pair means "none" (a plain human, unowned, no ideoligion).</para>
    /// </summary>
    public sealed class RegionProfile
    {
        /// <summary>Share of people who are female, 0..1.</summary>
        public float femaleFraction = 0.5f;

        /// <summary>[child, working-age, elder] shares (Core's <c>AgeStructureRules</c> buckets).</summary>
        public float[] ageShares;

        /// <summary>Five education-tier shares, illiterate..postgrad (Core's <c>EducationRules</c>).</summary>
        public float[] educationShares;

        /// <summary>Four socioeconomic-tier shares, subsistence..affluent (Core's <c>SocioeconomicRules</c>).</summary>
        public float[] sesShares;

        /// <summary>Four occupation-sector shares, agriculture/industry/military/trade (Core's <c>EmploymentRules</c>).</summary>
        public float[] occupationShares;

        /// <summary>Share of working-age people in formal employment, 0..100.</summary>
        public int employmentRate = 60;

        /// <summary>Oldest age an elder here can be. Core's base elder ceiling is 90; a long-lived caste
        /// stretches it. The builder sets it from the region's longevity where that is known.</summary>
        public int elderMaxAge = DefaultElderMaxAge;

        public const int DefaultElderMaxAge = 90;

        /// <summary>Xenotype catalogue keys and their weights (parallel). Empty = everyone is a plain human.</summary>
        public int[] raceKeys;
        public float[] raceWeights;

        /// <summary>Faction catalogue keys and their weights (parallel). Empty = unowned wilderness.</summary>
        public int[] factionKeys;
        public float[] factionWeights;

        /// <summary>Ideoligion catalogue keys and their weights (parallel). Empty = no ideoligion.</summary>
        public int[] ideoKeys;
        public float[] ideoWeights;

        /// <summary>A profile with nothing known: the sampler's default path end to end.</summary>
        public static RegionProfile Empty() => new RegionProfile();

        /// <summary>True when the keys/weights pair is usable for a weighted pick.</summary>
        internal static bool HasWeights(int[] keys, float[] weights)
            => keys != null && weights != null && keys.Length > 0 && keys.Length == weights.Length;
    }
}
