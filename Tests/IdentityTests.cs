// Behaviour tests for pawn-bound identity and the sparse overlay (#9): ids are stable, unique and opaque;
// the overlay stores state not history, packs and unpacks exactly, and drops identity records; a moved
// person appears under the new home with the same id and attributes; a dead person leaves the list; a
// world-pawn link moves its person to where the pawn is; and the id index finds everyone. Pure.
using System;
using System.Collections.Generic;
using System.IO;
using RegionsAndSocieties.PersistentWorld.Population;

namespace IdentityTests
{
    public static class Program
    {
        private static int failures;
        private const int Seed = 90210;

        public static int Main()
        {
            Section("ids");
            long a = PersonId.Make(Seed, 400, 7);
            Check("stable", a == PersonId.Make(Seed, 400, 7) && a != 0);
            Check("differs by index, tile and seed", a != PersonId.Make(Seed, 400, 8) && a != PersonId.Make(Seed, 401, 7) && a != PersonId.Make(Seed + 1, 400, 7));
            var seen = new HashSet<long>();
            bool unique = true;
            for (int t = 0; t < 400 && unique; t++) for (int i = 0; i < 500; i++) unique &= seen.Add(PersonId.Make(Seed, t * 37, i));
            Check("200k ids across 400 tiles collide nowhere", unique);
            Check("hex round trip", PersonId.TryParseHex(PersonId.ToHex(a), out long hexBack) && hexBack == a && PersonId.ToHex(a).Length == 16);
            Check("negative ids round trip too", PersonId.TryParseHex(PersonId.ToHex(-5L), out long neg) && neg == -5L);
            Check("bad hex is refused", !PersonId.TryParseHex("zz", out _) && !PersonId.TryParseHex(null, out _) && !PersonId.TryParseHex("", out _) && !PersonId.TryParseHex("12345678901234567", out _));
            Check("the sampler stamps identity and home = birth", IndividualSampler.Sample(Seed, 400, 7, null).id == a && IndividualSampler.Sample(Seed, 400, 7, null).birthTile == 400 && IndividualSampler.Sample(Seed, 400, 7, null).tile == 400 && !IndividualSampler.Sample(Seed, 400, 7, null).Moved);

            Section("overlay is state, not history");
            var ov = new PopulationOverlay();
            Check("empty", ov.Count == 0 && ov.HomeOf(a, 400) == 400);
            Check("move records once", ov.Move(a, 400, 7, 900) && ov.Count == 1 && ov.HomeOf(a, 400) == 900);
            Check("moving again replaces, never appends", ov.Move(a, 400, 7, 950) && ov.Move(a, 400, 7, 960) && ov.Count == 1 && ov.HomeOf(a, 400) == 960);
            Check("moving to the same place is not a change", !ov.Move(a, 400, 7, 960));
            Check("moving home drops the record", ov.Move(a, 400, 7, 400) && ov.Count == 0);
            ov.MarkDead(a, 400, 7);
            Check("dead is a record even at home", ov.Count == 1 && ov.TryGet(a, out PersonDelta dd) && dd.Dead && !dd.Moved);
            ov.Move(a, 400, 7, 500);
            Check("dead and moved coexist", ov.TryGet(a, out PersonDelta dm) && dm.Dead && dm.homeTile == 500);
            Check("identity records are dropped on Set", Set(ov, new PersonDelta { id = 77, birthTile = 1, birthIndex = 0, homeTile = 1 }) && !ov.TryGet(77, out _));
            Check("id zero is ignored", !ov.Move(0, 1, 0, 2) && ov.Count == 1);

            Section("overlay packs exactly");
            var many = new PopulationOverlay();
            for (int i = 0; i < 1000; i++) many.Move(PersonId.Make(Seed, i % 13, i), i % 13, i, 5000 + i % 7);
            for (int i = 0; i < 100; i++) many.MarkDead(PersonId.Make(Seed, 99, i), 99, i);
            byte[] bytes = many.ToBytes();
            Check("size is records x packed size", bytes.Length == many.Count * PersonDelta.PackedSize && many.Count == 1100);
            var loaded = new PopulationOverlay();
            Check("load returns the count", loaded.Load(bytes) == 1100 && loaded.Count == 1100);
            bool same = true;
            foreach (PersonDelta d in many.Records()) same &= loaded.TryGet(d.id, out PersonDelta e) && e.birthTile == d.birthTile && e.birthIndex == d.birthIndex && e.homeTile == d.homeTile && e.flags == d.flags;
            Check("every record round-trips", same);
            Check("records come out sorted by id", Sorted(many.Records()));
            Check("a torn buffer loads nothing", new PopulationOverlay().Load(new byte[bytes.Length - 1]) == 0 && new PopulationOverlay().Load(null) == 0 && new PopulationOverlay().Load(Array.Empty<byte>()) == 0);

            Section("the build honours the overlay");
            RegionProfile[] profiles = { new RegionProfile { femaleFraction = 0.5f, ageShares = new[] { 0.2f, 0.6f, 0.2f }, raceKeys = new[] { 0 }, raceWeights = new[] { 1f } } };
            var rows = new List<TileSlot> { new TileSlot { tile = 400, region = 0, population = 100 }, new TileSlot { tile = 800, region = 0, population = 30 } };
            PopulationDataset baseline = PopulationBuilder.Build(PopulationSnapshot.From(Seed, rows, new[] { 77 }, profiles));
            Check("baseline: everyone lives where born", baseline.Count == 130 && baseline.MovedCount == 0 && baseline.TileCount == 2);

            var overlay = new PopulationOverlay();
            long mover = PersonId.Make(Seed, 400, 7), toEmpty = PersonId.Make(Seed, 400, 8), dying = PersonId.Make(Seed, 800, 3), stale = PersonId.Make(Seed, 800, 999);
            overlay.Move(mover, 400, 7, 800);
            overlay.Move(toEmpty, 400, 8, 12345);          // a tile with no births at all
            overlay.MarkDead(dying, 800, 3);
            overlay.Move(stale, 800, 999, 400);             // birth slot does not exist: ignored
            PopulationDataset ds = PopulationBuilder.Build(PopulationSnapshot.From(Seed, rows, new[] { 77 }, profiles, deltas: overlay.Records()));
            baseline.TryGetById(mover, out Individual before);
            Check("count: one dead, nobody else lost", ds.Count == 129);
            Check("the mover is found by id under the new home", ds.TryGetById(mover, out Individual after) && after.tile == 800 && after.birthTile == 400 && after.birthIndex == 7 && after.Moved);
            Check("same person: birth attributes unchanged", after.age == before.age && after.female == before.female && after.education == before.education && after.wealth == before.wealth && after.raceKey == before.raceKey);
            Check("the mover is a resident of 800, not 400", ds.TryTileRange(800, out int s800, out int c800) && c800 == 30 && Contains(ds, s800, c800, mover) && ds.TryTileRange(400, out int s400, out int c400) && c400 == 98 && !Contains(ds, s400, c400, mover));
            Check("a home tile with no births still gets a group", ds.TryTileRange(12345, out int sE, out int cE) && cE == 1 && ds.people[sE].id == toEmpty && ds.TileCount == 3 && ds.RegionOfTile(12345) == -1);
            Check("the dead are gone", !ds.TryGetById(dying, out _) && ds.PositionOf(dying) == -1);
            Check("a stale delta is ignored", ds.TryGetById(PersonId.Make(Seed, 400, 0), out _) && ds.Count == 129);
            Check("region and query follow the home", ds.RegionOf(in after) == 77 && PopulationQuery.Count(ds, new PopulationFilter { tile = 800 }) == 30 && PopulationQuery.Count(ds, new PopulationFilter { birthTile = 400 }) == 100 && PopulationQuery.Count(ds, new PopulationFilter { moved = 1 }) == 2);
            Check("households are per home tile over residents", ds.HouseholdsOnTile(800) == HouseholdRules.Count(30) && ds.HouseholdsOnTile(400) == HouseholdRules.Count(98) && ds.HouseholdsOnTile(12345) == 1 && after.household >= 0);
            Check("TryGetBorn finds a moved person", ds.TryGetBorn(400, 7, out Individual born) && born.id == mover && born.tile == 800);
            Check("residents of a tile are in birth order", ds.TryGetResident(800, 0, out Individual r0) && ds.TryGetResident(800, 29, out Individual r29) && r0.birthIndex < r29.birthIndex);
            bool indexOk = true;
            for (int i = 0; i < ds.Count; i++) indexOk &= ds.PositionOf(ds.people[i].id) == i;
            Check("the id index finds everyone at their position", indexOk);

            Section("links move people");
            var links = new WorldPawnLinks();
            var ov2 = new PopulationOverlay();
            Func<int, int> births = t => t == 400 ? 100 : t == 800 ? 30 : 0;
            LinkedPerson[] linked = links.Reconcile(Seed, new[] { Cand(1, 400), Cand(2, 800), Cand(3, 5) }, births, ov2);
            Check("pawns on tiles with births are linked; a pawn where nobody is born is not", linked.Length == 2 && links.Count == 2 && !links.TryGet(3, out _));
            Check("a linked person is born where the pawn was first seen", links.TryGet(1, out PawnLink l1) && l1.birthTile == 400 && l1.id == PersonId.Make(Seed, 400, l1.birthIndex));
            Check("no overlay records while pawns are home", ov2.Count == 0);
            links.Reconcile(Seed, new[] { Cand(1, 800), Cand(2, 800) }, births, ov2);
            Check("a pawn that travelled moves its person", ov2.Count == 1 && ov2.HomeOf(l1.id, 400) == 800 && links.TryGet(1, out PawnLink l1b) && l1b.id == l1.id);
            PopulationDataset withLinks = PopulationBuilder.Build(PopulationSnapshot.From(Seed, rows, new[] { 77 }, profiles, deltas: ov2.Records(), linked: links.Reconcile(Seed, new[] { Cand(1, 800), Cand(2, 800) }, births, ov2)));
            Check("the build shows the person at the pawn's tile with the pawn's attributes", withLinks.TryGetById(l1.id, out Individual lp) && lp.tile == 800 && lp.pawnId == 1 && lp.age == 41 && lp.female && withLinks.LinkedCount == 2);
            links.Reconcile(Seed, new[] { Cand(1, 400), Cand(2, 800) }, births, ov2);
            Check("coming home drops the overlay record", ov2.Count == 0);
            Check("stable across reconciles", links.TryGet(1, out PawnLink l1c) && l1c.id == l1.id);
            Check("a link from another world is dropped", Reload(links, Seed + 1) == 0);

            Section("export carries ids");
            var w = new StringWriter(); PopulationExport.WriteJson(ds, w);
            PopulationDataset back = PopulationExport.ReadJson(new StringReader(w.ToString()));
            Check("round trip keeps ids, births and homes", back != null && back.Count == ds.Count && back.TryGetById(mover, out Individual bm) && bm.tile == 800 && bm.birthTile == 400 && bm.birthIndex == 7);
            Check("a row whose id does not match its birth is refused", PopulationExport.ReadJson(new StringReader(w.ToString().Replace(PersonId.ToHex(mover), PersonId.ToHex(mover ^ 1)))) == null);

            Console.WriteLine(failures == 0 ? "identity: all checks passed" : $"identity: {failures} FAILED");
            return failures == 0 ? 0 : 1;
        }

        private static LinkCandidate Cand(int id, int tile) => new LinkCandidate { pawnId = id, homeTile = tile, female = true, age = 41, raceKey = -1, factionKey = -1, ideoKey = -1 };

        private static bool Set(PopulationOverlay ov, PersonDelta d) { ov.Set(d); return true; }

        private static bool Contains(PopulationDataset ds, int start, int count, long id)
        {
            for (int i = start; i < start + count; i++) if (ds.people[i].id == id) return true;
            return false;
        }

        private static bool Sorted(PersonDelta[] a) { for (int i = 1; i < a.Length; i++) if (a[i].id <= a[i - 1].id) return false; return true; }

        private static int Reload(WorldPawnLinks links, int otherSeed)
        {
            var copy = new WorldPawnLinks();
            copy.Load(links.Records());
            copy.Reconcile(otherSeed, new[] { Cand(1, 400), Cand(2, 800) }, t => t == 400 ? 100 : 30, null);
            // Under another seed the stored ids do not match their birth coordinates, so both are re-linked
            // fresh; the test asks how many kept their OLD id.
            int kept = 0;
            foreach (PawnLink old in links.Records()) if (copy.TryGet(old.pawnId, out PawnLink now) && now.id == old.id) kept++;
            return kept;
        }

        private static void Section(string name) => Console.WriteLine("-- " + name);

        private static void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + name);
            if (!ok) failures++;
        }
    }
}
