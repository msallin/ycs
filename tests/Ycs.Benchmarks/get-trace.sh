#!/usr/bin/env bash
# Downloads the "automerge-paper" editing trace (Martin Kleppmann typing a LaTeX paper,
# ~260k single-character edits) from https://github.com/josephg/editing-traces.
# The trace is not checked in; pass the downloaded file to the benchmark.
set -euo pipefail
OUT="${1:-${TMPDIR:-/tmp}/automerge-paper.json.gz}"
curl -sSfL -o "$OUT" https://github.com/josephg/editing-traces/raw/master/sequential_traces/automerge-paper.json.gz
echo "$OUT"
