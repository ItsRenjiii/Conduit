#!/bin/sh
# SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
# Copyright (c) 2026 ItsRenji. All rights reserved.
#
# Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
# https://polyformproject.org/licenses/shield/1.0.0

# Hands this Claude session's own messaging address and credential to the Conduit bridge, so work that
# outlives its call can be reported back into this exact conversation. The macOS and Linux counterpart
# of register-session.ps1; both are driven by the same SessionStart hook entry.
#
# No credential is written to disk or printed: the transport bearer is read out of the descriptor at
# runtime rather than passed on a command line, where any process on the machine could read it.
#
# It ALWAYS exits 0. A hook that fails loudly nags the owner at the top of every session about something
# they cannot act on mid-conversation, so the outcome goes to a single-run log beside the descriptor.
#
# The descriptor's three values are read with sed rather than a JSON parser, because neither jq nor
# python can be assumed present on someone else's machine. That is safe ONLY because Conduit writes this
# file itself and every field read here is a flat scalar; it is not a general JSON read.

set -u

descriptor="${1:-}"
log="$(dirname "$descriptor")/session-hook.log"

write_outcome() {
    printf '%s  %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$1" > "$log" 2>/dev/null || true
}

if [ -z "$descriptor" ]; then
    exit 0
fi

if [ -z "${CLAUDE_CODE_SESSION_ID:-}" ] || [ -z "${CLAUDE_CODE_MESSAGING_SOCKET:-}" ] \
        || [ -z "${CLAUDE_CODE_MESSAGING_TOKEN:-}" ]; then
    write_outcome 'skipped: this process carries no session messaging environment'
    exit 0
fi

if [ ! -f "$descriptor" ]; then
    write_outcome "skipped: no transport descriptor at $descriptor, so the editor is not listening"
    exit 0
fi

if ! command -v curl > /dev/null 2>&1; then
    write_outcome 'skipped: curl is not available, so the session could not be registered'
    exit 0
fi

port=$(sed -n 's/.*"port"[[:space:]]*:[[:space:]]*\([0-9]\{1,\}\).*/\1/p' "$descriptor" | head -n 1)
token=$(sed -n 's/.*"token"[[:space:]]*:[[:space:]]*"\([^"]\{1,\}\)".*/\1/p' "$descriptor" | head -n 1)
identity=$(sed -n 's/.*"identity"[[:space:]]*:[[:space:]]*"\([^"]\{1,\}\)".*/\1/p' "$descriptor" | head -n 1)

if [ -z "$port" ] || [ -z "$token" ] || [ -z "$identity" ]; then
    write_outcome 'skipped: the transport descriptor is incomplete'
    exit 0
fi

status=$(curl --silent --show-error --max-time 10 --output /dev/null --write-out '%{http_code}' \
    --request POST "http://127.0.0.1:${port}/agent-session" \
    --header "Authorization: Bearer ${token}" \
    --header "X-Conduit-Project: ${identity}" \
    --header "X-Conduit-Session: ${CLAUDE_CODE_SESSION_ID}" \
    --header "X-Conduit-Socket: ${CLAUDE_CODE_MESSAGING_SOCKET}" \
    --header "X-Conduit-Messaging-Token: ${CLAUDE_CODE_MESSAGING_TOKEN}" \
    --header 'Content-Type: application/json' \
    --data '{}' 2>/dev/null) || status='000'

if [ "$status" = "200" ]; then
    write_outcome "registered session ${CLAUDE_CODE_SESSION_ID}"
else
    write_outcome "failed: the bridge answered ${status}"
fi

exit 0
