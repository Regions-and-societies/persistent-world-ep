// Behaviour tests for world-pawn linkage (#4): every candidate with a populated home gets exactly one slot
// on that tile, no two pawns share a slot, links are stable across reconciles and independent of the order
// pawns arrive in, a pawn is re-homed when its tile or slot disappears, and the build overlays the pawn's
// real attributes on its slot while every other person stays derived. Pure, so it runs without a game.
using System;
using System.Collections.Generic;
using RegionsAndSocieties.Demographics;
using RegionsAndSocieties.PersistentWorld.Population;

namespace LinkageTests
{
    public static class Program
    {
        private static int failures;
        private const int Seed = 4242;

        public static int Main()
        {
            var pop = new Dictionary<int, int> { { 100, 50 }, { 200, 3 }, { 300, 0 } };
            Func<int, int> popOf = t => pop.TryGetValue(t, out int p) ? p : 0;

            Section("placement");
            var links = new WorldPawnLinks();
            LinkedPerson[] linked = links.Reconcile(new[] { C(11, 100), C(12, 100), C(13, 200), C(14, 300), C(15, 999) }, popOf);
            Check("pawns on populated tiles are linked, others are not", linked.Length == 3 && links.Count == 3);
            Check("each slot is inside its tile's population", All(linked, l => l.index >= 0 && l.index < popOf(l.tile)));
            Check("no two pawns share a slot", Distinct(linked));
            Check("result is sorted by tile then index", linked[0].tile <= linked[1].tile && linked[1].tile <= linked[2].tile);
            Check("a pawn on an empty tile is unlinked", !links.TryGet(14, out _) && !links.TryGet(15, out _));
            Check("records come back in pawn-id order", links.Records()[0].pawnId == 11 && links.Records()[2].pawnId == 13);

            Section("stability");
            links.TryGet(11, out PawnSlot s11); links.TryGet(12, out PawnSlot s12);
            links.Reconcile(new[] { C(12, 100), C(11, 100), C(13, 200) }, popOf);
            Check("same candidates in another order keep their slots", links.TryGet(11, out PawnSlot t11) && t11.index == s11.index && links.TryGet(12, out PawnSlot t12) && t12.index == s12.index);
            var fresh = new WorldPawnLinks();
            fresh.Reconcile(new[] { C(13, 200), C(12, 100), C(11, 100) }, popOf);
            Check("a fresh table places the same pawns in the same slots regardless of order", fresh.TryGet(11, out PawnSlot f11) && f11.index == s11.index && fresh.TryGet(12, out PawnSlot f12) && f12.index == s12.index);
            links.Reconcile(new[] { C(11, 100), C(13, 200) }, popOf);
            Check("a pawn that is gone is dropped", !links.TryGet(12, out _) && links.Count == 2);
            links.Reconcile(new[] { C(11, 100), C(12, 100), C(13, 200) }, popOf);
            Check("it gets the same slot back when it returns", links.TryGet(12, out PawnSlot r12) && r12.index == s12.index);

            Section("re-homing");
            links.Reconcile(new[] { C(11, 200), C(12, 100), C(13, 200) }, popOf);
            Check("a pawn that moved is placed on its new tile", links.TryGet(11, out PawnSlot m11) && m11.tile == 200 && m11.index < 3);
            pop[200] = 1;
            LinkedPerson[] shrunk = links.Reconcile(new[] { C(11, 200), C(12, 100), C(13, 200) }, popOf);
            Check("when a tile shrinks, at most its population stays linked there and nobody shares", Count(shrunk, l => l.tile == 200) == 1 && Distinct(shrunk));
            pop[200] = 3;

            Section("a full tile never evicts");
            var crowd = new WorldPawnLinks();
            LinkedPerson[] c = crowd.Reconcile(new[] { C(1, 200), C(2, 200), C(3, 200), C(4, 200), C(5, 200) }, popOf);
            Check("three slots, five pawns: exactly three linked", c.Length == 3 && Distinct(c) && All(c, l => l.index < 3));
            Check("the lowest pawn ids win (processing order is by id)", crowd.TryGet(1, out _) && crowd.TryGet(2, out _) && crowd.TryGet(3, out _) && !crowd.TryGet(5, out _));

            Section("load round trip");
            var copy = new WorldPawnLinks();
            copy.Load(links.Records());
            bool equal = copy.Count == links.Count && links.Count > 0;
            foreach (PawnSlot r in links.Records()) equal &= copy.TryGet(r.pawnId, out PawnSlot l) && l.tile == r.tile && l.index == r.index;
            Check("loaded table equals the source", equal);
            copy.Load(null);
            Check("loading null empties it", copy.Count == 0);

            Section("preferred index is a spread hash");
            var seen = new HashSet<int>();
            for (int id = 1; id <= 200; id++) seen.Add(WorldPawnLinks.PreferredIndex(id, 100, 50));
            Check("200 ids over 50 slots touch most slots", seen.Count >= 40);
            Check("empty tile has no preferred index", WorldPawnLinks.PreferredIndex(7, 100, 0) == -1);

            Section("the build overlays real attributes on linked slots only");
            var profile = new RegionProfile { femaleFraction = 0f, ageShares = new[] { 0f, 1f, 0f }, raceKeys = new[] { 0 }, raceWeights = new[] { 1f } };
            var rows = new List<TileSlot> { new TileSlot { tile = 100, region = 0, population = 50 }, new TileSlot { tile = 200, region = 0, population = 3 } };
            var overlay = new[]
            {
                new LinkedPerson { pawnId = 900, tile = 100, index = 7, female = true, age = 71, raceKey = 5, factionKey = 2, ideoKey = 1 },
                new LinkedPerson { pawnId = 901, tile = 200, index = 0, female = true, age = 6, raceKey = -1, factionKey = -1, ideoKey = -1 },
            };
            PopulationSnapshot snap = PopulationSnapshot.From(Seed, rows, new[] { 1 }, new[] { profile }, linked: overlay);
            PopulationDataset ds = PopulationBuilder.Build(snap);
            Check("linked slot carries the pawn id", ds.TryGet(100, 7, out Individual a) && a.pawnId == 900 && a.IsLinked);
            Check("linked slot reports the pawn's sex, age and keys", a.female && a.age == 71 && a.raceKey == 5 && a.factionKey == 2 && a.ideoKey == 1);
            Check("age bucket and work status follow the real age", a.ageBucket == AgeBucket.Elder && a.work == WorkStatus.Retired);
            Check("a linked child is a dependent", ds.TryGet(200, 0, out Individual b) && b.age == 6 && b.ageBucket == AgeBucket.Child && b.work == WorkStatus.Dependent);
            Check("everyone else is derived", ds.TryGet(100, 6, out Individual d) && !d.IsLinked && !d.female && d.raceKey == 0 && ds.TryGet(100, 8, out Individual e) && !e.IsLinked);
            Check("linked count is the overlay size", ds.LinkedCount == 2);
            Check("a link outside the tile's range is ignored", PopulationBuilder.Build(PopulationSnapshot.From(Seed, rows, new[] { 1 }, new[] { profile }, linked: new[] { new LinkedPerson { pawnId = 1, tile = 200, index = 3 }, new LinkedPerson { pawnId = 2, tile = 555, index = 0 } })).LinkedCount == 0);

            Console.WriteLine(failures == 0 ? "linkage: all checks passed" : $"linkage: {failures} FAILED");
            return failures == 0 ? 0 : 1;
        }

        private static LinkCandidate C(int id, int tile) => new LinkCandidate { pawnId = id, homeTile = tile, female = (id & 1) == 0, age = 30, raceKey = -1, factionKey = -1, ideoKey = -1 };

        private static bool Distinct(LinkedPerson[] ls)
        {
            var set = new HashSet<long>();
            foreach (LinkedPerson l in ls) if (!set.Add(((long)l.tile << 32) | (uint)l.index)) return false;
            return true;
        }

        private static bool All(LinkedPerson[] ls, Func<LinkedPerson, bool> f) { foreach (LinkedPerson l in ls) if (!f(l)) return false; return true; }
        private static int Count(LinkedPerson[] ls, Func<LinkedPerson, bool> f) { int n = 0; foreach (LinkedPerson l in ls) if (f(l)) n++; return n; }

        private static void Section(string name) => Console.WriteLine("-- " + name);

        private static void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + name);
            if (!ok) failures++;
        }
    }
}
