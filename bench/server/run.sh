#!/usr/bin/env bash
# Builds the Ycs server (Release), fetches the editing trace, and runs a scenario against the Ycs server and the
# Node reference server in turn, each started fresh. Usage: run.sh [fanout|join|memory|all] [--option value ...]
# (the options are in README.md). The server runs on SERVER_CPUS, the driver on DRIVER_CPUS (default: the first and
# the second half of the cores), with taskset when there is one.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$here"
export MSBUILDDISABLENODEREUSE=1
scenario="${1:-all}"
shift || true

[ -d node_modules ] || npm ci --silent
TRACE="${TRACE:-$here/.trace/automerge-paper.json.gz}"
if [ ! -f "$TRACE" ]; then mkdir -p "$(dirname "$TRACE")"; ../Ycs.Benchmarks/get-trace.sh "$TRACE" >/dev/null; fi
echo "42c0ff1720c0cad77077e190e36135c9cab9f7bbc266351c0a7fe4981c6a1546  $TRACE" | sha256sum -c --quiet -
dotnet build host -c Release --nologo -v quiet -clp:ErrorsOnly

cores="$(nproc --all)"
half=$((cores / 2))
SERVER_CPUS="${SERVER_CPUS:-0-$((half - 1))}"
DRIVER_CPUS="${DRIVER_CPUS:-$half-$((cores - 1))}"
if command -v taskset >/dev/null; then
  echo "server pinned to cores $SERVER_CPUS, driver to cores $DRIVER_CPUS (taskset)" >&2
  exec taskset -c "$DRIVER_CPUS" node bench.mjs "$scenario" --server-cpus "$SERVER_CPUS" --trace "$TRACE" "$@"
else
  echo "no taskset: server and driver share every core" >&2
  exec node bench.mjs "$scenario" --trace "$TRACE" "$@"
fi
