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

## Storage: a SQLite database per world, with a lineage of saves (revised 2026-09-06)

The original plan kept the overlay inside the `.rws` and treated files as caches. The user's call for
0.1.0 is the reverse: **the database is the source of truth**, and the `.rws` carries a pointer plus a
recovery copy. The hazard that motivated the original plan — RimWorld saves are copied, renamed, and
branched out from under you — is handled by giving every save file its own place in a tree.

- **One database per world**: `Saves/PersistentWorld/census_<worldId>.db`, keyed by the world's
  persistent random value, so it follows the world through every save of it. SQLite ships inside the
  mod and self‑loads from `Natives/<rid>/`; the user installs nothing. If it cannot load, the mod runs
  in memory and the `.rws` copy carries the state — nothing is lost, only the analysis store.
- **A lineage of commits.** Every save file is a commit holding only what changed since its parent.
  Play writes into a *working* commit under the loaded save; saving seals it under the file's name and
  opens a child. An autosave, a manual save, and a branch the player loads from an older save each
  carry exactly their own diff and none can trample another. Loading resolves the effective overlay by
  walking the ancestry, nearest record wins; a tombstone record undoes an ancestor's.
- **Recovery.** The `.rws` keeps the world id, its commit id, its parent's id, and the packed overlay.
  If the database has no commit for the save (moved machine, deleted database, file from a backup),
  the packed copy seeds a root commit. The database wins whenever it has the commit.
- **Cleanup.** A save overwritten in game supersedes its old commit; a save deleted in the game's
  dialog is dropped at once; a save file that simply went missing is dropped after a grace period.
  A commit is never dropped while a surviving save's lineage passes through it, and a dead chain
  unwinds from its tip. Compaction of long chains is future work.
- **The census tables** hold the latest materialized planet (people, households, links, labels,
  regions, tiles, builds), replaced per build on the background thread, read back on load so queries
  have a planet before the first fresh build lands, and open to a read‑only SQL door for consumers and
  tuning. A CSV dump remains as a debug action.

### Schema v2: the tables mirror Core's locked demographic model (2026-09-06)

Core's `Design/DEMOGRAPHIC_MODEL.md` (0.4.0 keystone, topology locked) is the spec the history tables
follow: **xenotype cohorts are the unit, the region is a population‑weighted aggregate, the influence
graph steps once per demographic year, and every axis is tracked per cohort.** Core simulates that
model and emits the numbers; this EP records them and resolves them into people.

| Group | Tables | What they hold |
|---|---|---|
| Vocabulary | `factors`, `factor_tiers`, `factor_edges`, `sectors`, `cohort_kinds` | The influence graph (Stock / Flow / Mutable / Context / Derived, Scalar or Distribution, hysteresis constants, weighted edges with Linear / Saturating / Threshold curves and Stress / Ceiling / Suppress modes), the economic sector tree with labour tiers, and each xenotype's gene‑derived constants (inheritable, lifespan, drug dependency, fragility, fertility). Seeded from Core's templates, overwritten by a reflection‑guarded sync from Core's Defs once they ship; a patch's factor needs no migration. |
| Lineage | `commits`, `deltas` | One diff per save file (see above). `deltas` also carries strata, enslaved, partner, education and class so a branch can change more than home and death. |
| History | `region_years`, `cohort_years`, `factor_levels`, `flows`, `births_assigned`, `settlement_years`, `person_events` | Per demographic year: the region's shared stage, geo read (area, food capacity, carrying capacity, density) and aggregates; every per‑cohort axis and mechanism output (standing, slavery share, fight/flight, income / cost of living / assets, education and strata and class distributions, sex and gender, healthcare, housing, contentment, substance use, crime, the seven cause‑specific mortality hazards, life expectancy, infant mortality, leading cause, dependency); any factor's current/target in long format; births, deaths by cause, migration, combat, enslavement; which cohort the children of two cohorts join; what each settlement presented to its region; and **what happened to whom** — the person events (born, died, moved, enslaved, freed, partnered, separated, schooled, class, employed, unemployed, converted, linked). Every row carries the `save_id` it was written under, so a save's history is the union along its lineage and a collected commit takes its rows with it. |
| Census | `builds`, `people`, `households`, `links`, `labels`, `regions`, `tiles` | The latest materialized planet. `people` gained the model's per‑person mutable state: strata, enslaved, partner, parents, birth and death years, cause of death, income, cost of living, assets, contentment, substance use, healthcare. |

The write path into the history is the 0.3.0 work (#12 consumes Core's deltas as per‑person changes);
0.1.0 ships the tables, the vocabulary, the event API (`RecordEvent`, `HistoryOf`), and `MovePerson`
recording its own event. The demographic year is one in‑game year (sixty days).

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

**Individual identity (revised by #9).** A person's id is a 64‑bit mix of the world seed, birth tile
and birth index — opaque, unique, never changes. Birth index runs `0..birthsOnTile`. Home tile is a
mutable attribute; the dataset is grouped by home and indexed by id. Households (#7) are assembled
deterministically per *home* tile over its current residents from `ResidenceRules` occupancy, keyed
`(tile, residenceIndex)` with members as a contiguous run, so a household is stable across rebuilds
for as long as the tile's residents are.

**Game versions.** RimWorld 1.6 only, matching Core (which now uses 1.6‑only APIs).

**World‑Map‑Export as a consumer.** Deferred; not an issue in this milestone.

---

## Vision: persistent demographic history (settled 2026-09-05)

**The split.** Core provides the deltas; this expansion provides the persistence and the history. Core
keeps tracking the top‑level generic numbers per region — birth rate, growth, migration totals, stress,
territory change — and exposes hooks so that shift can be overridden; good enough for most players,
which is why it lives in Core. If a region's birth rate is low, Core still tracks the birth rate. What
this EP does is resolve those numbers into *people*: which xenotypes are growing and which are not,
because of who is connected to whom — who partners with whom, whose children inherit what, who left and
who arrived. A stationary list of every person, a record of what changed for whom, and answers that are
definite rather than probabilistic. Person 1600 either holds a doctorate or does not, and the answer
never changes unless history changes it.

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

**On SQLite.** Adopted in 0.1.0 as the authoritative store and the analysis engine (see *Storage*). The
in‑memory index stays the per‑tick hot path — the database is never read on the game loop — and the
SQL door is how consumers and the tuning loop ask the grand‑vision questions.

**Milestones.** `0.1.0 Deterministic Census` (the baseline, with pawn‑bound identity), `0.2.0 Conditional
Model` (lookup tables, relationships), `0.3.0 Demographic History` (consume Core deltas, migration,
history log, hooks, aggregation flip).
