# Behaviour tests

Suites that run without RimWorld, Unity, Harmony, or a game install. Mirrors `Core-MMF/Tests`.

```
Tests/run-tests.sh
```

Uses mono's `mcs` when present, otherwise the .NET SDK (`dotnet`). Exit code is zero only if every
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

Not covered, and not pretended to be: anything that needs a live world. Save/load round trips, the
background rebuild cadence, and whether Core's reflection targets still resolve are in-game checks
via the dev-mode debug dump.

## Layout note

`Tests/` is a sibling of `Source/`, and the csproj lives in `Source/`. SDK-style projects glob
`**/*.cs` relative to the project directory, so nothing here is picked up by the mod build. Do not
move this folder under `Source/`.

The stubs are not a mock framework. They are the smallest real types that make the callers compile,
written from the actual RimWorld signatures the code meets.
