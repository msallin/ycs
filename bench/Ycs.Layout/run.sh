#!/usr/bin/env bash
# Builds and runs the layout prototype: each workload's memory, the sizes by kind and the operations, each in a process
# of its own. Usage: bench/Ycs.Layout/run.sh [output file]. Fetches the automerge-paper trace as the comparison does.
set -euo pipefail
cd "$(dirname "$0")/../.."
OUT="${1:-bench/Ycs.Layout/results/$(date -u +%Y-%m-%d).md}"
FIX="${FIXTURES:-${TMPDIR:-/tmp}/ycs-layout-fixtures}"
mkdir -p "$FIX"
if [ ! -f "$FIX/trace.json.gz" ]; then
  bench/Ycs.Benchmarks/get-trace.sh "$FIX/trace.json.gz" > /dev/null
fi
echo "42c0ff1720c0cad77077e190e36135c9cab9f7bbc266351c0a7fe4981c6a1546  $FIX/trace.json.gz" | sha256sum -c - > /dev/null
dotnet build bench/Ycs.Layout -c Release > /dev/null
DLL=bench/Ycs.Layout/bin/Release/net11.0/Ycs.Layout.dll
{
  echo "Run $(date -u '+%Y-%m-%d %H:%M UTC'); load average $(cut -d' ' -f1-3 /proc/loadavg)"
  echo
  dotnet "$DLL" "$FIX" header
  for w in text-random-100k trace-replay concurrent-20 map-set-distinct array-insert-random array-delete-random; do
    dotnet "$DLL" "$FIX" memory "$w" | grep -v '^all checks passed$'
  done
  dotnet "$DLL" "$FIX" sizes | grep -v '^all checks passed$'
  dotnet "$DLL" "$FIX" ops
} | tee "$OUT"
