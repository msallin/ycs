#!/usr/bin/env bash
# Round-trip against Yjs v14 (@y/y): Ycs answers the documents v14 wrote (tests/Ycs.Tests V14InteropTests),
# and v14 applies the answers and checks it converges with Ycs. Needs Node >= 22 and the .NET SDK; not part
# of `dotnet test`, which checks the recorded fixtures without Node.
set -euo pipefail
cd "$(dirname "$0")"
OUT="$(mktemp -d)"
trap 'rm -rf "$OUT"' EXIT

[ -d node_modules ] || npm ci --silent

YCS_V14_EXPORT="$OUT" dotnet test ../../tests/Ycs.Tests --filter "FullyQualifiedName~V14InteropTests.Export_WhenRequested_WritesYcsAnswers" -v q --nologo >/dev/null
node roundtrip.mjs "$OUT"
