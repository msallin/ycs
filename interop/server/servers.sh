# Sourced by smoke.sh and converge.sh: build and start the servers on free ports, stop them on exit.
# Needs $here (this directory) and $root (the repository).
export MSBUILDDISABLENODEREUSE=1
pids=()
trap 'for p in "${pids[@]}"; do kill "$p" 2>/dev/null; wait "$p" 2>/dev/null || true; done' EXIT

ensure_node_modules() {
  [ -d "$here/node_modules" ] || (cd "$here" && npm ci --silent)
}

build_ycs_server() {
  dotnet build "$root/samples/Ycs.Server" -c Release --nologo -v quiet -clp:ErrorsOnly
}

free_port() {
  node -e "const s=require('net').createServer().listen(0,'127.0.0.1',()=>{console.log(s.address().port);s.close()})"
}

# wait_for_port <port> <pid>
wait_for_port() {
  for _ in $(seq 100); do
    (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null && return 0
    kill -0 "$2" 2>/dev/null || { echo "the server exited" >&2; return 1; }
    sleep 0.1
  done
  echo "the server did not listen on $1" >&2
  return 1
}

# start_ycs_server: sets ycs_url
start_ycs_server() {
  local port
  port="$(free_port)"
  dotnet "$root/samples/Ycs.Server/bin/Release/net11.0/Ycs.Server.dll" --urls "http://127.0.0.1:$port" \
    --Logging:LogLevel:Default Warning &
  pids+=($!)
  wait_for_port "$port" $!
  ycs_url="ws://127.0.0.1:$port/ws"
}

# start_node_server: the reference, @y/websocket-server; sets node_url
start_node_server() {
  local port
  port="$(free_port)"
  HOST=127.0.0.1 PORT="$port" node "$here/node_modules/@y/websocket-server/src/server.js" >/dev/null &
  pids+=($!)
  wait_for_port "$port" $!
  node_url="ws://127.0.0.1:$port/ws"
}
