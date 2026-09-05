using RegionsAndSocieties.Demographics;

namespace RegionsAndSocieties.PersistentWorld.Population
{
    /// <summary>Where a person stands in relation to the workforce.</summary>
    public enum WorkStatus
    {
        Dependent = 0,   // a child — not yet working
        Employed = 1,    // working-age, in formal employment; <see cref="Individual.sector"/> says where
        Unemployed = 2,  // working-age, outside formal employment
        Retired = 3      // an elder — past working age
    }

    /// <summary>
    /// One derived person: a plain value, computed on demand from <c>(world seed, tile, index)</c> and the
    /// region's <see cref="RegionProfile"/>, stored nowhere. Person <c>index</c> of <c>tile</c> is the same
    /// on every machine and every load. In 0.1.0 individuals are placeholders — a slot with sampled
    /// demographics, no name, no personality — which is exactly what population counts need.
    /// </summary>
    public struct Individual
    {
        public int tile;
        public int index;

        public bool female;
        public int age;                  // years
        public AgeBucket ageBucket;

        public EducationTier education;
        public SesTier ses;
        public int wealth;               // silver-ish, inside the SES tier's band

        public WorkStatus work;
        public OccupationSector sector;  // meaningful only when work == Employed

        public int raceKey;              // catalogue key; -1 = plain human / Biotech off
        public int factionKey;           // catalogue key; -1 = unowned
        public int ideoKey;              // catalogue key; -1 = none / Ideology off

        public int pawnId;               // Verse thingIDNumber of the real pawn backing this slot; 0 = derived

        /// <summary>True when a real world pawn holds this slot (#4); its sex, age and keys are the pawn's own.</summary>
        public bool IsLinked => pawnId != 0;

        /// <summary>A stable identity for this slot, unique per world: the tile and index packed together.</summary>
        public long Id => ((long)tile << 32) | (uint)index;
    }
}
