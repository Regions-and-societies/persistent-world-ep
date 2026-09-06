// Behaviour tests for the deterministic individual sampler (#2): the same (seed, tile, index, profile)
// must always yield the same person; different slots must differ; every attribute must land inside its
// band; and over many draws the sampled population must reproduce the region's distributions.
// Pure, so it runs without a game — compiled against Core's own pure Demographics rules.
using System;
using RegionsAndSocieties.Demographics;
using RegionsAndSocieties.PersistentWorld.Population;

namespace IndividualSamplerTests
{
    public static class Program
    {
        private static int failures;
        private const int Seed = 123456789;

        public static int Main()
        {
            RegionProfile city = City();

            Section("determinism");
            Individual a = IndividualSampler.Sample(Seed, 8421, 37, city);
            Individual b = IndividualSampler.Sample(Seed, 8421, 37, city);
            Check("same inputs, same person", Same(a, b));
            Check("identity is stamped", a.id == PersonId.Make(Seed, 8421, 37) && a.tile == 8421 && a.birthTile == 8421 && a.birthIndex == 37);
            Check("a different index is (almost surely) a different person", Differs(a, IndividualSampler.Sample(Seed, 8421, 38, city)));
            Check("a different tile is (almost surely) a different person", Differs(a, IndividualSampler.Sample(Seed, 8422, 37, city)));
            Check("a different world seed is (almost surely) a different person", Differs(a, IndividualSampler.Sample(Seed + 1, 8421, 37, city)));
            Check("person seed is never zero", IndividualSampler.PersonSeed(0, 0, 0) != 0u);
            Check("person seeds differ across index", IndividualSampler.PersonSeed(Seed, 1, 0) != IndividualSampler.PersonSeed(Seed, 1, 1));
            Check("person seeds differ across tile", IndividualSampler.PersonSeed(Seed, 1, 0) != IndividualSampler.PersonSeed(Seed, 2, 0));

            Section("every attribute lands in its band");
            bool bands = true, work = true, keys = true;
            for (int i = 0; i < 5000; i++)
            {
                Individual p = IndividualSampler.Sample(Seed, 100, i, city);
                bands &= AgeInBucket(p.age, p.ageBucket, city.elderMaxAge);
                IndividualSampler.WealthBand(p.ses, out int lo, out int hi);
                bands &= p.wealth >= lo && p.wealth <= hi;
                bands &= (int)p.education >= 0 && (int)p.education < EducationRules.TierCount;
                bands &= (int)p.sector >= 0 && (int)p.sector < EmploymentRules.SectorCount;
                work &= p.ageBucket != AgeBucket.Child || p.work == WorkStatus.Dependent;
                work &= p.ageBucket != AgeBucket.Elder || p.work == WorkStatus.Retired;
                work &= p.ageBucket != AgeBucket.WorkingAge || p.work == WorkStatus.Employed || p.work == WorkStatus.Unemployed;
                keys &= Contains(city.raceKeys, p.raceKey) && Contains(city.factionKeys, p.factionKey) && Contains(city.ideoKeys, p.ideoKey);
            }
            Check("age inside bucket band, wealth inside class band, enums in range", bands);
            Check("children are dependents, elders retired, working-age employed or not", work);
            Check("race/faction/ideo keys come from the profile", keys);

            Section("the sampled population reproduces the region's distributions");
            const int N = 40000;
            int female = 0, employed = 0, workingAge = 0;
            var age = new int[AgeStructureRules.BucketCount];
            var edu = new int[EducationRules.TierCount];
            var ses = new int[SocioeconomicRules.TierCount];
            var sector = new int[EmploymentRules.SectorCount];
            var race = new int[3];
            for (int i = 0; i < N; i++)
            {
                Individual p = IndividualSampler.Sample(Seed, 200, i, city);
                if (p.female) female++;
                age[(int)p.ageBucket]++;
                edu[(int)p.education]++;
                ses[(int)p.ses]++;
                race[p.raceKey]++;
                if (p.ageBucket == AgeBucket.WorkingAge)
                {
                    workingAge++;
                    if (p.work == WorkStatus.Employed) { employed++; sector[(int)p.sector]++; }
                }
            }
            Check("female fraction", Near(female / (float)N, city.femaleFraction));
            Check("age pyramid", Near(age, N, city.ageShares));
            Check("education ladder", Near(edu, N, city.educationShares));
            Check("class mix", Near(ses, N, city.sesShares));
            Check("xenotype mix", Near(race, N, city.raceWeights));
            Check("employment rate among working-age", Near(employed / (float)workingAge, city.employmentRate / 100f));
            Check("sector split among the employed", Near(sector, employed, city.occupationShares));

            Section("a sparse or empty profile degrades to defaults, never throws");
            Individual d = IndividualSampler.Sample(Seed, 5, 0, null);
            Check("null profile is the empty profile", Same(d, IndividualSampler.Sample(Seed, 5, 0, RegionProfile.Empty())));
            Check("working-age by default", d.ageBucket == AgeBucket.WorkingAge && d.age >= AgeStructureRules.ChildMaxAge && d.age < AgeStructureRules.ElderMinAge);
            Check("illiterate, subsistence by default", d.education == EducationTier.Illiterate && d.ses == SesTier.Subsistence);
            Check("no race, faction or ideoligion", d.raceKey == -1 && d.factionKey == -1 && d.ideoKey == -1);
            Check("employed default sector is agriculture", d.work != WorkStatus.Employed || d.sector == OccupationSector.Agriculture);
            var lopsided = new RegionProfile { femaleFraction = 1f, ageShares = new[] { 0f, 0f, 1f }, employmentRate = 0, elderMaxAge = 20 };
            Individual e = IndividualSampler.Sample(Seed, 6, 0, lopsided);
            Check("all-female profile samples female", e.female);
            Check("all-elder profile samples an elder", e.ageBucket == AgeBucket.Elder && e.work == WorkStatus.Retired);
            Check("an elder ceiling below the elder floor is lifted to the floor", e.age == AgeStructureRules.ElderMinAge);
            var mismatched = new RegionProfile { raceKeys = new[] { 7 }, raceWeights = new[] { 0.5f, 0.5f } };
            Check("mismatched key/weight lengths read as no races", IndividualSampler.Sample(Seed, 7, 0, mismatched).raceKey == -1);

            Section("the draw order is stable: an absent optional draw does not shift the ones after it");
            var withRaces = City();
            var noRaces = City(); noRaces.raceKeys = null; noRaces.raceWeights = null;
            bool stable = true;
            for (int i = 0; i < 500; i++)
            {
                Individual x = IndividualSampler.Sample(Seed, 300, i, withRaces);
                Individual y = IndividualSampler.Sample(Seed, 300, i, noRaces);
                stable &= x.female == y.female && x.age == y.age && x.ageBucket == y.ageBucket;
            }
            Check("sex and age unchanged when the race draw is skipped", stable);

            Console.WriteLine(failures == 0 ? "sampler: all checks passed" : $"sampler: {failures} FAILED");
            return failures == 0 ? 0 : 1;
        }

        // A spacer-ish city: mixed xenotypes, two factions, one ideoligion, most people working.
        private static RegionProfile City() => new RegionProfile
        {
            femaleFraction = 0.48f,
            ageShares = new[] { 0.20f, 0.65f, 0.15f },
            educationShares = new[] { 0.05f, 0.15f, 0.40f, 0.30f, 0.10f },
            sesShares = new[] { 0.10f, 0.30f, 0.45f, 0.15f },
            occupationShares = new[] { 0.10f, 0.45f, 0.15f, 0.30f },
            employmentRate = 74,
            elderMaxAge = 100,
            raceKeys = new[] { 0, 1, 2 }, raceWeights = new[] { 0.70f, 0.20f, 0.10f },
            factionKeys = new[] { 0, 1 }, factionWeights = new[] { 0.9f, 0.1f },
            ideoKeys = new[] { 0 }, ideoWeights = new[] { 1f },
        };

        private static bool AgeInBucket(int age, AgeBucket bucket, int elderMax)
        {
            switch (bucket)
            {
                case AgeBucket.Child: return age >= 0 && age < AgeStructureRules.ChildMaxAge;
                case AgeBucket.Elder: return age >= AgeStructureRules.ElderMinAge && age <= elderMax;
                default: return age >= AgeStructureRules.ChildMaxAge && age < AgeStructureRules.ElderMinAge;
            }
        }

        private static bool Same(Individual a, Individual b)
            => a.id == b.id && a.tile == b.tile && a.birthIndex == b.birthIndex && a.female == b.female && a.age == b.age && a.ageBucket == b.ageBucket
            && a.education == b.education && a.ses == b.ses && a.wealth == b.wealth && a.work == b.work && a.sector == b.sector
            && a.raceKey == b.raceKey && a.factionKey == b.factionKey && a.ideoKey == b.ideoKey;

        // Two draws from different seeds share every attribute only by coincidence; wealth alone has ~900 values.
        private static bool Differs(Individual a, Individual b) => a.wealth != b.wealth || a.age != b.age || a.female != b.female;

        private static bool Contains(int[] keys, int key)
        {
            if (keys == null) return key == -1;
            foreach (int k in keys) if (k == key) return true;
            return false;
        }

        private const float Tolerance = 0.02f;
        private static bool Near(float actual, float expected) => Math.Abs(actual - expected) <= Tolerance;

        private static bool Near(int[] counts, int total, float[] shares)
        {
            if (total <= 0) return false;
            float sum = 0f; foreach (float s in shares) sum += s;
            for (int i = 0; i < shares.Length; i++)
                if (!Near(counts[i] / (float)total, shares[i] / sum)) return false;
            return true;
        }

        private static void Section(string name) => Console.WriteLine("-- " + name);

        private static void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + name);
            if (!ok) failures++;
        }
    }
}
