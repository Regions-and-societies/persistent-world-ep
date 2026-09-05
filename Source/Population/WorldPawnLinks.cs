using System;
using System.Collections.Generic;

namespace RegionsAndSocieties.PersistentWorld.Population
{
    /// <summary>A real pawn that wants a slot in the derived population: who it is, where it lives, what it is.</summary>
    public struct LinkCandidate
    {
        public int pawnId;      // Verse thingIDNumber — stable for the life of the pawn and unique per save
        public int homeTile;    // the populated tile the pawn belongs to
        public bool female;
        public int age;         // biological years
        public int raceKey;     // catalogue keys; -1 = none
        public int factionKey;
        public int ideoKey;
    }

    /// <summary>A slot that is backed by a real pawn, carried in the snapshot so the build can overlay the
    /// pawn's actual attributes on the derived person there.</summary>
    public struct LinkedPerson
    {
        public int pawnId;
        public int tile;
        public int index;
        public bool female;
        public int age;
        public int raceKey;
        public int factionKey;
        public int ideoKey;
    }

    /// <summary>One scribed record: which slot a pawn holds. The only state this feature saves.</summary>
    public struct PawnSlot
    {
        public int pawnId;
        public int tile;
        public int index;
    }

    /// <summary>
    /// The world-pawn linkage rule (#4, 0.1.0 scope): every real world pawn with a home tile is bound to
    /// one derived index slot on that tile, so a query can tell a <c>Pawn</c>-backed row from a derived
    /// one and the row reports the pawn's real sex, age and xenotype. Pure: pawns arrive as
    /// <see cref="LinkCandidate"/>s, the table is plain records, and the population of a tile is a
    /// delegate — so the rule runs and is tested without a game.
    ///
    /// <para><b>Stability.</b> A link survives as long as the pawn is still a candidate on the same tile and
    /// its slot still exists (index &lt; tile population). A new pawn takes the slot its id hashes to,
    /// probing forward past occupied slots, so links do not depend on enumeration order. Candidates are
    /// processed in pawn-id order for the same reason. Cost is O(world pawns), never O(population).</para>
    /// </summary>
    public sealed class WorldPawnLinks
    {
        private readonly Dictionary<int, PawnSlot> byPawn = new Dictionary<int, PawnSlot>();

        public int Count => byPawn.Count;

        /// <summary>The slot a pawn holds, if it is linked.</summary>
        public bool TryGet(int pawnId, out PawnSlot slot) => byPawn.TryGetValue(pawnId, out slot);

        /// <summary>Every link, in pawn-id order (for scribing and the debug dump).</summary>
        public List<PawnSlot> Records()
        {
            var list = new List<PawnSlot>(byPawn.Values);
            list.Sort((a, b) => a.pawnId.CompareTo(b.pawnId));
            return list;
        }

        /// <summary>Replace the table wholesale (from a loaded save). Duplicate pawn ids keep the last record.</summary>
        public void Load(IEnumerable<PawnSlot> records)
        {
            byPawn.Clear();
            if (records == null) return;
            foreach (PawnSlot r in records) byPawn[r.pawnId] = r;
        }

        /// <summary>
        /// Bring the table in line with the current candidates: keep links that are still valid, re-home
        /// pawns whose tile or slot went away, link new pawns, drop pawns that are gone. Returns the linked
        /// people for the snapshot, sorted by (tile, index). <paramref name="populationOf"/> is the head
        /// count of a tile; a candidate whose home tile has nobody stays unlinked.
        /// </summary>
        public LinkedPerson[] Reconcile(IList<LinkCandidate> candidates, Func<int, int> populationOf)
        {
            populationOf = populationOf ?? (_ => 0);
            var sorted = new List<LinkCandidate>(candidates ?? Array.Empty<LinkCandidate>());
            sorted.Sort((a, b) => a.pawnId.CompareTo(b.pawnId));

            // Pass 1: which existing links still hold? Occupancy is per tile, keyed by slot.
            var occupied = new HashSet<long>();
            var keep = new Dictionary<int, PawnSlot>();
            foreach (LinkCandidate c in sorted)
            {
                if (!byPawn.TryGetValue(c.pawnId, out PawnSlot old)) continue;
                if (old.tile != c.homeTile || old.index < 0 || old.index >= populationOf(old.tile)) continue;
                keep[c.pawnId] = old;
                occupied.Add(Key(old.tile, old.index));
            }

            // Pass 2: place everyone else.
            var result = new List<LinkedPerson>(sorted.Count);
            byPawn.Clear();
            foreach (LinkCandidate c in sorted)
            {
                if (!keep.TryGetValue(c.pawnId, out PawnSlot slot))
                {
                    int pop = populationOf(c.homeTile);
                    if (pop <= 0) continue;
                    int index = FindFree(c.pawnId, c.homeTile, pop, occupied);
                    if (index < 0) continue;   // tile full of linked pawns — leave unlinked rather than evict
                    slot = new PawnSlot { pawnId = c.pawnId, tile = c.homeTile, index = index };
                    occupied.Add(Key(slot.tile, slot.index));
                }
                byPawn[c.pawnId] = slot;
                result.Add(new LinkedPerson
                {
                    pawnId = c.pawnId, tile = slot.tile, index = slot.index,
                    female = c.female, age = c.age, raceKey = c.raceKey, factionKey = c.factionKey, ideoKey = c.ideoKey,
                });
            }

            result.Sort((a, b) => a.tile != b.tile ? a.tile.CompareTo(b.tile) : a.index.CompareTo(b.index));
            return result.ToArray();
        }

        /// <summary>The slot a pawn id prefers on a tile of <paramref name="population"/>: a hash, so it is
        /// spread across the tile and independent of the order pawns were seen in.</summary>
        public static int PreferredIndex(int pawnId, int tile, int population)
        {
            if (population <= 0) return -1;
            unchecked
            {
                uint h = (uint)pawnId * 0x9E3779B1u;
                h ^= (uint)tile * 0x85EBCA77u;
                h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12;
                return (int)(h % (uint)population);
            }
        }

        private static int FindFree(int pawnId, int tile, int population, HashSet<long> occupied)
        {
            int start = PreferredIndex(pawnId, tile, population);
            for (int n = 0; n < population; n++)
            {
                int index = (start + n) % population;
                if (!occupied.Contains(Key(tile, index))) return index;
            }
            return -1;
        }

        private static long Key(int tile, int index) => ((long)tile << 32) | (uint)index;
    }
}
