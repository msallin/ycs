#!/usr/bin/env bash
# Publishes YDotNetWasm for browser-wasm (the SDK alone, as samples/Ycs.WasmSample) and runs it in Node.
set -uo pipefail
cd "$(dirname "$0")"
OUT="${1:-${TMPDIR:-/tmp}/ycs-compare-wasm}"
rm -rf "$OUT"
dotnet publish YDotNetWasm -c Release -o "$OUT" 2>&1 | grep -E "warning IL|error" | sed 's/ \[.*//' | sort -u
echo "native files in the publish output: $(find "$OUT" -iname '*yrs*' | wc -l)"
(cd "$OUT/wwwroot" && timeout 120 node main.mjs 2>&1 | head -4; echo "exit ${PIPESTATUS[0]}")
