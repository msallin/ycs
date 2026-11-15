#!/usr/bin/env bash
# Stale-marker scripts generated and run in real Yjs now (gen.mjs), then replayed on Ycs by
# tests/Ycs.Tests/StaleMarkerScriptTests (content after every step, final state). See docs/testing.md.
#
#   interop/markers/soak.sh [scripts per mode=20000] [first seed=1] [modes="apply small observer"] [extra dotnet test args...]
#
# The reviewed sets: 3,000 "apply", 60,000 "small" and 20,000 "observer" scripts from seed 1.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
[ -d "$here/node_modules/yjs" ] || (cd "$here" && npm ci)
count=${1:-20000}
first=${2:-1}
modes=${3:-apply small observer}
shift $(( $# < 3 ? $# : 3 ))
scripts=$(mktemp)
trap 'rm -f "$scripts"' EXIT
for mode in $modes; do
  node "$here/gen.mjs" "$first" "$count" "$mode" | grep '^{' >> "$scripts"
done
echo "$(wc -l < "$scripts") scripts ($modes, seeds $first..$((first + count - 1)))"
YCS_STALE_MARKER_SCRIPTS="$scripts" dotnet test "$root/tests/Ycs.Tests" -c Release \
  --filter Name=Scripts_ContentAndStateEqualYjs --logger "console;verbosity=normal" "$@"
