#!/usr/bin/env bash
# Checks a two-computer desk, one stage at a time.
#
# The stages build on each other, and a failure in an early one makes every later result
# meaningless: a desk cannot arrange itself if the two computers have not found each other, and
# monitors cannot be switched if nobody knows which input selects which computer. Reporting them in
# order is what stops a fault being blamed on the layer above it.
#
#   usage: desk-check.sh <user@host-of-the-other-computer> [ssh-key]
#
# Each computer summarises itself — neither side is assumed to have anything installed to read JSON
# with, and both answer the same question the same way so the two can be compared directly.

set -uo pipefail

REMOTE="${1:?usage: desk-check.sh <user@host> [ssh-key]}"
KEY="${2:-}"
PORT="${PORT:-24801}"
SSH=(ssh -o BatchMode=yes -o ConnectTimeout=8)
[ -n "$KEY" ] && SSH+=(-i "$KEY")

HERE=$(curl -s --max-time 8 "http://127.0.0.1:$PORT/api/status.txt" 2>/dev/null)
THERE=$("${SSH[@]}" "$REMOTE" "curl -s --max-time 8 http://127.0.0.1:$PORT/api/status.txt" 2>/dev/null)

printf '\n\033[1m======== this computer ========\033[0m\n'
[ -n "$HERE" ] && echo "$HERE" || echo "  FAIL  not answering on 127.0.0.1:$PORT — is ScreenFuse running?"

printf '\n\033[1m======== %s ========\033[0m\n' "$REMOTE"
[ -n "$THERE" ] && echo "$THERE" || echo "  FAIL  not answering on its own 127.0.0.1:$PORT"

printf '\n\033[1m======== do they agree? ========\033[0m\n'
if [ -n "$HERE" ] && [ -n "$THERE" ]; then
  for field in layout crossings; do
    a=$(echo "$HERE"  | grep -F "$field:" | head -1 | sed "s/.*$field://" | tr -d ' ')
    b=$(echo "$THERE" | grep -F "$field:" | head -1 | sed "s/.*$field://" | tr -d ' ')
    if [ "$a" = "$b" ]; then
      printf '  ok    both report the same %s\n' "$field"
    else
      printf '  FAIL  the two computers disagree about %s\n' "$field"
      printf '          here:  %s\n' "${a:-(none)}"
      printf '          there: %s\n' "${b:-(none)}"
    fi
  done
else
  echo "  (cannot compare — one side did not answer)"
fi

failures=$( { echo "$HERE"; echo "$THERE"; } | grep -c "FAIL" )
printf '\n\033[1m%s\033[0m\n' "$([ "$failures" -eq 0 ] && echo "no failures reported" || echo "$failures failure(s) reported above")"
[ "$failures" -eq 0 ]
