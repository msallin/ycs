#!/usr/bin/env bash
# Builds the Ycs server, starts it on a free port, runs smoke.mjs against it, and stops it.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
. "$here/servers.sh"

ensure_node_modules
build_ycs_server
start_ycs_server
node "$here/smoke.mjs" "$ycs_url"
