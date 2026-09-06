# Persistent World — Expansion Pack (design)

**Status:** in development — milestone `0.1.0 Deterministic Census` (branch `release/0.1.0`). See *Decisions for 0.1.0* at the end.
**Series:** Regions and Societies expansion pack (EP), alongside `World-Map-Export-EP`.
**Depends on:** Regions and Societies Core (either edition — MMF or RP2), for its region
demographic aggregates. No hard `modDependency` (the two Core editions are mutually exclusive);
warn in‑game if no Core is present, mirroring the other EPs.
**Proposed packageId:** `RegionsAndSocieties.PersistentWorld`

---

## Purpose

Give the planet a full, queryable population — every person, not just per‑region aggregates — that
supports four uses at once:

1. **Query / analytics** — "how many postgrad imperial women over 40 in desert regions", counts and
   breakdowns for overlays, panels, exports, and other mods.
2. **Named individuals on demand** — pull a specific person (a settler, a household head) with real
   attributes when something needs one (e.g. the Core `#28` pawn‑generation hook).
3. **Households / residences** — the residence model (`ResidenceRules`, occupancy 1–7) as actual
   families: who lives with whom.
4. **Persistent individuals with history** — specific people who persist, age, move, and are
   remembered across saves.

The hard part is doing all four **without** turning the save into a multi‑megabyte per‑pawn table,
without an O(population) tick cost, and without breaking reproducibility. The design below does that.

---

## Core principle: derive the majority, store only the tracked few

Core's demographics are **derived**, not stored: a region's age pyramid, xenotype mix, education,
etc. are regenerated deterministically from `seed + faction + biome`, and nothing is written to the
save. This EP keeps that bet and extends it one level down, to individuals.

- **Derived majority (99.9%).** An individual is a **pure deterministic function of `(tile, index,
  region demographics)`** — a seeded sampler. Person #37 of tile 8421 is the same age/sex/xenotype/
  education/occupation on every machine, every load, computed on demand, stored nowhere. This covers
  use cases 1–3 for free.
- **Sparse tracked overlay (the few).** Only individuals that **earn** history — the promotion rule
  below — are stored as thin records. This is the *only* growing data, and it is authoritative.

This mirrors Core's existing `RegionDemographicsStress`: *"the baseline is deterministic … only
deliberate changes are stored."* Same idea, from region deltas to individual deltas.

A "SQL‑style breakdown" is then a **view**: the derived rows for everyone, unioned with the thin
tracked table. You query it as one dataset; you store almost nothing; the world still reproduces
from seed.

---

## Asynchronous materialization

Materializing the whole planet on the game loop would cost O(population) per tick. Instead, build it
on a background thread, on a cadence, and swap the result in atomically.

**Snapshot → compute → swap (the only thread‑safe shape in RimWorld):**

1. **Snapshot (main thread, cheap).** Copy an immutable snapshot of what the worker needs: per‑region
   aggregates (population, distributions) and the sparse tracked overlay — just numbers, ~hundreds of
   regions. Never hand the worker `Find` / `World` / `Pawn` / anything Verse.
2. **Compute (background `Task`).** The worker runs the pure deterministic sampler over the snapshot
   to materialize the full in‑memory dataset (+ households). Reads only the snapshot; touches no game
   state. This is where the O(population) work lives, off the game loop.
3. **Swap (main thread, atomic).** When the build completes, publish it as the new current dataset
   (double‑buffered). Queries always read the last **completed** build, never a half‑built one. If a
   rebuild is already running when the cadence fires, skip.

**Cadence:** every couple of in‑game days, plus an on‑request trigger (so a consumer never reads a
stale dataset right after an event). No hitch even at 100k+ people.

**Determinism:** the derived majority is reproducible from seed; only the sparse tracked overlay
carries genuine state.

---

## Storage: two per‑save faces, one source of truth

Each save needs its own data. It splits into two, and only one must be managed:

- **Authoritative persistence rides *inside* the `.rws`.** The sparse tracked‑individuals overlay is a
  scribed `WorldComponent` in this EP, so it travels with the save automatically — rename, copy,
  cloud‑sync, move between machines, and its tracked people come along. Tiny. No sidecar to keep in
  sync.
- **The CSV/JSON is a per‑save *derived export* (a rebuildable cache), not the source of truth.** It is
  the full materialized view (derived majority + scribed overlay), written as a sidecar keyed to the
  save (a stable game id), regenerated on the cadence and on load. A missing or stale file is a
  non‑event: the worker rebuilds it.

**Gotcha — never make the sidecar authoritative.** RimWorld saves are renamed, copied, and deleted
out from under you; a file that *is* the data desyncs the moment a save is copied. Truth in the
`.rws`, file as cache, sidesteps all of it.

---

## Query surface

- **In‑memory indexed store** — the worker builds it; the game queries it live (overlays, panels,
  other mods, a debug dump). Pure C#, cross‑platform, fast, no file I/O on the query path. **This is
  the query engine.**
- **CSV/JSON** — that same store serialized to disk, for external tools and reload. You never query
  the file in‑game; you query the in‑memory index the file was written from. (This corrects the false
  either/or: file format ≠ query capability.)

No SQLite / native dependency — pure‑C# index plus flat‑file export keeps it cross‑platform.

---

## Promotion rule (what makes a person "tracked")

The one knob that grows the save. Default proposal, tunable:

- A person becomes **tracked** when the player **directly interacts** with them (recruited, met,
  named by an event, promoted as a household head the game surfaced).
- Everything else stays derived.

Cost is therefore bounded to what the player actually touches, not planet population.

---

## Open questions (settled or deferred — see *Decisions for 0.1.0* below)

- Exact promotion triggers and any decay ("un‑track" someone the player never sees again?).
- Query API shape: a public static surface other mods call, a debug dump, an in‑game panel — or all.
- Household assembly: deterministic per‑residence sampling from `ResidenceRules` occupancy, and how
  household membership is keyed so it's stable across rebuilds.
- Whether the World‑Map‑Export EP consumes this dataset as a layer once it exists.

## Relationship to Core

- Reads Core's `RegionDemographicsUtility` aggregates and `ResidenceRules` — does not duplicate them.
- Feeds/aligns with Core `#28` (pawn‑generation hook: pawns inherit their home region's demographics)
  — this EP is the general "materialize an individual from the aggregate" machinery `#28` is a
  special case of.
- Same reflection‑guarded, no‑op‑without‑a‑consumer endpoint discipline as `TerritoryClaimHooks` /
  `PopulationDynamics`.

---

## Decisions for 0.1.0 (settled 2026-09-05)

**Scope.** All seven design issues ship in `0.1.0 Deterministic Census` (#1 scaffold, #2 sampler, #3 async
loop, #4 world‑pawn linkage, #5 query index, #6 export sidecar, #7 households). The focus is **raw
population numbers**: individuals are placeholders — an index slot per tile with sampled demographics,
no name or personality. Tunable, relational individuals are `0.2.0 Conditional Model`; movement and history are `0.3.0 Demographic History`.

**Promotion rule → 0.2.0 (#8).** Not needed for a derived‑only population. What #4 delivers in 0.1.0 is
the *linkage*: every existing Verse world pawn with a home tile is bound to one derived index slot on
that tile, so queries can tell a `Pawn`‑backed row from a derived one, and a linked slot reports the
real pawn's age/sex/xenotype. Cost is O(world pawns), never O(population).

**Query API shape.** A public static class other mods call (reflection‑friendly, no‑op without a
consumer) plus a dev‑mode debug action that dumps counts and breakdowns to the log. No in‑game panel
in 0.1.0.

**Tests.** A dependency‑free `Tests/` harness mirroring Core's (hand‑written RimWorld stubs,
`run-tests.sh`). The sampler, index, and household assembly are pure C# and get determinism and
distribution‑fidelity assertions; anything that needs a live world is an in‑game check.

**Individual identity.** Keyed `(tile, index)` as designed; `index` runs `0..populationAtTile`.
Households (#7) are assembled deterministically per tile from `ResidenceRules` occupancy, keyed
`(tile, residenceIndex)` with members as a contiguous run of index slots, so a household is stable
across rebuilds for as long as the tile's population is.

**Game versions.** RimWorld 1.6 only, matching Core (which now uses 1.6‑only APIs).

**World‑Map‑Export as a consumer.** Deferred; not an issue in this milestone.

---

## Vision: persistent demographic history (settled 2026-09-05)

**The split.** Core provides the deltas; this expansion provides the persistence and the history. Core
shifts the regional numbers over time (growth, migration passes, stress, territory change) and exposes
hooks so that shift can be overridden — good enough for most players, which is why it lives in Core. This
EP turns the shift into a *true* one: a stationary list of every person, a record of what changed for
whom, and answers that are definite rather than probabilistic. Person 1600 either holds a doctorate or
does not, and the answer never changes unless history changes it.

**Identity is pawn‑bound, never territory‑bound.** A person's id is a 64‑bit mix of the world seed, the
birth tile, and the birth index. It is opaque, unique per world, and never changes. Where they live is a
mutable attribute. This is the critical layer: without it nobody can relocate, and relationships and
history have nothing to hang on.

**Baseline plus overlay, state not history.** The baseline is the deterministic birth list, derived from
seed and never stored. The overlay is one sparse record per person whose state differs from birth — home
tile, alive, household, later class and relationships. A person who never changed costs nothing; a person
who moved five times costs one record. Storage is bounded by how many people ever changed, never by
elapsed time, and there is no replay. The overlay rides inside the `.rws`, packed. The history — the
sequence of deltas — goes to the sidecar on disk, for analysis and tuning, never into the save.

**Dynamics are filter, sample, commit.** A pass runs off‑thread over the materialized view: select
candidates by filter, score destinations, sample who changes with each person's own seeded stream, emit
a delta set. The main thread commits it to the overlay; the next build folds it in. The same snapshot →
compute → swap shape as the build itself. Rules: displacement from war; nearest‑first with hard
ideology/xenotype barriers; cost of living and housing as soft costs; brain drain and recruitment as
education‑ and sector‑conditional attraction.

**Lookup tables are the model.** Each attribute is drawn from a small table conditional on what was
already drawn (education given sex and age, class given education, partner's xenotype given own). The
tables are the tuning knobs; the CSV and the crosstab dump are how a knob's effect is read.

**On SQLite.** The in‑memory index already gives the SQL‑shaped operation this needs — apply a
calculation to every person matching a filter — in milliseconds at 100k people, with the save as the
persistence. A SQLite‑backed store can sit behind the same overlay interface later for the full
grand‑vision analysis; it is not on the in‑game path.

**Milestones.** `0.1.0 Deterministic Census` (the baseline, with pawn‑bound identity), `0.2.0 Conditional
Model` (lookup tables, relationships), `0.3.0 Demographic History` (consume Core deltas, migration,
history log, hooks, aggregation flip).
