#!/usr/bin/env bash
# Builds the Ycs server, starts it on a free port, runs smoke.mjs (SignalR) against it, and stops it.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
export MSBUILDDISABLENODEREUSE=1

[ -d "$here/node_modules" ] || (cd "$here" && npm ci --silent)
dotnet build "$root/samples/Ycs.Server" -c Release --nologo -v quiet
port="$(node -e "const s=require('net').createServer().listen(0,'127.0.0.1',()=>{console.log(s.address().port);s.close()})")"
dotnet "$root/samples/Ycs.Server/bin/Release/net11.0/Ycs.Server.dll" --contentRoot "$root/samples/Ycs.Server" --urls "http://127.0.0.1:$port" \
  --Logging:LogLevel:Default Warning &
server=$!
trap 'kill $server 2>/dev/null; wait $server 2>/dev/null || true' EXIT

for _ in $(seq 100); do
  (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null && break
  kill -0 $server 2>/dev/null || { echo "the server exited" >&2; exit 1; }
  sleep 0.1
done

node "$here/smoke.mjs" "http://127.0.0.1:$port"
