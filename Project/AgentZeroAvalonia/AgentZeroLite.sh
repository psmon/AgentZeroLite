#!/bin/sh
# AgentZero Lite (Avalonia host) CLI wrapper for macOS / Linux.
# Usage: AgentZeroLite.sh <command> [options]
#   e.g. ./AgentZeroLite.sh status
#        ./AgentZeroLite.sh terminal-list
#        ./AgentZeroLite.sh terminal-send 0 0 "ls -la"
# Inside the .app bundle this lives next to the executable (Contents/MacOS/).
dir="$(cd "$(dirname "$0")" && pwd)"
exe="$dir/AgentZeroLite"
if [ ! -x "$exe" ]; then
  echo "[ERROR] executable not found: $exe" >&2
  exit 1
fi
exec "$exe" -cli "$@"
