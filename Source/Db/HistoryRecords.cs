namespace RegionsAndSocieties.PersistentWorld.Db
{
    /// <summary>A factor of the influence graph (model §2). Mirrors Core's <c>DemographicFactorDef</c>.</summary>
    public struct FactorRow
    {
        public string defName, label, category, kind, tiersFrom, source;
        public float velocity, inertia, deadband, releaseBand, maxStep;
        public bool perCohort;
        public string[] tiers;
    }

    /// <summary>One weighted edge into a factor (model §2).</summary>
    public struct EdgeRow
    {
        public string factor, source, sourceTier, targetTier, curve, mode, spill;
        public float weight; public float? threshold; public int lagYears;
    }

    /// <summary>One node of the economic sector tree. Mirrors Core's <c>EconomicSectorDef</c>.</summary>
    public struct SectorRow
    {
        public string defName, parent, label, requiresDlc, source;
        public int laborTier;
    }

    /// <summary>A xenotype cohort's constants, read from its genes (model §1).</summary>
    public struct CohortKindRow
    {
        public int key; public string defName, label;
        public bool inheritable; public float lifespan, dependency, fragility, fertility;
    }

    /// <summary>The region's shared stage, geo read and aggregates for one demographic year (model §1, §6).</summary>
    public struct RegionYear
    {
        public int year, tick, region;
        public float pop, carryingCapacity, foodCapacity, areaKm2, density, foodSelfSuff;
        public float wealth, urbanisation, employment, health, crime, contentment, substanceUse, housing, dependency;
        public float devLevel, sanitation, pollution, conflict, roads, rigidity, slaveryStance, tolerance, natalism;
        public float balance, inequality, drugBurdenAgg, migrationAgg, birthAgg, growth;
        public float secAgriculture, secExtraction, secManufacturing, secServices, secMilitary, secPublic;
    }

    /// <summary>Everything one cohort is in one demographic year (model §1, §3–§8).</summary>
    public struct CohortYear
    {
        public int year, tick, region, cohort;
        public float pop, share, standing, basePreference, drugBurden, fertility, birthRate, birthMult, migrationNet, fight, flight;
        public float wealth, eduIndex, slaveShare, income, costLiving, netDaily, assets; public string incomeSource;
        public float eduIlliterate, eduPrimary, eduSecondary, eduUndergrad, eduPostgrad;
        public float strataElite, strataMiddle, strataUnderclass;
        public float sesSubsistence, sesModest, sesProsperous, sesAffluent;
        public float femaleFrac, genderDiverse, ageChild, ageWorking, ageElder, dependency;
        public float employment, healthcare, housing, contentment, substanceUse, crime, mortality;
        public float hzOldAge, hzMalnutrition, hzDisease, hzViolence, hzWar, hzAddiction, hzXenophobia;
        public float lifeExpectancy, infantMortality; public string leadingCause;
    }

    /// <summary>A factor's level for one region/cohort/year (cohort -1 = region level; tier "" = scalar).</summary>
    public struct FactorLevel
    {
        public int year, region, cohort; public string factor, tier; public float current, target; public bool moving;
    }

    /// <summary>The flows of one cohort in one year (model §2 taxonomy, deaths by cause per §4).</summary>
    public struct FlowRow
    {
        public int year, region, cohort;
        public float birthsGross, birthsEffective, deaths, dOldAge, dMalnutrition, dDisease, dViolence, dWar, dAddiction, dXenophobia;
        public float immigration, emigration, combatLosses, enslaved, freed;
    }

    /// <summary>Which cohort the children of two cohorts join (model §8).</summary>
    public struct BirthAssignment
    {
        public int year, region, motherCohort, fatherCohort, childCohort; public float count;
    }

    /// <summary>What a settlement presented to its region (the inbound hook, model §9).</summary>
    public struct SettlementYear
    {
        public int year, tick, region, tile; public string kind;
        public float headcount, wealth, education; public string sectorOutput; public int ideo; public string xenotypes;
    }

    /// <summary>The kinds of thing that can happen to a person.</summary>
    public static class EventKind
    {
        public const string Born = "born", Died = "died", Moved = "moved", Enslaved = "enslaved", Freed = "freed",
            Partnered = "partnered", Separated = "separated", Schooled = "schooled", Class = "class",
            Employed = "employed", Unemployed = "unemployed", Converted = "converted", Linked = "linked";
    }

    /// <summary>One thing that happened to one person: the unit of persistent demographic history.</summary>
    public struct PersonEvent
    {
        public long seq;            // assigned by the store
        public int year, tick, region;
        public long person;
        public string kind, cause;
        public long fromValue, toValue;
    }
}
