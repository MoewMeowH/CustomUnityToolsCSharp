# Fake claude for resting runs (ClaudeSessionTests): one process, many turns, stream-json in and out with --replay-user-messages.
# Line shapes follow the 2.1.316 probe (2026-11-03): every turn starts with system init; the user line comes back as
# {"type":"user","isReplay":true,"uuid":...} inside its turn; background_tasks_changed carries the whole task list.
# Behaviour copied from the probe:
#  - a message that arrives while a turn runs waits in the CLI queue; queued messages are merged into ONE turn.
#  - a task that ends while a turn runs is folded into that turn (no extra turn).
#  - a task that ends while idle starts an automatic turn. If a message is queued at that moment, the automatic turn goes first.
#  - stop_task: task stopped at once, no automatic turn.
#  - stdin closed with tasks still running: keeps running until the tasks end ('server' never ends -> the app must kill it).
# Commands (first word of a user message):
#   say <text>            answer <text>
#   bg <ms> <id>          start background task <id> that ends after <ms>
#   bgnow <id>            start background task <id> that ends when the next user message arrives
#   server <id>           start a background task that never ends
#   fg <ms>               a turn that takes <ms> (foreground command)
#   ask                   a permission request inside the turn; answer 'allow' -> result 'allowed', else 'denied'
#   bgask <ms> <id>       background task that asks for permission after <ms> (while idle), then ends
#   die                   exit 3 in the middle of the turn
#   nointerrupt           from now on the interrupt control answers error (the app must fall back)
# Controls: interrupt (ends the running turn with error_during_execution, tasks live on), stop_task, set_model, apply_flag_settings.
# ASCII only - Windows PowerShell reads this file as ANSI.
param([string]$Sid)
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[Console]::InputEncoding = [Text.Encoding]::UTF8

function Emit([string]$line) { [Console]::Out.WriteLine($line); [Console]::Out.Flush() }
function J([string]$s) { ConvertTo-Json $s -Compress }
# milliseconds (TickCount64 is not in Windows PowerShell's .NET Framework)
function Now { [long]([DateTime]::UtcNow.Ticks / 10000) }

$cost = 0.0
$model = 'claude-sonnet-5-5'
$tasks = [ordered]@{}        # id -> @{ End = tick or -1 (never); Ask = tick or $null; Asked = bool }
$queue = New-Object System.Collections.ArrayList   # queued user messages (objects with uuid, text)
$turn = $null                # running turn: @{ End = tick; Text = answer; Folded = list }
$asks = @{}                  # request id -> $true while waiting for a control_response
$answers = @{}               # request id -> response object
$eof = $false
$refuseInterrupt = $false    # 'nointerrupt' turns this on (interrupt answers error)
$autoPending = New-Object System.Collections.ArrayList   # ended task ids waiting for an automatic turn

function TaskList {
    $items = @()
    foreach ($k in $tasks.Keys) { $items += '{"task_id":' + (J $k) + ',"task_type":"local_bash","description":' + (J ('task ' + $k)) + '}' }
    Emit ('{"type":"system","subtype":"background_tasks_changed","tasks":[' + ($items -join ',') + ']}')
}

function Result([string]$text) {
    $script:cost += 0.01
    Emit ('{"type":"result","subtype":"success","is_error":false,"result":' + (J $text) + ',"total_cost_usd":' + $script:cost.ToString([Globalization.CultureInfo]::InvariantCulture) + ',"permission_denials":[]}')
}

function Init { Emit ('{"type":"system","subtype":"init","session_id":"' + $Sid + '","claude_code_version":"0.0.0-fake","model":"' + $script:model + '","mcp_servers":[]}') }

function EndTask([string]$id, [string]$status) {
    $tasks.Remove($id)
    Emit ('{"type":"system","subtype":"task_notification","task_id":' + (J $id) + ',"status":"' + $status + '","output_file":""}')
    TaskList
}

# start a user turn from the queue (all queued messages merge into one turn)
function StartUserTurn {
    Init
    $texts = @()
    foreach ($m in $queue) {
        $texts += $m.text
        Emit ('{"type":"user","message":{"role":"user","content":' + (J ($texts -join "`n")) + '},"session_id":"' + $Sid + '","parent_tool_use_id":null,"uuid":' + (J $m.uuid) + ',"isReplay":true}')
    }
    $queue.Clear()
    # the app may prepend system-reminder blocks (rules-changed note) - strip them to read the command
    $body = [regex]::Replace(($texts -join "`n"), '(?s)^(?:<system-reminder>.*?</system-reminder>\s*)+', '')
    $words = $body.Trim() -split '\s+'
    $script:turn = @{ End = (Now); Text = $body; Folded = New-Object System.Collections.ArrayList }
    switch ($words[0]) {
        'say' { $script:turn.Text = ($body.Trim().Substring(3)).Trim() }
        'bg' {
            $tasks[$words[2]] = @{ End = (Now) + [int]$words[1]; Ask = $null; Asked = $false }
            TaskList
            Emit ('{"type":"system","subtype":"task_started","task_id":' + (J $words[2]) + ',"description":"bg","is_backgrounded":true,"task_type":"local_bash"}')
            $script:turn.Text = 'started ' + $words[2]
        }
        'bgnow' {
            # ends exactly when the next user message arrives (the notification and the message race; the automatic turn goes first)
            $tasks[$words[1]] = @{ End = -2; Ask = $null; Asked = $false }
            TaskList
            $script:turn.Text = 'started ' + $words[1]
        }
        'server' {
            $tasks[$words[1]] = @{ End = -1; Ask = $null; Asked = $false }
            TaskList
            $script:turn.Text = 'server ' + $words[1]
        }
        'bgask' {
            $tasks[$words[2]] = @{ End = -1; Ask = (Now) + [int]$words[1]; Asked = $false }
            TaskList
            $script:turn.Text = 'agent ' + $words[2]
        }
        'fg' { $script:turn.End = (Now) + [int]$words[1]; $script:turn.Text = 'fg done' }
        'ask' {
            $asks['req_t'] = $true
            Emit '{"type":"control_request","request_id":"req_t","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"git status"},"tool_use_id":"toolu_t"}}'
            $script:turn.End = [long]::MaxValue
            $script:turn.Wait = 'req_t'
        }
        'nointerrupt' { $script:refuseInterrupt = $true; $script:turn.Text = 'refusing interrupts' }
        'die' { [Console]::Error.WriteLine('fake died'); exit 3 }
        default { $script:turn.Text = 'echo ' + $body }
    }
}

function AutoTurn {
    Init
    Emit ('{"type":"assistant","message":{"model":"' + $script:model + '","content":[{"type":"text","text":' + (J ('auto ' + ($autoPending -join ','))) + '}]}}')
    $text = 'auto ' + ($autoPending -join ',')
    $autoPending.Clear()
    Result $text
}

function HandleLine([string]$line) {
    $msg = $line | ConvertFrom-Json
    if ($msg.type -eq 'user') {
        foreach ($k in @($tasks.Keys)) { if ($tasks[$k].End -eq -2) { $tasks[$k].End = 0 } }
        [void]$queue.Add([pscustomobject]@{ uuid = [string]$msg.uuid; text = [string]$msg.message.content })
        return
    }
    if ($msg.type -eq 'control_response') { $answers[$msg.response.request_id] = $msg.response.response; return }
    if ($msg.type -ne 'control_request') { return }
    $id = $msg.request_id
    switch ($msg.request.subtype) {
        'initialize' { Emit ('{"type":"control_response","response":{"subtype":"success","request_id":' + (J $id) + ',"response":{}}}') }
        'set_model' {
            $script:model = 'claude-' + $msg.request.model
            Emit ('{"type":"control_response","response":{"subtype":"success","request_id":' + (J $id) + ',"response":{"model":' + (J $script:model) + '}}}')
        }
        'apply_flag_settings' { Emit ('{"type":"control_response","response":{"subtype":"success","request_id":' + (J $id) + '}}') }
        'stop_task' {
            $t = [string]$msg.request.task_id
            if ($tasks.Contains($t)) {
                EndTask $t 'stopped'
                Emit ('{"type":"control_response","response":{"subtype":"success","request_id":' + (J $id) + ',"response":{}}}')
            } else {
                Emit ('{"type":"control_response","response":{"subtype":"error","request_id":' + (J $id) + ',"error":"invalid_task_id"}}')
            }
        }
        'interrupt' {
            # 2.1.316 probe (2026-11-04): success at once, the running turn ends with error_during_execution (no text), background tasks live on.
            # A user message whose first word is 'nointerrupt' makes the fake refuse (the app falls back to ending the process).
            if ($script:refuseInterrupt) {
                Emit ('{"type":"control_response","response":{"subtype":"error","request_id":' + (J $id) + ',"error":"refused"}}')
            } else {
                Emit ('{"type":"control_response","response":{"subtype":"success","request_id":' + (J $id) + ',"response":{"still_queued":[]}}}')
                if ($null -ne $script:turn) {
                    $script:turn = $null
                    $script:cost += 0.01
                    Emit ('{"type":"result","subtype":"error_during_execution","is_error":true,"total_cost_usd":' + $script:cost.ToString([Globalization.CultureInfo]::InvariantCulture) + ',"permission_denials":[]}')
                }
            }
        }
        default { Emit ('{"type":"control_response","response":{"subtype":"error","request_id":' + (J $id) + ',"error":"unsupported"}}') }
    }
}

# [Console]::In.ReadLineAsync() blocks in Windows PowerShell (synchronized reader) - read the raw stream so timers keep running
$stdin = New-Object IO.StreamReader([Console]::OpenStandardInput(), (New-Object Text.UTF8Encoding $false))
$read = $stdin.ReadLineAsync()
while ($true) {
    # input
    if (-not $eof -and $read.Wait(20)) {
        $line = $read.Result
        if ($null -eq $line) { $eof = $true } else { if ($line.Trim()) { HandleLine $line }; $read = $stdin.ReadLineAsync() }
    } elseif ($eof) { Start-Sleep -Milliseconds 20 }

    $now = Now
    # background tasks: permission asks and ends
    foreach ($k in @($tasks.Keys)) {
        $t = $tasks[$k]
        if ($null -ne $t.Ask -and -not $t.Asked -and $now -ge $t.Ask) {
            $t.Asked = $true
            $asks[('req_' + $k)] = $true
            Emit ('{"type":"control_request","request_id":' + (J ('req_' + $k)) + ',"request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"echo ' + $k + '"},"agent_id":"a1","tool_use_id":"toolu_' + $k + '"}}')
        }
        if ($t.Asked -and $answers.Contains('req_' + $k)) {
            $status = if ($answers['req_' + $k].behavior -eq 'allow') { 'completed' } else { 'failed' }
            $answers.Remove('req_' + $k)
            EndTask $k $status
            if ($null -ne $turn) { [void]$turn.Folded.Add($k) } else { [void]$autoPending.Add($k) }
            continue
        }
        if ($t.End -ge 0 -and $now -ge $t.End) {
            EndTask $k 'completed'
            if ($null -ne $turn) { [void]$turn.Folded.Add($k) } else { [void]$autoPending.Add($k) }
        }
    }

    # running turn
    if ($null -ne $turn) {
        if ($turn.Wait -and $answers.Contains($turn.Wait)) {
            $d = $answers[$turn.Wait]; $answers.Remove($turn.Wait)
            $turn.Text = if ($d.behavior -eq 'allow') { 'allowed' } else { 'denied' }
            $turn.Wait = $null
            $turn.End = $now
        }
        if ($now -ge $turn.End) {
            $text = $turn.Text
            if ($turn.Folded.Count -gt 0) { $text += ' (also ended: ' + ($turn.Folded -join ',') + ')' }
            $turn = $null
            Result $text
        }
        continue
    }

    # idle: automatic turn first, then queued user messages
    if ($autoPending.Count -gt 0) { AutoTurn; continue }
    if ($queue.Count -gt 0) { StartUserTurn; continue }
    if ($eof -and $tasks.Count -eq 0) { break }
}
