# Behaviour tests

Suites that run without RimWorld, Unity, Harmony, or a game install. Mirrors `Core-MMF/Tests`.

```
Tests/run-tests.sh
```

Uses mono's `mcs` when present, otherwise the .NET SDK (`dotnet`). The sampler suite compiles Core's
pure Demographics rules from the sibling `../Core-MMF` checkout (override with `CORE_MMF_SRC`), so the
EP reuses Core's enums and RNG instead of duplicating them. Exit code is zero only if every
suite builds and every assertion holds.

## Why this can exist at all

The rules layers of this EP are dependency-free by design: the deterministic individual sampler, the
in-memory index, and household assembly are pure C# that receive world state as plain numbers or
`Func` delegates. Exactly one façade file per subsystem touches the game. That separation is what
lets the rules compile against the hand-written doubles in `RimWorldStubs.cs` and run anywhere. It is
also what makes the async materialization safe: the background worker only ever sees the same plain
snapshot the tests feed in.

## What is and is not covered

| Suite | Covers |
|---|---|
| `CorePresenceTests` | the either-edition Core guard the whole EP gates on |
| `IndividualSamplerTests` | the deterministic individual sampler: same slot, same person; bands; distribution fidelity over 40k draws; sparse-profile defaults; stable draw order |
| `MaterializationTests` | snapshot → compute → swap: a build is exactly the declared people, tile by tile and person for person; the loop refuses concurrent starts, publishes only completed builds, keeps the previous dataset through a cancel, swaps atomically |
| `LinkageTests` | world-pawn linkage: one slot per pawn on its home tile, no sharing, stable across reconciles and arrival order, re-homed when a tile or slot vanishes, never evicts; the build overlays the pawn's real attributes on its slot only |
| `QueryIndexTests` | the query index: every marginal sums to the population, region runs hold exactly their region, every filter's count / breakdown / select agrees with a brute-force scan, fast paths match the slow one, empty and null datasets answer zero |
| `ExportTests` | the sidecar: JSON round-trips person for person with tiles, regions, labels and links; CSV shape; untrusted input (wrong schema, torn, mismatched) reads as null; safe file names; a restored dataset is adopted only before the first build |
| `HouseholdTests` | households: sizes sum to the tile, stay 1..7, number Core's residences, are deterministic and vary by tile; hamlets big, cities small; every person in exactly one contiguous run; heads are the oldest adult; index, filters and export see household size |
| `IdentityTests` | pawn-bound identity: ids are stable, unique over 200k, opaque and hex round-trippable; the overlay stores state not history, packs exactly, drops identity records; a moved person is found by id under the new home with birth attributes intact, a dead person is gone, a stale delta is ignored; links move their people and come home; ids survive the export |
| `DbTests` | the database: schema creates and re-ensures; the save lineage resolves nearest-record-wins along a chain, honours tombstones, keeps branches apart, supersedes on overwrite, observes missing files and collects only what nothing can load; census tables round-trip a dataset; the SQL door reads and refuses writes. Runs over a temp file with the stock driver bundle, dotnet only |

Not covered, and not pretended to be: anything that needs a live world. Save/load round trips, the
background rebuild cadence, the mod's own native SQLite loader (the suites use the driver's stock
bundle), the save-dialog hooks, and whether Core's reflection targets still resolve are in-game checks
via the dev-mode actions (population dump, database status).

## Layout note

`Tests/` is a sibling of `Source/`, and the csproj lives in `Source/`. SDK-style projects glob
`**/*.cs` relative to the project directory, so nothing here is picked up by the mod build. Do not
move this folder under `Source/`.

The stubs are not a mock framework. They are the smallest real types that make the callers compile,
written from the actual RimWorld signatures the code meets.
