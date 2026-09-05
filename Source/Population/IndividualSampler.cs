using RegionsAndSocieties.Demographics;

namespace RegionsAndSocieties.PersistentWorld.Population
{
    /// <summary>
    /// The heart of the derive-the-majority bet (#2): an individual is a pure deterministic function of
    /// <c>(world seed, tile, index, region profile)</c>. Same inputs, same person, on every machine and
    /// every load — so the whole planet can be regenerated at will and nothing has to be saved.
    ///
    /// <para>Pure by design, like Core's <see cref="DemographicsRules"/>, whose seed mixer, RNG and weighted
    /// pick this reuses: plain numbers in, a plain <see cref="Individual"/> out, no game state, no
    /// allocation beyond the returned struct. Safe to call from the background worker.</para>
    ///
    /// <para>The draws happen in a fixed order from one per-person RNG stream (sex, age, race, faction,
    /// ideoligion, education, class, wealth, work, sector). Adding a draw at the END keeps every earlier
    /// attribute of every existing person stable; inserting one in the middle reshuffles the planet.</para>
    /// </summary>
    public static class IndividualSampler
    {
        // Keeps this EP's per-person seeds clear of Core's per-tile salts (which are small integers).
        private const int PersonSaltBase = 0x5057_0000;

        // Wealth bands per SES tier, in the same silver-ish units and at the same boundaries Core's
        // SocioeconomicRules classifies with (200 / 600 / 1500). The Affluent ceiling is open in Core; 4000
        // is the top of what its per-tech base wealth reaches.
        private static readonly int[] WealthFloor = { 40, 200, 600, 1500 };
        private static readonly int[] WealthCeiling = { 199, 599, 1499, 4000 };

        /// <summary>The seed of person <paramref name="index"/> on <paramref name="tile"/>. Distinct per
        /// (tile, index), never zero, deterministic across machines.</summary>
        public static uint PersonSeed(int worldSeed, int tile, int index)
            => DemographicsRules.TileSeed(worldSeed, tile, unchecked(PersonSaltBase + index));

        /// <summary>
        /// Materialize one person. A null profile is treated as <see cref="RegionProfile.Empty"/>; any missing
        /// or all-zero share array falls to its default (working-age, illiterate, subsistence, agriculture,
        /// no race/faction/ideoligion) so a sparse profile still yields a valid individual.
        /// </summary>
        public static Individual Sample(int worldSeed, int tile, int index, RegionProfile profile)
        {
            profile = profile ?? RegionProfile.Empty();
            uint rng = PersonSeed(worldSeed, tile, index);

            var p = new Individual { tile = tile, index = index };

            // 1. sex
            p.female = DemographicsRules.NextFloat(ref rng) < profile.femaleFraction;

            // 2. age: bucket from the pyramid, then a year uniformly inside that bucket's band
            int bucket = DemographicsRules.WeightedPick(ref rng, profile.ageShares);
            p.ageBucket = bucket < 0 ? AgeBucket.WorkingAge : (AgeBucket)bucket;
            p.age = AgeWithin(ref rng, p.ageBucket, profile.elderMaxAge);

            // 3-5. race, faction, ideoligion — catalogue keys, -1 when the region has none
            p.raceKey = PickKey(ref rng, profile.raceKeys, profile.raceWeights);
            p.factionKey = PickKey(ref rng, profile.factionKeys, profile.factionWeights);
            p.ideoKey = PickKey(ref rng, profile.ideoKeys, profile.ideoWeights);

            // 6. education
            int edu = DemographicsRules.WeightedPick(ref rng, profile.educationShares);
            p.education = edu < 0 ? EducationTier.Illiterate : (EducationTier)edu;

            // 7-8. class, then a wealth figure inside the class band
            int ses = DemographicsRules.WeightedPick(ref rng, profile.sesShares);
            p.ses = ses < 0 ? SesTier.Subsistence : (SesTier)ses;
            p.wealth = DemographicsRules.RangeInt(ref rng, WealthFloor[(int)p.ses], WealthCeiling[(int)p.ses]);

            // 9-10. work status follows age; sector only for the employed
            p.sector = OccupationSector.Agriculture;
            switch (p.ageBucket)
            {
                case AgeBucket.Child:
                    p.work = WorkStatus.Dependent;
                    break;
                case AgeBucket.Elder:
                    p.work = WorkStatus.Retired;
                    break;
                default:
                    bool employed = DemographicsRules.NextFloat(ref rng) * 100f < profile.employmentRate;
                    p.work = employed ? WorkStatus.Employed : WorkStatus.Unemployed;
                    if (employed)
                    {
                        int sector = DemographicsRules.WeightedPick(ref rng, profile.occupationShares);
                        if (sector >= 0) p.sector = (OccupationSector)sector;
                    }
                    break;
            }

            return p;
        }

        /// <summary>A year inside the bucket's band: children 0..12, working-age 13..64, elders 65..ceiling.
        /// Uses Core's band edges so the buckets mean the same thing here as in the region panel.</summary>
        public static int AgeWithin(ref uint rng, AgeBucket bucket, int elderMaxAge)
        {
            switch (bucket)
            {
                case AgeBucket.Child:
                    return DemographicsRules.RangeInt(ref rng, 0, AgeStructureRules.ChildMaxAge - 1);
                case AgeBucket.Elder:
                    int ceiling = elderMaxAge < AgeStructureRules.ElderMinAge ? AgeStructureRules.ElderMinAge : elderMaxAge;
                    return DemographicsRules.RangeInt(ref rng, AgeStructureRules.ElderMinAge, ceiling);
                default:
                    return DemographicsRules.RangeInt(ref rng, AgeStructureRules.ChildMaxAge, AgeStructureRules.ElderMinAge - 1);
            }
        }

        /// <summary>The wealth band a class samples from, for consumers that want to bucket by wealth.</summary>
        public static void WealthBand(SesTier tier, out int floor, out int ceiling)
        {
            floor = WealthFloor[(int)tier];
            ceiling = WealthCeiling[(int)tier];
        }

        // A weighted pick over a parallel keys/weights pair. Always consumes exactly one RNG draw when the
        // pair is usable, and none when it is not — so an empty pair does not shift later draws.
        private static int PickKey(ref uint rng, int[] keys, float[] weights)
        {
            if (!RegionProfile.HasWeights(keys, weights)) return -1;
            int i = DemographicsRules.WeightedPick(ref rng, weights);
            return i < 0 ? -1 : keys[i];
        }
    }
}
