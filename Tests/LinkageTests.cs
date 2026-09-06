// Behaviour tests for world-pawn linkage (#4 on pawn-bound identity #9): every candidate on a tile with
// births becomes exactly one person born there, no two pawns share a person, links are stable across
// reconciles and independent of arrival order, a pawn keeps its person when it travels (the person moves),
// a full tile never evicts, and the build overlays the pawn's real attributes on its person only. Pure.
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
            var births = new Dictionary<int, int> { { 100, 50 }, { 200, 3 }, { 300, 0 } };
            Func<int, int> birthsOn = t => births.TryGetValue(t, out int p) ? p : 0;

            Section("placement");
            var links = new WorldPawnLinks();
            var ov = new PopulationOverlay();
            LinkedPerson[] linked = links.Reconcile(Seed, new[] { C(11, 100), C(12, 100), C(13, 200), C(14, 300), C(15, 999) }, birthsOn, ov);
            Check("pawns on tiles with births are linked, others are not", linked.Length == 3 && links.Count == 3);
            Check("each person is born on the pawn's tile, inside its births", All(linked, l => l.birthIndex >= 0 && l.birthIndex < birthsOn(l.birthTile)) && links.TryGet(11, out PawnLink p11) && p11.birthTile == 100 && links.TryGet(13, out PawnLink p13) && p13.birthTile == 200);
            Check("ids are the person ids of those births", All(linked, l => l.id == PersonId.Make(Seed, l.birthTile, l.birthIndex)));
            Check("no two pawns share a person", Distinct(linked));
            Check("result is sorted by id", linked[0].id < linked[1].id && linked[1].id < linked[2].id);
            Check("a pawn where nobody is born is unlinked", !links.TryGet(14, out _) && !links.TryGet(15, out _));
            Check("records come back in pawn-id order", links.Records()[0].pawnId == 11 && links.Records()[2].pawnId == 13);
            Check("nobody moved", ov.Count == 0);

            Section("stability");
            links.TryGet(11, out PawnLink s11); links.TryGet(12, out PawnLink s12);
            links.Reconcile(Seed, new[] { C(12, 100), C(11, 100), C(13, 200) }, birthsOn, ov);
            Check("same candidates in another order keep their people", links.TryGet(11, out PawnLink t11) && t11.id == s11.id && links.TryGet(12, out PawnLink t12) && t12.id == s12.id);
            var fresh = new WorldPawnLinks();
            fresh.Reconcile(Seed, new[] { C(13, 200), C(12, 100), C(11, 100) }, birthsOn, null);
            Check("a fresh table links the same pawns to the same people regardless of order", fresh.TryGet(11, out PawnLink f11) && f11.id == s11.id && fresh.TryGet(12, out PawnLink f12) && f12.id == s12.id);
            links.Reconcile(Seed, new[] { C(11, 100), C(13, 200) }, birthsOn, ov);
            Check("a pawn that is gone is dropped", !links.TryGet(12, out _) && links.Count == 2);
            links.Reconcile(Seed, new[] { C(11, 100), C(12, 100), C(13, 200) }, birthsOn, ov);
            Check("it gets the same person back when it returns", links.TryGet(12, out PawnLink r12) && r12.id == s12.id);

            Section("travel");
            links.Reconcile(Seed, new[] { C(11, 200), C(12, 100), C(13, 200) }, birthsOn, ov);
            Check("a pawn that travelled keeps its person; the person moves", links.TryGet(11, out PawnLink m11) && m11.id == s11.id && m11.birthTile == 100 && ov.HomeOf(s11.id, 100) == 200 && ov.Count == 1);
            links.Reconcile(Seed, new[] { C(11, 300), C(12, 100), C(13, 200) }, birthsOn, ov);
            Check("even to a tile with no births", links.TryGet(11, out _) && ov.HomeOf(s11.id, 100) == 300);
            links.Reconcile(Seed, new[] { C(11, 100), C(12, 100), C(13, 200) }, birthsOn, ov);
            Check("coming home clears the record", ov.Count == 0);
            births[100] = 5;
            LinkedPerson[] shrunk = links.Reconcile(Seed, new[] { C(11, 100), C(12, 100), C(13, 200) }, birthsOn, ov);
            Check("when births shrink below a person's index the pawn is re-linked, and nobody shares", Distinct(shrunk) && All(shrunk, l => l.birthIndex < birthsOn(l.birthTile)));
            births[100] = 50;

            Section("a full tile never evicts");
            var crowd = new WorldPawnLinks();
            LinkedPerson[] c = crowd.Reconcile(Seed, new[] { C(1, 200), C(2, 200), C(3, 200), C(4, 200), C(5, 200) }, birthsOn, null);
            Check("three births, five pawns: exactly three linked", c.Length == 3 && Distinct(c) && All(c, l => l.birthIndex < 3));
            Check("the lowest pawn ids win (processing order is by id)", crowd.TryGet(1, out _) && crowd.TryGet(2, out _) && crowd.TryGet(3, out _) && !crowd.TryGet(5, out _));

            Section("load round trip");
            var copy = new WorldPawnLinks();
            copy.Load(links.Records());
            bool equal = copy.Count == links.Count && links.Count > 0;
            foreach (PawnLink r in links.Records()) equal &= copy.TryGet(r.pawnId, out PawnLink l) && l.id == r.id && l.birthTile == r.birthTile && l.birthIndex == r.birthIndex;
            Check("loaded table equals the source", equal);
            copy.Load(null);
            Check("loading null empties it", copy.Count == 0);

            Section("preferred index is a spread hash");
            var seen = new HashSet<int>();
            for (int id = 1; id <= 200; id++) seen.Add(WorldPawnLinks.PreferredIndex(id, 100, 50));
            Check("200 ids over 50 slots touch most slots", seen.Count >= 40);
            Check("no births, no preferred index", WorldPawnLinks.PreferredIndex(7, 100, 0) == -1);

            Section("the build overlays real attributes on linked people only");
            var profile = new RegionProfile { femaleFraction = 0f, ageShares = new[] { 0f, 1f, 0f }, raceKeys = new[] { 0 }, raceWeights = new[] { 1f } };
            var rows = new List<TileSlot> { new TileSlot { tile = 100, region = 0, population = 50 }, new TileSlot { tile = 200, region = 0, population = 3 } };
            long id7 = PersonId.Make(Seed, 100, 7), id0 = PersonId.Make(Seed, 200, 0);
            var overlay = new[]
            {
                new LinkedPerson { id = id7, birthTile = 100, birthIndex = 7, pawnId = 900, female = true, age = 71, raceKey = 5, factionKey = 2, ideoKey = 1 },
                new LinkedPerson { id = id0, birthTile = 200, birthIndex = 0, pawnId = 901, female = true, age = 6, raceKey = -1, factionKey = -1, ideoKey = -1 },
            };
            PopulationSnapshot snap = PopulationSnapshot.From(Seed, rows, new[] { 1 }, new[] { profile }, linked: overlay);
            PopulationDataset ds = PopulationBuilder.Build(snap);
            Check("linked person carries the pawn id", ds.TryGetById(id7, out Individual a) && a.pawnId == 900 && a.IsLinked);
            Check("linked person reports the pawn's sex, age and keys", a.female && a.age == 71 && a.raceKey == 5 && a.factionKey == 2 && a.ideoKey == 1);
            Check("age bucket and work status follow the real age", a.ageBucket == AgeBucket.Elder && a.work == WorkStatus.Retired);
            Check("a linked child is a dependent", ds.TryGetById(id0, out Individual b) && b.age == 6 && b.ageBucket == AgeBucket.Child && b.work == WorkStatus.Dependent);
            Check("everyone else is derived", ds.TryGetBorn(100, 6, out Individual d) && !d.IsLinked && !d.female && d.raceKey == 0 && ds.TryGetBorn(100, 8, out Individual e) && !e.IsLinked);
            Check("linked count is the overlay size", ds.LinkedCount == 2);
            Check("a link whose id does not match its birth is ignored", PopulationBuilder.Build(PopulationSnapshot.From(Seed, rows, new[] { 1 }, new[] { profile }, linked: new[] { new LinkedPerson { id = 5, birthTile = 200, birthIndex = 1, pawnId = 1 }, new LinkedPerson { id = PersonId.Make(Seed, 200, 3), birthTile = 200, birthIndex = 3, pawnId = 2 } })).LinkedCount == 0);

            Console.WriteLine(failures == 0 ? "linkage: all checks passed" : $"linkage: {failures} FAILED");
            return failures == 0 ? 0 : 1;
        }

        private static LinkCandidate C(int id, int tile) => new LinkCandidate { pawnId = id, homeTile = tile, female = (id & 1) == 0, age = 30, raceKey = -1, factionKey = -1, ideoKey = -1 };

        private static bool Distinct(LinkedPerson[] ls)
        {
            var set = new HashSet<long>();
            foreach (LinkedPerson l in ls) if (!set.Add(l.id)) return false;
            return true;
        }

        private static bool All(LinkedPerson[] ls, Func<LinkedPerson, bool> f) { foreach (LinkedPerson l in ls) if (!f(l)) return false; return true; }

        private static void Section(string name) => Console.WriteLine("-- " + name);

        private static void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + name);
            if (!ok) failures++;
        }
    }
}
