using System;
using System.Collections.Generic;

namespace RegionsAndSocieties.PersistentWorld.Population
{
    /// <summary>A real pawn that wants to be a person in the census: who it is, where it lives, what it is.</summary>
    public struct LinkCandidate
    {
        public int pawnId;      // Verse thingIDNumber — stable for the life of the pawn and unique per save
        public int homeTile;    // the tile the pawn lives on now
        public bool female;
        public int age;         // biological years
        public int raceKey;     // catalogue keys; -1 = none
        public int factionKey;
        public int ideoKey;
    }

    /// <summary>A person who is a real pawn, carried in the snapshot so the build can put the pawn's actual
    /// attributes on them. Birth coordinates let the build find the baseline row without a lookup.</summary>
    public struct LinkedPerson
    {
        public long id;
        public int birthTile;
        public int birthIndex;
        public int pawnId;
        public bool female;
        public int age;
        public int raceKey;
        public int factionKey;
        public int ideoKey;
    }

    /// <summary>One scribed link: which person a pawn is.</summary>
    public struct PawnLink
    {
        public int pawnId;
        public long id;
        public int birthTile;
        public int birthIndex;
    }

    /// <summary>
    /// The world-pawn linkage rule (#4, on pawn-bound identity #9): every real world pawn with a home is
    /// one specific person, chosen once from the people born on the tile where the pawn was first seen and
    /// kept for the life of the pawn. When the pawn is somewhere else than its person's home, the reconcile
    /// moves the person there through the overlay — the first real movement in the census. Pure: pawns
    /// arrive as <see cref="LinkCandidate"/>s, births per tile and the overlay are handed in.
    ///
    /// <para><b>Stability.</b> A link survives as long as the pawn is still a candidate and its person's
    /// birth slot still exists. A new pawn takes the birth slot its id hashes to on its home tile, probing
    /// forward past slots other pawns hold, so links do not depend on enumeration order; candidates are
    /// processed in pawn-id order for the same reason. Cost is O(world pawns), never O(population).</para>
    /// </summary>
    public sealed class WorldPawnLinks
    {
        private readonly Dictionary<int, PawnLink> byPawn = new Dictionary<int, PawnLink>();

        public int Count => byPawn.Count;

        /// <summary>The person a pawn is, if it is linked.</summary>
        public bool TryGet(int pawnId, out PawnLink link) => byPawn.TryGetValue(pawnId, out link);

        /// <summary>Every link, in pawn-id order (for scribing and the debug dump).</summary>
        public List<PawnLink> Records()
        {
            var list = new List<PawnLink>(byPawn.Values);
            list.Sort((a, b) => a.pawnId.CompareTo(b.pawnId));
            return list;
        }

        /// <summary>Replace the table wholesale (from a loaded save). Duplicate pawn ids keep the last record.</summary>
        public void Load(IEnumerable<PawnLink> records)
        {
            byPawn.Clear();
            if (records == null) return;
            foreach (PawnLink r in records) if (r.id != 0) byPawn[r.pawnId] = r;
        }

        /// <summary>
        /// Bring the table in line with the current candidates: keep links that still hold, link new pawns to
        /// a person born where they live, drop pawns that are gone, and move each linked person to where
        /// its pawn is now (through <paramref name="overlay"/>). Returns the linked people for the snapshot,
        /// sorted by id. <paramref name="birthsOn"/> is how many people are born on a tile; a candidate on a
        /// tile with no births stays unlinked. <paramref name="worldSeed"/> makes the ids.
        /// </summary>
        public LinkedPerson[] Reconcile(int worldSeed, IList<LinkCandidate> candidates, Func<int, int> birthsOn, PopulationOverlay overlay)
        {
            birthsOn = birthsOn ?? (_ => 0);
            var sorted = new List<LinkCandidate>(candidates ?? Array.Empty<LinkCandidate>());
            sorted.Sort((a, b) => a.pawnId.CompareTo(b.pawnId));

            // Pass 1: which existing links still hold? Their people stay taken.
            var taken = new HashSet<long>();
            var keep = new Dictionary<int, PawnLink>();
            foreach (LinkCandidate c in sorted)
            {
                if (!byPawn.TryGetValue(c.pawnId, out PawnLink old)) continue;
                if (old.birthIndex < 0 || old.birthIndex >= birthsOn(old.birthTile)) continue;
                if (old.id != PersonId.Make(worldSeed, old.birthTile, old.birthIndex)) continue;   // another world's link
                keep[c.pawnId] = old;
                taken.Add(old.id);
            }

            // Pass 2: place everyone, then make sure each person lives where its pawn is.
            var result = new List<LinkedPerson>(sorted.Count);
            var dropped = new List<PawnLink>();
            foreach (PawnLink old in byPawn.Values) if (!keep.ContainsKey(old.pawnId)) dropped.Add(old);
            byPawn.Clear();
            foreach (LinkCandidate c in sorted)
            {
                if (!keep.TryGetValue(c.pawnId, out PawnLink link))
                {
                    int births = birthsOn(c.homeTile);
                    if (births <= 0) continue;
                    int index = FindFree(worldSeed, c.pawnId, c.homeTile, births, taken, out long id);
                    if (index < 0) continue;   // every person born here is already a pawn — leave unlinked, never evict
                    link = new PawnLink { pawnId = c.pawnId, id = id, birthTile = c.homeTile, birthIndex = index };
                    taken.Add(id);
                }
                byPawn[c.pawnId] = link;
                overlay?.Move(link.id, link.birthTile, link.birthIndex, c.homeTile);
                result.Add(new LinkedPerson
                {
                    id = link.id, birthTile = link.birthTile, birthIndex = link.birthIndex, pawnId = c.pawnId,
                    female = c.female, age = c.age, raceKey = c.raceKey, factionKey = c.factionKey, ideoKey = c.ideoKey,
                });
            }

            // A person whose pawn is gone stays where the pawn left them; nothing to undo.
            result.Sort((a, b) => a.id.CompareTo(b.id));
            return result.ToArray();
        }

        /// <summary>The birth slot a pawn id prefers on a tile with <paramref name="births"/> people: a hash,
        /// so it is spread across the tile and independent of the order pawns were seen in.</summary>
        public static int PreferredIndex(int pawnId, int tile, int births)
        {
            if (births <= 0) return -1;
            unchecked
            {
                uint h = (uint)pawnId * 0x9E3779B1u;
                h ^= (uint)tile * 0x85EBCA77u;
                h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12;
                return (int)(h % (uint)births);
            }
        }

        private static int FindFree(int worldSeed, int pawnId, int tile, int births, HashSet<long> taken, out long id)
        {
            int start = PreferredIndex(pawnId, tile, births);
            for (int n = 0; n < births; n++)
            {
                int index = (start + n) % births;
                id = PersonId.Make(worldSeed, tile, index);
                if (!taken.Contains(id)) return index;
            }
            id = 0;
            return -1;
        }
    }
}
