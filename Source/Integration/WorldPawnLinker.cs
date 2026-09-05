using System.Collections.Generic;
using RegionsAndSocieties.PersistentWorld.Population;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RegionsAndSocieties.PersistentWorld.Integration
{
    /// <summary>
    /// The main-thread façade for world-pawn linkage (#4): turns the game's world pawns into plain
    /// <see cref="LinkCandidate"/>s and reconciles the scribed <see cref="WorldPawnLinks"/> table against
    /// them right before a snapshot is taken. Which pawns count, and where they live:
    /// <list type="bullet">
    /// <item>Living humanlike pawns in <c>WorldPawns</c> — faction leaders, quest and event pawns, people
    /// the player has met and let go. Player-faction pawns are skipped: the colony is the player's, not
    /// part of the modeled planet.</item>
    /// <item>Home tile: the pawn's own tile if it is somewhere populated (a settlement it sits in);
    /// otherwise its faction's most populous settlement; otherwise it has no home and stays unlinked.</item>
    /// </list>
    /// </summary>
    public static class WorldPawnLinker
    {
        /// <summary>Collect the current candidates. Empty when there is no world.</summary>
        public static List<LinkCandidate> Candidates(PopulationCatalogue catalogue)
        {
            var list = new List<LinkCandidate>();
            if (Find.World == null || Find.WorldPawns == null) return list;

            var factionHome = new Dictionary<Faction, int>();
            foreach (Pawn pawn in Find.WorldPawns.AllPawnsAlive)
            {
                if (pawn == null || pawn.Dead || pawn.RaceProps == null || !pawn.RaceProps.Humanlike) continue;
                Faction faction = pawn.Faction;
                if (faction != null && faction.IsPlayer) continue;

                int home = HomeTileOf(pawn, faction, factionHome);
                if (home < 0) continue;

                list.Add(new LinkCandidate
                {
                    pawnId = pawn.thingIDNumber,
                    homeTile = home,
                    female = pawn.gender == Gender.Female,
                    age = pawn.ageTracker?.AgeBiologicalYears ?? 0,
                    raceKey = ModsConfig.BiotechActive ? catalogue.KeyOf(pawn.genes?.Xenotype) : -1,
                    factionKey = catalogue.KeyOf(faction),
                    ideoKey = ModsConfig.IdeologyActive ? catalogue.KeyOf(pawn.Ideo) : -1,
                });
            }
            return list;
        }

        /// <summary>The head count of a world tile, for the reconcile step.</summary>
        public static int PopulationOf(int tile) => PopulationDensityUtility.GetSourcePopulationAtTile(tile);

        private static int HomeTileOf(Pawn pawn, Faction faction, Dictionary<Faction, int> factionHome)
        {
            PlanetTile own = pawn.Tile;
            if (own.Valid && PopulationOf(own.tileId) > 0) return own.tileId;
            if (faction == null) return -1;

            if (!factionHome.TryGetValue(faction, out int home))
            {
                home = -1; int best = 0;
                List<Settlement> settlements = Find.WorldObjects?.Settlements;
                if (settlements != null)
                    foreach (Settlement s in settlements)
                    {
                        if (s?.Faction != faction || !s.Tile.Valid) continue;
                        int pop = PopulationOf(s.Tile.tileId);
                        // Most populous wins; ties break on the lower tile id so the choice is stable.
                        if (pop > best || (pop == best && pop > 0 && s.Tile.tileId < home)) { best = pop; home = s.Tile.tileId; }
                    }
                factionHome[faction] = home;
            }
            return home;
        }
    }

    /// <summary>The scribed form of one <see cref="PawnSlot"/>: pawn id, tile, index. Three ints per linked pawn.</summary>
    public class WorldPawnLinkRecord : IExposable
    {
        public int pawnId;
        public int tile;
        public int index;

        public WorldPawnLinkRecord() { }
        public WorldPawnLinkRecord(PawnSlot slot) { pawnId = slot.pawnId; tile = slot.tile; index = slot.index; }

        public PawnSlot ToSlot() => new PawnSlot { pawnId = pawnId, tile = tile, index = index };

        public void ExposeData()
        {
            Scribe_Values.Look(ref pawnId, "pawn", 0);
            Scribe_Values.Look(ref tile, "tile", -1);
            Scribe_Values.Look(ref index, "index", -1);
        }
    }
}
