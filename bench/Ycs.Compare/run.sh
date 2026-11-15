#!/usr/bin/env bash
# Reruns the whole comparison: fetches and checks the editing trace, builds in Release, runs the suite
# (3 passes, every workload in a child process per library), the Native AOT probe and the wasm probe.
# Results go to results/<UTC date>/ (or $1); what the checks read goes to $WORK. SUITE_ARGS adds suite options, e.g.
# to run ycs only and carry YDotNet over from an earlier run (see Program.cs):
#   SUITE_ARGS="--carry-yrs results/2026-11-15 --carry-yrs-work <its work dir> --yrs-controls text-append,map-set-distinct" ./run.sh
set -euo pipefail
cd "$(dirname "$0")"
OUT="${1:-results/$(date -u +%F)}"
WORK="${WORK:-${TMPDIR:-/tmp}/ycs-compare-work}"
TRACE="${TRACE:-${TMPDIR:-/tmp}/automerge-paper.json.gz}"
[ -f "$TRACE" ] || ../Ycs.Benchmarks/get-trace.sh "$TRACE"
echo "42c0ff1720c0cad77077e190e36135c9cab9f7bbc266351c0a7fe4981c6a1546  $TRACE" | sha256sum -c -
mkdir -p "$OUT"
dotnet build -c Release
bin/Release/net11.0/Ycs.Compare suite --trace "$TRACE" --out "$OUT" --work "$WORK" --passes "${PASSES:-3}" --runs "${RUNS:-5}" ${SUITE_ARGS:-} 2> >(tee "$OUT/suite.log" >&2)
aot/measure.sh "$WORK/aot" | tee "$OUT/aot.txt"
wasm/probe.sh "$WORK/wasm" | tee "$OUT/wasm.txt"
