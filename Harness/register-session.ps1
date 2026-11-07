# SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
# Copyright (c) 2026 ItsRenji. All rights reserved.
#
# Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
# https://polyformproject.org/licenses/shield/1.0.0

# Hands this Claude session's own messaging address and credential to the Conduit bridge, so work that
# outlives its call can be reported back into this exact conversation.
#
# Run from a SessionStart hook, where the harness exports the three session values below. It writes no
# credential to disk and it never prints one: the transport bearer is read out of the descriptor at
# runtime rather than passed on a command line, where any process on the machine could read it.
#
# It ALWAYS exits 0. A hook that fails loudly nags the owner at the top of every session about something
# they cannot act on mid-conversation, so the outcome goes to a single-run log beside the descriptor.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $DescriptorPath
)

$ErrorActionPreference = 'Stop'

$logPath = Join-Path (Split-Path -Parent $DescriptorPath) 'session-hook.log'

function Write-Outcome {
    param([string] $Text)

    try {
        Set-Content -Path $logPath -Value "$(Get-Date -Format o)  $Text" -Encoding UTF8 -ErrorAction Stop
    }
    catch {
        # A log that cannot be written is not a reason to fail the session start.
    }
}

try {
    if (-not $env:CLAUDE_CODE_SESSION_ID -or -not $env:CLAUDE_CODE_MESSAGING_SOCKET `
            -or -not $env:CLAUDE_CODE_MESSAGING_TOKEN) {
        Write-Outcome 'skipped: this process carries no session messaging environment'
        exit 0
    }

    if (-not (Test-Path -LiteralPath $DescriptorPath)) {
        Write-Outcome "skipped: no transport descriptor at $DescriptorPath, so the editor is not listening"
        exit 0
    }

    $descriptor = Get-Content -LiteralPath $DescriptorPath -Raw | ConvertFrom-Json
    if (-not $descriptor.port -or -not $descriptor.token -or -not $descriptor.identity) {
        Write-Outcome 'skipped: the transport descriptor is incomplete'
        exit 0
    }

    $response = Invoke-RestMethod -Uri "http://127.0.0.1:$($descriptor.port)/agent-session" -Method Post `
        -Headers @{
            'Authorization'             = "Bearer $($descriptor.token)"
            'X-Conduit-Project'         = $descriptor.identity
            'X-Conduit-Session'         = $env:CLAUDE_CODE_SESSION_ID
            'X-Conduit-Socket'          = $env:CLAUDE_CODE_MESSAGING_SOCKET
            'X-Conduit-Messaging-Token' = $env:CLAUDE_CODE_MESSAGING_TOKEN
        } `
        -Body '{}' -ContentType 'application/json' -TimeoutSec 10

    Write-Outcome "registered session $($response.sessionId)"
}
catch {
    Write-Outcome "failed: $($_.Exception.Message)"
}

exit 0
