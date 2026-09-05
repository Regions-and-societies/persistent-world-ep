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

# --- suites for #2 sampler, #5 index, #7 households register here as they land ---

if [ "$failures" -ne 0 ]; then
    echo "$failures suite(s) failed"
    exit 1
fi
echo "all suites passed"
