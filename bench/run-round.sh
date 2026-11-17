#!/usr/bin/env bash
# One benchmarking round over every bench of this repository, on one machine, in sequence, into one directory:
#   bench/run-round.sh <out> [<baseline commit>]
# Steps (each logged with its start, end, exit code and the load average in <out>/round.log):
#   compare     bench/Ycs.Compare/run.sh: ycs against YDotNet, the Native AOT and wasm probes
#   ab          bench/Ycs.Compare/ab.py 4 6: the ycs side of every workload, the baseline build against this one
#               (the baseline is built from `git archive <commit>`, default 044bb73, the last commit of items as objects)
#   benchmarks  bench/Ycs.Benchmarks: the editing trace, limits, sizes, perf
#   layout      bench/Ycs.Layout small: bytes per document
#   server      bench/server/run.sh: the sync server against the Node reference server
# Run it on a quiet machine: the numbers are only as good as the load (round.log and machine.txt say what it was).
# The CI workflow `bench.yml` runs it on a chosen runner and uploads <out>.
set -u
OUT=$(realpath -m "$1")
BASELINE=${2:-044bb73}
ROOT=$(cd "$(dirname "$0")/.." && pwd)
mkdir -p "$OUT"
LOG=$OUT/round.log
export WORK=$OUT/work TRACE=$OUT/work/automerge-paper.json.gz
mkdir -p "$WORK"
load() { cut -d' ' -f1-3 /proc/loadavg; }
step() { echo "== $(date -u +%FT%T) start $1, load $(load)" | tee -a "$LOG"; }
done_() { echo "== $(date -u +%FT%T) end $1 (exit $2), load $(load)" | tee -a "$LOG"; }

cd "$ROOT"
{
  echo "commit $(git rev-parse HEAD) baseline $BASELINE"
  echo "date $(date -u +%FT%T) load $(load)"
  uname -a; nproc; lscpu | grep -E 'Model name|^CPU\(s\)|Thread|MHz|L[23] cache' || true
  free -m; dotnet --version; node --version
} > "$OUT/machine.txt" 2>&1

step compare; bench/Ycs.Compare/run.sh "$OUT/compare" > "$OUT/compare.out" 2>&1; done_ compare $?

step ab
rm -rf "$WORK/base" && mkdir -p "$WORK/base" && git archive "$BASELINE" | tar -x -C "$WORK/base"
(cd "$WORK/base" && dotnet build bench/Ycs.Compare -c Release -o "$WORK/base-bin" > "$OUT/ab-base-build.out" 2>&1)
dotnet build bench/Ycs.Compare -c Release > "$OUT/ab-build.out" 2>&1
BASE=$WORK/base-bin/Ycs.Compare python3 bench/Ycs.Compare/ab.py 4 6 trace-replay text-random text-random-100k text-append \
  array-insert-random array-delete-random array-push-1txn concurrent-20 apply-state-v1/ycs apply-state-v2/ycs \
  apply-updates/ycs encode-v1 text-append-1txn-observed-25k text-append-1txn-observed map-set-distinct \
  map-set-distinct-1txn map-set-same array-push read-array read-map > "$OUT/ab-$BASELINE.txt" 2>&1
done_ ab $?

dotnet build bench/Ycs.Benchmarks -c Release > "$OUT/benchmarks-build.out" 2>&1
for mode in trace limits sizes perf; do
  arg=$mode; [ "$mode" = trace ] && arg=$TRACE
  step "benchmarks-$mode"
  dotnet run -c Release --no-build --project bench/Ycs.Benchmarks -- "$arg" > "$OUT/benchmarks-$mode.txt" 2>&1
  done_ "benchmarks-$mode" $?
done

mkdir -p "$WORK/layout" && cp "$TRACE" "$WORK/layout/trace.json.gz"
step layout; dotnet run -c Release --project bench/Ycs.Layout -- "$WORK/layout" small > "$OUT/layout-small.txt" 2>&1; done_ layout $?

step server; bench/server/run.sh all --out "$OUT/server" > "$OUT/server.out" 2>&1; done_ server $?
rm -rf "$WORK/base" "$WORK/base-bin"
echo "ALLDONE" | tee -a "$LOG"
