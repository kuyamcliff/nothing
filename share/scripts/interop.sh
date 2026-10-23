#!/usr/bin/env bash
# Sends real files between the Windows (.NET) and Android (Kotlin/JVM) engines in both directions
# and checks every byte. Usage: interop.sh [big-file-MB]
set -euo pipefail
HERE="$(cd "$(dirname "$0")/.." && pwd)"
BIG_MB="${1:-300}"
WORK="$(mktemp -d /tmp/cfs-interop-XXXX)"
trap 'kill $(jobs -p) 2>/dev/null || true; rm -rf "$WORK"' EXIT

DOTNET_CLI="dotnet $HERE/windows/tools/ClipFx.Share.Cli/bin/Release/net8.0/ClipFx.Share.Cli.dll"
KOTLIN_CLI="$HERE/android/core/build/install/core/bin/core"

# Test content
SRC="$WORK/src"; mkdir -p "$SRC/Album/2024/deep" "$SRC/Album/Empty"
head -c $((BIG_MB * 1024 * 1024)) /dev/urandom > "$SRC/movie.mkv"
head -c 3000000 /dev/urandom > "$SRC/Album/2024/photo.jpg"
for i in $(seq 1 200); do head -c $((RANDOM % 5000)) /dev/urandom > "$SRC/Album/2024/deep/f$i.txt"; done
: > "$SRC/Album/empty.bin"

check() { # $1 = save dir
  diff -r "$SRC/Album" "$1/Album" && cmp "$SRC/movie.mkv" "$1/movie.mkv" && test -d "$1/Album/Empty"
}

run_pair() { # $1 receiver cli, $2 sender cli, $3 label
  local save="$WORK/save-$3"; mkdir -p "$save"
  local log="$WORK/recv-$3.log"
  $1 receive --data "$WORK/rdata-$3" --save "$save" > "$log" 2>&1 &
  local pid=$!
  for _ in $(seq 1 100); do grep -q READY "$log" 2>/dev/null && break; sleep 0.2; done
  local port; port=$(grep READY "$log" | awk '{print $2}')
  local out; out=$($2 send --data "$WORK/sdata-$3" --to "127.0.0.1:$port" --text "hello $3" "$SRC/Album" "$SRC/movie.mkv")
  echo "$3: $out"
  sleep 0.5
  kill $pid 2>/dev/null || true
  check "$save" && echo "$3: files identical"
}

run_pair "$DOTNET_CLI" "$KOTLIN_CLI" "kotlin-to-dotnet"
run_pair "$KOTLIN_CLI" "$DOTNET_CLI" "dotnet-to-kotlin"
run_pair "$KOTLIN_CLI" "$KOTLIN_CLI" "kotlin-to-kotlin"
echo "INTEROP OK"
