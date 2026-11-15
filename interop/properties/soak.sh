#!/usr/bin/env bash
# The live differential soak: fresh random scripts (by seed), run by real Yjs (oracle.mjs, Node)
# and by Ycs (tests/Ycs.Properties), compared after every step. Each distinct mismatch is shrunk
# to a minimal script, written to interop/properties/failures/ and printed. See docs/testing.md.
#
#   interop/properties/soak.sh [scripts=200000] [first seed=1] [workers=nproc] [extra dotnet test args...]
#
# Example: interop/properties/soak.sh 1000000 200001 8 --artifacts-path /tmp/ycs-soak
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
[ -d "$here/node_modules/yjs" ] || (cd "$here" && npm ci)
export YCS_PROP_SOAK=${1:-200000}
export YCS_PROP_SEED=${2:-1}
export YCS_PROP_WORKERS=${3:-$(nproc)}
shift $(( $# < 3 ? $# : 3 ))
exec dotnet test "$root/tests/Ycs.Properties" -c Release --filter Name=LiveSoak_ScriptsAgreeWithYjs \
  --logger "console;verbosity=detailed" "$@"
