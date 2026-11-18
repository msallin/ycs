#!/usr/bin/env bash
# Builds the probe against an old commit and against the working tree, runs both on the same inputs and compares.
#   bench/Ycs.DecodeProbe/run.sh <old commit> [mode ...]      modes: corpus hostile yjs fuzz-v2 fuzz-gen (default: all)
# Env: SCRATCH=<dir> (default: a new temporary directory), KEEP=1 keeps the builds and both outputs (else only the
# outputs of a run that differs), VERBOSE=1 adds --verbose (every outcome on its own line, for the first difference).
set -euo pipefail

base=${1:?usage: run.sh <old commit> [mode ...]}
shift
root=$(git rev-parse --show-toplevel)
scratch=${SCRATCH:-$(mktemp -d)}
mkdir -p "$scratch"
extra=()
[ "${VERBOSE:-}" = 1 ] && extra=(--verbose)

# Both trees get the same probe sources (the working tree's) over their own src/Ycs: the project compiles against
# whichever library sits beside it.
prepare() { # <dir>
  mkdir -p "$1/bench/Ycs.DecodeProbe" "$1/bench/Ycs.Parity"
  cp "$root"/bench/Ycs.DecodeProbe/{Ycs.DecodeProbe.csproj,Program.cs} "$1/bench/Ycs.DecodeProbe/"
}

echo "== old: $base"
rm -rf "$scratch/old"
mkdir -p "$scratch/old"
git -C "$root" archive "$base" -- src tests/Ycs.Properties bench/Ycs.Parity/Rng.cs Directory.Build.props global.json .editorconfig LICENSE | tar -x -C "$scratch/old"
prepare "$scratch/old"

echo "== new: the working tree"
rm -rf "$scratch/new"
mkdir -p "$scratch/new"
(cd "$root" && tar -c --exclude=bin --exclude=obj src tests/Ycs.Properties bench/Ycs.Parity/Rng.cs Directory.Build.props global.json .editorconfig LICENSE) | tar -x -C "$scratch/new"
prepare "$scratch/new"

for side in old new; do
  dotnet build "$scratch/$side/bench/Ycs.DecodeProbe" -c Release -o "$scratch/$side/out" -v q -nologo -p:RunAnalyzers=false >"$scratch/$side-build.log" 2>&1 \
    || { cat "$scratch/$side-build.log"; exit 2; }
done

status=0
for side in old new; do
  echo "== run $side"
  start=$SECONDS
  # The fixtures (corpus, Hostile.cs, yjs-cases.tsv) come from the working tree for both sides.
  dotnet "$scratch/$side/out/Ycs.DecodeProbe.dll" "$root" "$@" "${extra[@]}" >"$scratch/$side.txt"
  echo "   $((SECONDS - start)) s, $(wc -l <"$scratch/$side.txt") lines"
done

if cmp "$scratch/old.txt" "$scratch/new.txt"; then
  echo "IDENTICAL: $(grep -m1 '^## summary' "$scratch/new.txt")"
else
  echo "DIFFERENT: first differences (rerun with VERBOSE=1 for every outcome; outputs in $scratch)"
  diff "$scratch/old.txt" "$scratch/new.txt" | head -20
  status=1
  KEEP=1
fi

if [ "${KEEP:-}" != 1 ]; then
  rm -rf "$scratch/old" "$scratch/new" "$scratch"/*.txt "$scratch"/*.log
  rmdir "$scratch" 2>/dev/null || true
else
  rm -rf "$scratch"/{old,new}/{bench,src,tests}/*/{obj,bin} "$scratch"/{old,new}/src/Ycs/{obj,bin} 2>/dev/null || true
fi
exit $status
