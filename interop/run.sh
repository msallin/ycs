#!/usr/bin/env bash
# Round-trips a document between real Yjs and Ycs: Yjs writes it, Ycs reads it,
# edits it, writes it back in both update formats, and Yjs verifies the result.
# Also refreshes the base64 fixtures pasted into tests/Ycs.Tests/YjsInteropTests.cs.
set -euo pipefail
cd "$(dirname "$0")"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
OUT="${TMPDIR:-/tmp}"

[ -d js/node_modules ] || (cd js && npm install --silent)

for V in yjs13 yjs14; do
  node js/gen.mjs "$V" > "$OUT/$V-js.json"
  "$DOTNET" run --project Ycs.Interop/Ycs.Interop.csproj -v q -- "$OUT/$V-js.json" > "$OUT/$V-cs.json"
  node js/verify.mjs "$V" "$OUT/$V-js.json" "$OUT/$V-cs.json"
done

# Additional self-contained scenarios; each exits non-zero on failure.
for CHECK in js/check-*.mjs; do
  [ -e "$CHECK" ] || continue
  DOTNET="$DOTNET" node "$CHECK"
done
