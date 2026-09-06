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
    /// One person: a plain value. Identity (<see cref="id"/>, birth tile, birth index) is fixed at birth
    /// and derived from the world seed; everything else is the person's birth state unless the overlay
    /// (#9) has changed it — <see cref="tile"/> is where they live <i>now</i>. Computed on demand, stored
    /// nowhere except as a sparse delta when it differs from birth. In 0.1.0 people are placeholders —
    /// sampled demographics, no name, no personality — which is exactly what population counts need.
    /// </summary>
    public struct Individual
    {
        public long id;                  // PersonId.Make(seed, birthTile, birthIndex); never changes
        public int birthTile;
        public int birthIndex;

        public int tile;                 // home: where this person lives now

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

        public int pawnId;               // Verse thingIDNumber of the real pawn backing this person; 0 = derived

        public int household;            // index of this person's household within their home tile (#7); -1 = none
        public int householdSize;        // people in that household, 1..HouseholdRules.MaxOccupancy

        /// <summary>True when a real world pawn is this person (#4); sex, age and keys are the pawn's own.</summary>
        public bool IsLinked => pawnId != 0;

        /// <summary>True when this person lives somewhere other than where they were born.</summary>
        public bool Moved => tile != birthTile;
    }
}
