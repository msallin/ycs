#!/usr/bin/env bash
# Round-trip: Ycs re-encodes every corpus case (and authors its own random sessions), and real
# Yjs reads it all back and checks the state. Needs Node and the .NET SDK; not part of `dotnet test`.
set -euo pipefail
cd "$(dirname "$0")"
OUT="$(mktemp -d)"
trap 'rm -rf "$OUT"' EXIT

[ -d node_modules ] || npm ci --silent

YCS_CORPUS_EXPORT="$OUT" dotnet test ../../tests/Ycs.Corpus --filter "FullyQualifiedName~RoundTripExport" -v q --nologo >/dev/null
node roundtrip.mjs "$OUT"
