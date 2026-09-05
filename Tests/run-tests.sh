#!/usr/bin/env bash
# Runs every behaviour suite without RimWorld, Unity, Harmony or a game install.
#
# The suites exist because the rules layers are deliberately dependency-free: no Find, no Harmony, no
# Unity. That is what lets them be compiled against the hand-written doubles in RimWorldStubs.cs and
# run anywhere a C# compiler exists. Anything that genuinely needs the game is not tested here and is
# not pretended to be. Mirrors Core-MMF/Tests/run-tests.sh.
#
# Compiler: uses mono's mcs when present, otherwise falls back to the .NET SDK (dotnet), which is
# what Windows dev machines have. Same suites, same files, either way.
#
#   Ubuntu/WSL:  sudo apt-get install -y mono-mcs mono-runtime
#   Windows:     .NET SDK on PATH (dotnet)
#   Then:        Tests/run-tests.sh
#
# Exits non-zero if any suite fails to build or fails an assertion. Each binary is removed before
# its build so a compile failure can never be masked by a stale binary reporting a stale pass.
set -u

cd "$(dirname "$0")/.." || exit 1

SRC=Source
# Core's pure rules, reused (not duplicated) by the population layer. Override for a non-sibling checkout.
CORE_SRC="${CORE_MMF_SRC:-../Core-MMF/Source}"
if [ ! -f "$CORE_SRC/Demographics/DemographicsRules.cs" ]; then
    echo "Core-MMF sources not found at $CORE_SRC (set CORE_MMF_SRC); the sampler suite needs them." >&2
    exit 1
fi
OUT="${TMPDIR:-/tmp}/pw-tests"
mkdir -p "$OUT"

MCS_FLAGS="-langversion:latest -nowarn:0169,0414,0649,0219,0067"
failures=0

if command -v mcs > /dev/null 2>&1; then
    COMPILER=mcs
elif command -v dotnet > /dev/null 2>&1; then
    COMPILER=dotnet
else
    echo "Neither mcs (mono) nor dotnet (.NET SDK) is on PATH; cannot build the suites." >&2
    exit 1
fi

# Windows-native tooling wants C:/ paths, not /c/ ones; cygpath exists exactly where that matters.
winpath() { if command -v cygpath > /dev/null 2>&1; then cygpath -m "$1"; else echo "$1"; fi; }

# run_suite <name> <Exe|Library> <files...> — build with whichever compiler is available; run if Exe.
run_suite() {
    name=$1; kind=$2; shift 2

    if [ "$COMPILER" = mcs ]; then
        if [ "$kind" = Exe ]; then target=exe; binary="$OUT/$name.exe"; else target=library; binary="$OUT/$name.dll"; fi
        rm -f "$binary"
        if ! mcs -target:$target $MCS_FLAGS -out:"$binary" "$@"; then
            echo "BUILD FAILED: $name"; failures=$((failures + 1)); return
        fi
        if [ "$kind" = Exe ] && ! mono "$binary"; then failures=$((failures + 1)); fi
        return
    fi

    # dotnet path: emit a minimal csproj listing exactly the same files, then build (and run, if Exe).
    proj="$OUT/$name"; rm -rf "$proj"; mkdir -p "$proj"
    {
        echo '<Project Sdk="Microsoft.NET.Sdk">'
        echo "  <PropertyGroup>"
        echo "    <OutputType>$kind</OutputType>"
        echo "    <TargetFramework>net8.0</TargetFramework>"
        echo "    <Nullable>disable</Nullable>"
        echo "    <LangVersion>latest</LangVersion>"
        echo "    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>"
        echo "    <NoWarn>0169;0414;0649;0219;0067</NoWarn>"
        echo "    <AssemblyName>$name</AssemblyName>"
        echo "  </PropertyGroup>"
        echo "  <ItemGroup>"
        for f in "$@"; do echo "    <Compile Include=\"$(winpath "$PWD/$f")\" />"; done
        echo "  </ItemGroup>"
        echo '</Project>'
    } > "$proj/$name.csproj"

    if ! dotnet build "$proj/$name.csproj" -v q --nologo -c Release -o "$proj/bin" > "$proj/build.log" 2>&1; then
        echo "BUILD FAILED: $name"; tail -12 "$proj/build.log"; failures=$((failures + 1)); return
    fi
    if [ "$kind" = Exe ] && ! dotnet "$proj/bin/$name.dll"; then failures=$((failures + 1)); fi
}

# 0.1.0 scaffold (#1): the Core-presence rule the whole EP gates on. Pure, no game.
run_suite corepresence Exe \
    Tests/RimWorldStubs.cs Tests/CorePresenceTests.cs \
    $SRC/Integration/CorePresence.cs

# 0.1.0 sampler (#2): an individual as a pure function of (seed, tile, index, profile). Compiled against
# Core's own pure Demographics rules (enums, seed mixer, RNG, weighted pick) from the sibling checkout,
# the same files Core's harness runs alone -- so the EP never duplicates them.
run_suite sampler Exe \
    Tests/IndividualSamplerTests.cs \
    $SRC/Population/RegionProfile.cs $SRC/Population/Individual.cs $SRC/Population/IndividualSampler.cs \
    $CORE_SRC/Demographics/DemographicsRules.cs $CORE_SRC/Demographics/AgeStructureRules.cs \
    $CORE_SRC/Demographics/EducationRules.cs $CORE_SRC/Demographics/SocioeconomicRules.cs \
    $CORE_SRC/Demographics/EmploymentRules.cs

# The pure population layer as a whole: the sampler plus the snapshot / dataset / builder / loop (#3).
POPULATION_PURE="$SRC/Population/RegionProfile.cs $SRC/Population/Individual.cs $SRC/Population/IndividualSampler.cs \
    $SRC/Population/PopulationSnapshot.cs $SRC/Population/PopulationDataset.cs $SRC/Population/PopulationBuilder.cs \
    $SRC/Population/MaterializationLoop.cs $SRC/Population/WorldPawnLinks.cs \
    $SRC/Population/PopulationIndex.cs $SRC/Population/PopulationQuery.cs $SRC/Population/PopulationExport.cs"
CORE_RULES="$CORE_SRC/Demographics/DemographicsRules.cs $CORE_SRC/Demographics/AgeStructureRules.cs \
    $CORE_SRC/Demographics/EducationRules.cs $CORE_SRC/Demographics/SocioeconomicRules.cs \
    $CORE_SRC/Demographics/EmploymentRules.cs"

# 0.1.0 async materialization (#3): snapshot -> compute -> swap, double-buffered, cancel-safe. Pure.
run_suite materialization Exe \
    Tests/MaterializationTests.cs \
    $POPULATION_PURE $CORE_RULES

# 0.1.0 world-pawn linkage (#4): stable, order-independent slots for real pawns; the build overlays them.
run_suite linkage Exe \
    Tests/LinkageTests.cs \
    $POPULATION_PURE $CORE_RULES

# 0.1.0 query index (#5): marginals, region runs, and filtered counts/breakdowns against brute force.
run_suite queryindex Exe \
    Tests/QueryIndexTests.cs \
    $POPULATION_PURE $CORE_RULES

# 0.1.0 export sidecar (#6): JSON round trip, CSV shape, untrusted input, naming, restore-before-build.
run_suite export Exe \
    Tests/ExportTests.cs \
    $POPULATION_PURE $CORE_RULES

# --- suites for #7 households register here as they land ---

if [ "$failures" -ne 0 ]; then
    echo "$failures suite(s) failed"
    exit 1
fi
echo "all suites passed"
