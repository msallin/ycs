#!/usr/bin/env bash
# Builds the Ycs server, starts it and the reference server (@y/websocket-server) on free ports, runs converge.mjs's
# seeds against each, and stops them.
# Usage: converge.sh [seeds (default 32)] [first seed (default 1)] [converge.mjs options, e.g. --clients 3 --steps 20]
# TARGETS="ycs" or "node" runs one server only.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
. "$here/servers.sh"

seeds="${1:-32}"
first="${2:-1}"
shift $(( $# < 2 ? $# : 2 ))
targets="${TARGETS:-ycs node}"

ensure_node_modules
status=0
for target in $targets; do
  case "$target" in
    ycs) build_ycs_server; start_ycs_server; url="$ycs_url" ;;
    node) start_node_server; url="$node_url" ;;
    *) echo "unknown target $target" >&2; exit 2 ;;
  esac
  node "$here/converge.mjs" --url "$url" --name "$target" --seeds "$seeds" --first "$first" "$@" || status=1
done
exit $status
