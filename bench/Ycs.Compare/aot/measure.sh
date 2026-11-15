#!/usr/bin/env bash
# Publishes the same minimal program on ycs and on YDotNet for linux-x64 three ways (framework-dependent,
# self-contained + trimmed, Native AOT), runs each, and prints the publish size without symbol files, the size of
# libyrs in it, and the distinct trim/AOT warnings.
set -uo pipefail
cd "$(dirname "$0")"
OUT="${1:-${TMPDIR:-/tmp}/ycs-compare-aot}"
rm -rf "$OUT"
for app in YcsAot YDotNetAot; do
  for mode in fdd trimmed aot; do
    case $mode in
      fdd) props="--self-contained false" ;;
      trimmed) props="--self-contained true -p:PublishTrimmed=true -p:TrimmerSingleWarn=false" ;;
      aot) props="-p:PublishAot=true -p:TrimmerSingleWarn=false" ;;
    esac
    dir="$OUT/$app-$mode"
    log="$OUT/$app-$mode.log"
    mkdir -p "$dir"
    dotnet publish "$app" -c Release -r linux-x64 $props -o "$dir" > "$log" 2>&1
    status=$?
    # Each warning is printed twice (once per target); count distinct messages.
    warnings=$(grep -oE "warning IL[0-9]+: [^[]*" "$log" | sort -u | grep -oE "IL[0-9]+" | sort | uniq -c | tr -s ' ' | paste -sd, -)
    # What ships: everything but the symbol files (.dbg, .pdb).
    size=$(find "$dir" -type f ! -name '*.dbg' ! -name '*.pdb' -print0 | du -cb --files0-from=- | tail -1 | cut -f1)
    native=$(find "$dir" -name 'libyrs*' -o -name 'yrs.dll' | xargs -r du -cb | tail -1 | cut -f1)
    if [ $status -eq 0 ]; then
      run=$("$dir/$app" 2>&1 | tail -1); code=$?
    else
      run="publish failed"; code=-
    fi
    printf '%s\t%s\tpublish=%s\tbytes=%s\tlibyrs=%s\twarnings=[%s]\trun(exit %s): %s\n' "$app" "$mode" "$status" "$size" "${native:-0}" "$warnings" "$code" "$run"
  done
done
