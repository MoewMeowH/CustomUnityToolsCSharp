# 가짜 claude: stdin stream-json(SDK식 — initialize 제어 요청 → 사용자 말)에서 사용자 말의 첫 낱말로 동작을 고르고 stream-json 줄을 낸다
# (RoomHubTests · ApprovalFlowTests). 인자: 세션 ID. 줄 모양은 0번 실측(Claude Code 2.1.311 · 2.1.312 · 2.1.316)에서 본 것만 흉내 낸다.
# 한 프로세스가 말 여러 개를 받는다 (쉬는 실행) — stdin이 닫힐 때까지. 말에 uuid가 있으면 init 뒤 그 말을 되돌린다 (--replay-user-messages).
# approve · ask · write 모드: 3차 0번 실측의 control_request can_use_tool을 내고 stdin의 control_response를 기다려, 허용이면 성공 · 거절이면 오류 결과를 낸다.
# 두 번째 인자: 할 일 폴더(tasks 모드가 <폴더>/<세션>/<번호>.json을 쓴다 — 3차 b, 0번 실측 B 모양).
# third arg (optional): session transcript path - echo modes append one line there like the real CLI recording the user message.
param([string]$Sid, [string]$TasksRoot, [string]$Transcript)
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[Console]::InputEncoding = [Text.Encoding]::UTF8

function Emit([string]$line) { [Console]::Out.WriteLine($line); [Console]::Out.Flush() }

# Many turns in one process (resting runs, 2.1.316 probe): every user message -> init, the user line back (isReplay + uuid, --replay-user-messages),
# then the mode's lines. stdin closed -> the process ends. User lines that arrive while a turn waits for an answer are kept for later.
$inbox = New-Object System.Collections.Queue
function NextUser {
    if ($script:inbox.Count -gt 0) { return $script:inbox.Dequeue() }
    while ($null -ne ($line = [Console]::In.ReadLine())) {
        if (-not $line.Trim()) { continue }
        $msg = $line | ConvertFrom-Json
        if ($msg.type -eq 'control_request' -and $msg.request.subtype -eq 'initialize') {
            Emit ('{"type":"control_response","response":{"subtype":"success","request_id":"' + $msg.request_id + '","response":{}}}')
        } elseif ($msg.type -eq 'user') { return $msg }
    }
    return $null
}

# 승인 요청을 내고 같은 request_id의 답을 기다린다. 답(response 객체) 또는 입력이 끝나면 $null.
function Ask([string]$id, [string]$request) {
    Emit ('{"type":"control_request","request_id":"' + $id + '","request":' + $request + '}')
    while ($null -ne ($line = [Console]::In.ReadLine())) {
        if (-not $line.Trim()) { continue }
        $msg = $line | ConvertFrom-Json
        if ($msg.type -eq 'control_response' -and $msg.response.request_id -eq $id) { return $msg.response.response }
        if ($msg.type -eq 'user') { $script:inbox.Enqueue($msg) }
    }
    return $null
}

function TaskList([string]$items) { Emit ('{"type":"system","subtype":"background_tasks_changed","tasks":[' + $items + ']}') }
function Init([string]$id) { Emit ('{"type":"system","subtype":"init","session_id":"' + $id + '","claude_code_version":"0.0.0-fake","mcp_servers":[]}') }

function Finish($d, [string]$tool, [string]$okText) {
    if ($d -and $d.behavior -eq 'allow') {
        Emit ('{"type":"result","subtype":"success","is_error":false,"result":' + (ConvertTo-Json $okText -Compress) + ',"total_cost_usd":0.001,"permission_denials":[]}')
    } elseif ($d) {
        $msg = 'denied: ' + $d.message
        Emit ('{"type":"result","subtype":"error_during_execution","is_error":true,"result":' + (ConvertTo-Json $msg -Compress) + ',"total_cost_usd":0.001,"permission_denials":[{"tool_name":"' + $tool + '"}]}')
    } else {
        Emit ('{"type":"result","subtype":"error_during_execution","is_error":true,"result":"no answer","total_cost_usd":0.001,"permission_denials":[{"tool_name":"' + $tool + '"}]}')
    }
}

while ($null -ne ($user = NextUser)) {
$prompt = [string]$user.message.content
# app may prepend system-reminder blocks (rules-changed note, group chat delta - one or more): strip them to read the mode, keep the full prompt for 'echo'
$body = [regex]::Replace($prompt, '(?s)^(?:<system-reminder>.*?</system-reminder>\s*)+', '')
$mode = ($body.Trim() -split '\s+')[0]

# resumed session whose background task died with the previous CLI (real check 2026-10-29, same in 2.1.316): before the user turn the CLI emits
# task_notification stopped, init and an EMPTY result (no user line back). 'ghostfail': that early result is an error without text.
if ($mode -in 'ghost', 'ghostfail') {
    Emit '{"type":"system","subtype":"task_notification","task_id":"b_old","status":"stopped","output_file":"","summary":"Background shell command did not finish before the previous session ended"}'
    Init $Sid
    if ($mode -eq 'ghost') { Emit '{"type":"result","subtype":"success","is_error":false,"result":"","total_cost_usd":0,"permission_denials":[]}' }
    else { Emit '{"type":"result","subtype":"error_during_execution","is_error":true,"total_cost_usd":0,"permission_denials":[]}' }
}

$useSid = if ($mode -eq 'other') { '00000000-0000-0000-0000-000000000000' } else { $Sid }
Init $useSid
if ($user.uuid) { Emit ('{"type":"user","message":{"role":"user","content":' + (ConvertTo-Json $prompt -Compress) + '},"session_id":"' + $Sid + '","parent_tool_use_id":null,"uuid":"' + $user.uuid + '","isReplay":true}') }

switch ($mode) {
    { $_ -in 'echo', '/echo' } {
        # returns the whole received prompt as an error result so the test can read it from the end event
        if ($Transcript) { New-Item -ItemType Directory -Force (Split-Path $Transcript) | Out-Null; Add-Content -Path $Transcript -Value '{"type":"user"}' }
        Emit ('{"type":"result","subtype":"error_during_execution","is_error":true,"result":' + (ConvertTo-Json $prompt -Compress) + ',"total_cost_usd":0.001,"permission_denials":[]}')
    }
    'env' {
        # environment the app added for this run (subagent effort) - returned as an error result like echo
        $v = if ($null -ne $env:CLAUDE_CODE_SUBAGENT_EFFORT) { 'effort=' + $env:CLAUDE_CODE_SUBAGENT_EFFORT } else { 'effort=none' }
        Emit ('{"type":"result","subtype":"error_during_execution","is_error":true,"result":' + (ConvertTo-Json $v -Compress) + ',"total_cost_usd":0.001,"permission_denials":[]}')
    }
    'call' {
        # group chain tests (GroupTests, stage 4): success answer = the message with '#Name' turned into '@Name' (the user message itself calls nobody else)
        # SLOW anywhere in the prepended blocks (an earlier line of the group chat) -> wait 3s first, so members called together can differ in speed
        if ($prompt.Substring(0, $prompt.Length - $body.Length) -match 'SLOW') { Start-Sleep -Seconds 3 }
        Emit ('{"type":"result","subtype":"success","is_error":false,"result":' + (ConvertTo-Json ($body.Trim() -replace '#', '@') -Compress) + ',"total_cost_usd":0.001,"permission_denials":[]}')
    }
    { $_.StartsWith('(') } {
        # a chained call (the app's note starts with '('): success answer = the whole prompt, so the test sees what was sent and that
        # mentions inside it (the caller's answer) do not chain again
        Emit ('{"type":"result","subtype":"success","is_error":false,"result":' + (ConvertTo-Json $prompt -Compress) + ',"total_cost_usd":0.001,"permission_denials":[]}')
    }
    { $_ -in 'say', 'shrink' } {
        # screen tests (ChatScriptTests): 'say N' appends the user line + N conductor lines (say-1..say-N) to the transcript like the real CLI;
        # 'shrink' rewrites the transcript to its first line + the user line + one conductor line (the record got shorter).
        # ASCII only - Windows PowerShell reads this file as ANSI.
        $utf8 = New-Object Text.UTF8Encoding $false
        $now = [DateTime]::UtcNow
        # 'o' is culture-independent (custom formats follow the time separator and calendar of the current culture)
        $stamp = { param($i) $now.AddMilliseconds($i).ToString('o') }
        $lines = @('{"type":"user","timestamp":"' + (& $stamp 0) + '","message":{"role":"user","content":' + (ConvertTo-Json $body.Trim() -Compress) + '}}')
        $count = if ($mode -eq 'say') { [int](($body.Trim() -split '\s+')[1]) } else { 1 }
        for ($i = 1; $i -le $count; $i++) {
            $text = if ($mode -eq 'say') { "say-$i" } else { 'shrunk' }
            $lines += '{"type":"assistant","timestamp":"' + (& $stamp $i) + '","message":{"role":"assistant","content":[{"type":"text","text":"' + $text + '"}]}}'
        }
        # the app reads transcripts with FileShare.ReadWrite, so no retry; a failed write must not look like success
        try {
            if ($mode -eq 'shrink') { $lines = @([IO.File]::ReadAllLines($Transcript, $utf8)[0]) + $lines; [IO.File]::WriteAllText($Transcript, (($lines -join "`n") + "`n"), $utf8) }
            else { [IO.File]::AppendAllText($Transcript, (($lines -join "`n") + "`n"), $utf8) }
            Emit '{"type":"result","subtype":"success","is_error":false,"result":"said","total_cost_usd":0.001,"permission_denials":[]}'
        } catch {
            Emit ('{"type":"result","subtype":"error_during_execution","is_error":true,"result":' + (ConvertTo-Json ('transcript write failed: ' + $_.Exception.Message) -Compress) + ',"total_cost_usd":0.001,"permission_denials":[]}')
        }
    }
    'roomtools' {
        # room servers: unity editor_status (expect allow, no card), unity eval (card; test lowers the level then denies), editor_status again (expect deny)
        $r1 = Ask 'req_u1' '{"subtype":"can_use_tool","tool_name":"mcp__unity__editor_status","input":{},"tool_use_id":"toolu_u1"}'
        $r2 = Ask 'req_u2' '{"subtype":"can_use_tool","tool_name":"mcp__unity__eval","input":{"code":"Debug.Log(1);"},"tool_use_id":"toolu_u2"}'
        $r3 = Ask 'req_u3' '{"subtype":"can_use_tool","tool_name":"mcp__unity__editor_status","input":{},"tool_use_id":"toolu_u3"}'
        $text = 'r1=' + $r1.behavior + ' r2=' + $r2.behavior + ' r3=' + $r3.behavior
        if ($text -eq 'r1=allow r2=deny r3=deny') {
            Emit '{"type":"result","subtype":"success","is_error":false,"result":"roomtools ok","total_cost_usd":0.001,"permission_denials":[]}'
        } else {
            Emit ('{"type":"result","subtype":"error_during_execution","is_error":true,"result":' + (ConvertTo-Json $text -Compress) + ',"total_cost_usd":0.001,"permission_denials":[]}')
        }
    }
    'unityshot' {
        # unity capture tool_use + tool_result image (kept), editor_status image (not kept)
        Emit '{"type":"assistant","message":{"model":"opus","content":[{"type":"tool_use","id":"toolu_u1","name":"mcp__unity__capture_game_view","input":{}},{"type":"tool_use","id":"toolu_u2","name":"mcp__unity__editor_status","input":{}}]}}'
        Emit '{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_u1","content":[{"type":"image","source":{"type":"base64","media_type":"image/png","data":"iVBORw0KGgo="}},{"type":"image","source":{"type":"base64","media_type":"image/jpeg","data":"/9j/4AAQ"}}]},{"type":"tool_result","tool_use_id":"toolu_u2","content":[{"type":"image","source":{"type":"base64","media_type":"image/png","data":"iVBORw0KGgo="}}]}]}}'
        Emit '{"type":"result","subtype":"success","is_error":false,"result":"shot","total_cost_usd":0.001,"permission_denials":[]}'
    }
    'runbubble' {
        # external bubble mid-turn (screen test): run_mode tool_use + its result (runId header) reach the transcript and the stream,
        # then a held card keeps the turn going while the test looks for the bubble in the live pane. The test writes the call record.
        $utf8 = New-Object Text.UTF8Encoding $false
        $now = [DateTime]::UtcNow
        $stamp = { param($i) $now.AddMilliseconds($i).ToString('o') }
        # sessionId on every line like the real CLI (the run reader needs it to read the run_mode calls)
        $sidPart = '"sessionId":"' + $Sid + '",'
        $use = '{"type":"assistant",' + $sidPart + '"timestamp":"' + (& $stamp 1) + '","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_run1","name":"mcp__hb-orchestration__run_mode","input":{"mode":"review_main"}}]}}'
        $res = '{"type":"user",' + $sidPart + '"timestamp":"' + (& $stamp 2) + '","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_run1","content":"runId: 0a1b2c3d4e\n### 1. Reviewer"}]}}'
        # build the user line first: ',' binds tighter than '+', so it cannot sit inside the @(...) list with the concatenation
        $said = '{"type":"user",' + $sidPart + '"timestamp":"' + (& $stamp 0) + '","message":{"role":"user","content":' + (ConvertTo-Json $body.Trim() -Compress) + '}}'
        $lines = @($said, $use, $res)
        New-Item -ItemType Directory -Force (Split-Path $Transcript) | Out-Null
        [IO.File]::AppendAllText($Transcript, (($lines -join "`n") + "`n"), $utf8)
        Emit $use
        Emit $res
        $d = Ask 'req_hold' '{"subtype":"can_use_tool","tool_name":"Bash","display_name":"Bash","input":{"command":"echo hold"},"tool_use_id":"toolu_hold"}'
        Finish $d 'Bash' 'reviewed'
    }
    'runlate' {
        # external bubble whose call record is written after the turn ends (screen test): run_mode result + a conductor line, then the turn ends at once.
        # The test writes the call record afterwards; the late retry must show it in place, not in the live pane.
        $utf8 = New-Object Text.UTF8Encoding $false
        $now = [DateTime]::UtcNow
        $stamp = { param($i) $now.AddMilliseconds($i).ToString('o') }
        $sidPart = '"sessionId":"' + $Sid + '",'
        $use = '{"type":"assistant",' + $sidPart + '"timestamp":"' + (& $stamp 1) + '","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_late1","name":"mcp__hb-orchestration__run_mode","input":{"mode":"review_main"}}]}}'
        $res = '{"type":"user",' + $sidPart + '"timestamp":"' + (& $stamp 2) + '","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_late1","content":"runId: 0f0e0d0c0b\n### 1. Reviewer"}]}}'
        $text = '{"type":"assistant",' + $sidPart + '"timestamp":"' + (& $stamp 3) + '","message":{"role":"assistant","content":[{"type":"text","text":"after review"}]}}'
        $said = '{"type":"user",' + $sidPart + '"timestamp":"' + (& $stamp 0) + '","message":{"role":"user","content":' + (ConvertTo-Json $body.Trim() -Compress) + '}}'
        $lines = @($said, $use, $res, $text)
        New-Item -ItemType Directory -Force (Split-Path $Transcript) | Out-Null
        [IO.File]::AppendAllText($Transcript, (($lines -join "`n") + "`n"), $utf8)
        Emit $use
        Emit $res
        Emit '{"type":"result","subtype":"success","is_error":false,"result":"after review","total_cost_usd":0.001,"permission_denials":[]}'
    }
    'stream' {
        # group live text (screen test): a tool and two text pieces, then wait for the test's go file (<tasks>/go-<session id>, up to 30s), then the rest
        Emit '{"type":"assistant","message":{"content":[{"type":"tool_use","id":"ts","name":"Read","input":{"file_path":"C:\\ws\\notes.md"}}]}}'
        Emit '{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Hel"}}}'
        Emit '{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"lo"}}}'
        $go = Join-Path $TasksRoot ('go-' + $Sid)
        for ($i = 0; $i -lt 300 -and -not (Test-Path $go); $i++) { Start-Sleep -Milliseconds 100 }
        Emit '{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":" world"}}}'
        Emit '{"type":"result","subtype":"success","is_error":false,"result":"Hello world","total_cost_usd":0.001,"permission_denials":[]}'
    }
    'sleep' {
        Emit '{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"wait"}}}'
        Start-Sleep -Seconds 60
    }
    'fail' {
        Emit '{"type":"result","subtype":"error_during_execution","is_error":true,"result":"boom","total_cost_usd":0.002,"permission_denials":[]}'
    }
    'nores' {
        [Console]::Error.WriteLine('fake stderr')
        exit 3
    }
    'other' {
        Start-Sleep -Seconds 60
    }
    'approve' {
        Emit '{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","name":"Bash","input":{}}}}'
        $d = Ask 'req_bash' ('{"subtype":"can_use_tool","tool_name":"Bash","display_name":"Bash","input":{"command":"git status","description":"Show status"},"description":"Show status",' +
            '"permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"git status"}],"behavior":"allow","destination":"localSettings"}],' +
            '"decision_reason":"This command requires approval","decision_reason_type":"other","tool_use_id":"toolu_fake1"}')
        # 허용이면 updatedInput은 받은 입력 그대로여야 한다 (앱이 명령을 바꾸지 않음)
        if ($d -and $d.behavior -eq 'allow' -and $d.updatedInput.command -ne 'git status') { $d = [pscustomobject]@{ behavior = 'deny'; message = 'updatedInput changed' } }
        Finish $d 'Bash' 'allowed'
    }
    'approve3' {
        # turn trust: three commands - first asks, second skips the card once trusted, third (git push) always asks.
        $d = $null
        foreach ($c in @(@('req_1', 'echo one'), @('req_2', 'echo two'), @('req_3', 'git push'))) {
            $d = Ask $c[0] ('{"subtype":"can_use_tool","tool_name":"Bash","display_name":"Bash","input":{"command":"' + $c[1] + '"},"tool_use_id":"toolu_' + $c[0] + '"}')
            if (-not $d -or $d.behavior -ne 'allow') { break }
        }
        Finish $d 'Bash' 'allowed'
    }
    'asktext' {
        # question mid-turn: text, answer, more text, then a held command card so the live pane stays until the test ends the turn.
        Emit '{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"before"}}}'
        $d = Ask 'req_ask' '{"subtype":"can_use_tool","tool_name":"AskUserQuestion","display_name":"AskUserQuestion","input":{"questions":[{"question":"Which color?","header":"Color","options":[{"label":"Red","description":"warm"},{"label":"Blue","description":"cool"}],"multiSelect":false}]},"tool_use_id":"toolu_q1","requires_user_interaction":true}'
        Emit '{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"after"}}}'
        $d = Ask 'req_hold' '{"subtype":"can_use_tool","tool_name":"Bash","display_name":"Bash","input":{"command":"echo hold"},"tool_use_id":"toolu_hold"}'
        Finish $d 'Bash' 'allowed'
    }
    'commit' {
        # text that reached the transcript (assistant line), then text still streaming, then a held card. Text blocks announce themselves (2.1.312).
        Emit '{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}}'
        Emit '{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"one"}}}'
        Emit '{"type":"assistant","message":{"usage":{"input_tokens":2,"output_tokens":8}}}'
        Emit '{"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}}'
        Emit '{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"two"}}}'
        $d = Ask 'req_hold' '{"subtype":"can_use_tool","tool_name":"Bash","display_name":"Bash","input":{"command":"echo hold"},"tool_use_id":"toolu_hold"}'
        Finish $d 'Bash' 'allowed'
    }
    'narrate' {
        # blocks arrive one assistant line each (2.1.312): empty real thinking, a server summary, a plain text block, a held card, then the last text block = the answer.
        Emit '{"type":"assistant","message":{"usage":{"input_tokens":2,"output_tokens":8},"content":[{"type":"thinking","thinking":"","signature":"CAQSDAoKQgh0aGlua2luZw=="}]}}'
        Emit '{"type":"assistant","message":{"usage":{"input_tokens":2,"output_tokens":8},"content":[{"type":"thinking","thinking":"middle summary\n\n","signature":"CAQSDQoLQgluYXJyYXRpb24="}]}}'
        Emit '{"type":"assistant","message":{"usage":{"input_tokens":2,"output_tokens":8},"content":[{"type":"text","text":"middle text"}]}}'
        $d = Ask 'req_hold' '{"subtype":"can_use_tool","tool_name":"Bash","display_name":"Bash","input":{"command":"echo hold"},"tool_use_id":"toolu_hold"}'
        Emit '{"type":"assistant","message":{"usage":{"input_tokens":2,"output_tokens":8},"content":[{"type":"text","text":"final answer"}]}}'
        Finish $d 'Bash' 'final answer'
    }
    'echoall' {
        # group context test: the answer is everything the member received.
        Emit ('{"type":"result","subtype":"success","is_error":false,"result":' + (ConvertTo-Json $prompt -Compress) + ',"total_cost_usd":0.001,"permission_denials":[]}')
    }
    'clideny' {
        # CLI's own rule denials: empty reason with a known tool call, then no reason and no tool name.
        Emit '{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t_deny","name":"Edit","input":{"file_path":"C:\\ws\\denied.md"}}]}}'
        Emit '{"type":"system","subtype":"permission_denied","tool_name":"Edit","tool_use_id":"t_deny","decision_reason":""}'
        Emit '{"type":"system","subtype":"permission_denied"}'
        Finish ([pscustomobject]@{ behavior = 'allow' }) 'Edit' 'done'
    }
    'terminal' {
        # terminal side panel: app MCP terminal_start permission request (step-0 probe shape). Allow must carry only name + command back.
        $d = Ask 'req_term' '{"subtype":"can_use_tool","tool_name":"mcp__hb-chat__terminal_start","display_name":"terminal_start","input":{"name":"web","command":"python -m http.server 8000","extra":"x"},"tool_use_id":"toolu_fake11"}'
        if ($d -and $d.behavior -eq 'allow' -and ($d.updatedInput.name -ne 'web' -or $d.updatedInput.command -ne 'python -m http.server 8000' -or $null -ne $d.updatedInput.extra)) {
            $d = [pscustomobject]@{ behavior = 'deny'; message = 'updatedInput changed' }
        }
        Finish $d 'mcp__hb-chat__terminal_start' 'allowed'
    }
    'browse' {
        # agent browser: browser_open for an outside site (card), localhost (no card), then another page on the approved site (no card).
        # Each allow must carry only the url back.
        $d1 = Ask 'req_b1' '{"subtype":"can_use_tool","tool_name":"mcp__hb-chat__browser_open","input":{"url":"https://example.com/a","extra":1},"tool_use_id":"toolu_b1"}'
        if (-not $d1 -or $d1.behavior -ne 'allow' -or $d1.updatedInput.url -ne 'https://example.com/a' -or $null -ne $d1.updatedInput.extra) { Finish $d1 'browser_open' 'x'; break }
        $d2 = Ask 'req_b2' '{"subtype":"can_use_tool","tool_name":"mcp__hb-chat__browser_open","input":{"url":"http://localhost:3000/"},"tool_use_id":"toolu_b2"}'
        $d3 = Ask 'req_b3' '{"subtype":"can_use_tool","tool_name":"mcp__hb-chat__browser_open","input":{"url":"https://example.com/b"},"tool_use_id":"toolu_b3"}'
        if ($d2.behavior -eq 'allow' -and $d3.behavior -eq 'allow') { Finish $d3 'browser_open' 'browsed' } else { Finish ([pscustomobject]@{ behavior = 'deny'; message = 'later open denied' }) 'browser_open' 'x' }
    }
    'ask' {
        # 3차 0번 실측 A의 AskUserQuestion 모양 (질문 둘: 하나 고르기 · 여러 개)
        Emit '{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","name":"AskUserQuestion","input":{}}}}'
        $d = Ask 'req_ask' ('{"subtype":"can_use_tool","tool_name":"AskUserQuestion","display_name":"AskUserQuestion","input":{"questions":[' +
            '{"question":"Which color?","header":"Color","options":[{"label":"Red","description":"warm"},{"label":"Blue","description":"cool"}],"multiSelect":false},' +
            '{"question":"Which sides?","header":"Sides","options":[{"label":"Fries"},{"label":"Salad"},{"label":"Soup"}],"multiSelect":true}]},' +
            '"tool_use_id":"toolu_fake2","requires_user_interaction":true}')
        # 허용이면 updatedInput = 원래 질문 + answers(질문 글 → 답)여야 한다 (0번 실측 A)
        if ($d -and $d.behavior -eq 'allow' -and (-not $d.updatedInput.answers.'Which color?' -or -not $d.updatedInput.answers.'Which sides?' -or $d.updatedInput.questions.Count -ne 2)) {
            $d = [pscustomobject]@{ behavior = 'deny'; message = 'answers missing in updatedInput' }
        }
        Finish $d 'AskUserQuestion' 'answered'
    }
    'cancel' {
        # CLI가 낸 승인 요청을 거둠 (control_cancel_request — SDK 규약 모양, /self-review 7d839f4aa5 C1). 거둔 요청에는 답을 기다리지 않는다.
        Emit ('{"type":"control_request","request_id":"req_gone","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"git status"},"tool_use_id":"toolu_fake4"}}')
        Start-Sleep -Milliseconds 1500
        Emit '{"type":"control_cancel_request","request_id":"req_gone"}'
        Start-Sleep -Milliseconds 1500
        Emit '{"type":"result","subtype":"success","is_error":false,"result":"withdrawn","total_cost_usd":0.001,"permission_denials":[]}'
    }
    'tasks' {
        # 할 일 도구 (3차 0번 실측 B): TaskCreate 둘 → 파일 → TaskUpdate → 파일 → 글. 앱은 도구 다음 출력에서 폴더를 다시 읽는다.
        $dir = Join-Path $TasksRoot $Sid
        New-Item -ItemType Directory -Force $dir | Out-Null
        # 앱이 그 파일을 읽는 순간과 겹치면 쓰기가 실패할 수 있다 — 다시 시도한다 (앞서 다섯 번에 한 번꼴로 마지막 상태가 pending으로 남던 시험 실패, 4차 C3)
        function Put([string]$path, [string]$text) {
            for ($i = 0; $i -lt 40; $i++) {
                try { [System.IO.File]::WriteAllText($path, $text); return } catch { Start-Sleep -Milliseconds 25 }
            }
            throw "할 일 파일을 쓰지 못함: $path"
        }
        Emit '{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"TaskCreate","input":{"subject":"alpha","description":"a"}},{"type":"tool_use","id":"t2","name":"TaskCreate","input":{"subject":"beta","description":"b"}}]}}'
        Put (Join-Path $dir '1.json') '{"id":"1","subject":"alpha","description":"a","status":"pending","blocks":[],"blockedBy":[]}'
        Put (Join-Path $dir '2.json') '{"id":"2","subject":"beta","description":"b","status":"pending","blocks":[],"blockedBy":[]}'
        Emit '{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t3","name":"TaskUpdate","input":{"taskId":"1","status":"in_progress"}}]}}'
        Put (Join-Path $dir '1.json') '{"id":"1","subject":"alpha","description":"a","status":"in_progress","blocks":[],"blockedBy":[]}'
        Emit '{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"working"}}}'
        Emit '{"type":"system","subtype":"post_turn_summary","status_category":"blocked","status_detail":"step alpha in progress","needs_action":"check alpha"}'
        Emit '{"type":"result","subtype":"success","is_error":false,"result":"working","total_cost_usd":0.001,"permission_denials":[]}'
    }
    'plan' {
        # plan approval (feature C, 2.1.312 shape): ExitPlanMode {plan, planFilePath}. Prompt "plan <mode>": allow must carry setMode <mode>
        $want = ($prompt.Trim() -split '\s+')[1]
        # input also carries a hidden key (allowedPrompts) that the app must drop from updatedInput (security review d3dd55b237 C1)
        $d = Ask 'req_plan' '{"subtype":"can_use_tool","tool_name":"ExitPlanMode","display_name":"ExitPlanMode","input":{"plan":"# Plan\n1. edit notes","planFilePath":"C:\\Users\\x\\.claude\\plans\\p.md","allowedPrompts":[{"tool":"Bash","prompt":"run anything"}]},"permission_suggestions":null,"tool_use_id":"toolu_fake5"}'
        if ($d -and $d.behavior -eq 'allow' -and ($d.updatedInput.plan -ne "# Plan`n1. edit notes" -or $null -ne $d.updatedInput.allowedPrompts -or $d.updatedPermissions[0].type -ne 'setMode' -or [string]$d.updatedPermissions[0].mode -ne $want)) {
            $d = [pscustomobject]@{ behavior = 'deny'; message = ('bad plan answer: mode ' + [string]$d.updatedPermissions[0].mode) }
        }
        Finish $d 'ExitPlanMode' 'planned'
    }
    'runmode' {
        # model picker card: run_mode asks the app (not pre-allowed). allow -> error result whose text is 'ALLOWED ' + updatedInput JSON
        # (like echo: only error results carry their text to the end event) so the test reads what the app sent back
        $d = Ask 'req_rm' '{"subtype":"can_use_tool","tool_name":"mcp__hb-orchestration__run_mode","display_name":"run_mode","input":{"mode":"design_main","input":"check this design","reason":"pick one","files":["a.cs"],"reasoning":["*=low"]},"tool_use_id":"toolu_rm1"}'
        if ($d -and $d.behavior -eq 'allow') {
            Emit ('{"type":"result","subtype":"error_during_execution","is_error":true,"result":' + (ConvertTo-Json ('ALLOWED ' + ($d.updatedInput | ConvertTo-Json -Compress -Depth 5)) -Compress) + ',"total_cost_usd":0.001,"permission_denials":[]}')
        } else { Finish $d 'mcp__hb-orchestration__run_mode' 'ran' }
    }
    'agent' {
        # subagent (feature D, 2.1.312 shape): task_started -> task_progress -> subagent Bash approval with agent_id -> task_notification
        Emit '{"type":"system","subtype":"task_started","task_id":"t_sub1","tool_use_id":"toolu_a","description":"Make dir","subagent_type":"general-purpose","is_backgrounded":false}'
        Emit '{"type":"system","subtype":"task_progress","task_id":"t_sub1","tool_use_id":"toolu_a","description":"Running mkdir","subagent_type":"general-purpose","usage":{"total_tokens":10,"tool_uses":1,"duration_ms":5}}'
        $d = Ask 'req_sub' '{"subtype":"can_use_tool","tool_name":"Bash","display_name":"Bash","input":{"command":"mkdir subdir","description":"Create subdir"},"agent_id":"a1b2c3","tool_use_id":"toolu_fake6"}'
        $status = if ($d -and $d.behavior -eq 'allow') { 'completed' } else { 'failed' }
        Emit ('{"type":"system","subtype":"task_notification","task_id":"t_sub1","tool_use_id":"toolu_a","status":"' + $status + '","subagent_type":"general-purpose"}')
        Finish $d 'Bash' 'sub done'
    }
    'subsay' {
        # subagent words (2.1.316 probe shape): subagent assistant lines carry parent_tool_use_id = the Agent call, no deltas;
        # task_notification carries summary = the final answer. A line for an unknown parent is dropped.
        Emit '{"type":"system","subtype":"task_started","task_id":"t_say","tool_use_id":"toolu_s","description":"Check apples","subagent_type":"general-purpose","is_backgrounded":false,"task_type":"local_agent"}'
        Emit '{"type":"assistant","parent_tool_use_id":"toolu_s","message":{"role":"assistant","content":[{"type":"text","text":"apples are red"}]}}'
        Emit '{"type":"assistant","parent_tool_use_id":"toolu_other","message":{"role":"assistant","content":[{"type":"text","text":"lost words"}]}}'
        $d = Ask 'req_say' '{"subtype":"can_use_tool","tool_name":"Bash","display_name":"Bash","input":{"command":"echo hi","description":"Say hi"},"agent_id":"say1","tool_use_id":"toolu_fake_say"}'
        Emit '{"type":"assistant","parent_tool_use_id":"toolu_s","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_fake_say","name":"Bash","input":{"command":"echo hi"}},{"type":"text","text":"second line"}]}}'
        Emit '{"type":"system","subtype":"task_notification","task_id":"t_say","tool_use_id":"toolu_s","status":"completed","summary":"FINAL apples"}'
        # the conductor's own command after the subagent: the turn waits here so a test can see the finished bubble
        $d2 = Ask 'req_say2' '{"subtype":"can_use_tool","tool_name":"Bash","display_name":"Bash","input":{"command":"echo after","description":"After"},"tool_use_id":"toolu_fake_say2"}'
        Finish $d2 'Bash' 'say done'
    }
    'roomopen' {
        # the conductor asks to open a new room (app MCP room_open) with a higher level than a read room: the host card answers; allow -> the edited input back
        $d = Ask 'req_ro' '{"subtype":"can_use_tool","tool_name":"mcp__hb-chat__room_open","display_name":"room_open","input":{"workspace":"ws","level":"app","model":"opus","title":"handoff","prompt":"carry on"},"tool_use_id":"toolu_ro1"}'
        if ($d -and $d.behavior -eq 'allow') {
            Emit ('{"type":"result","subtype":"error_during_execution","is_error":true,"result":' + (ConvertTo-Json ('ALLOWED ' + ($d.updatedInput | ConvertTo-Json -Compress -Depth 5)) -Compress) + ',"total_cost_usd":0.001,"permission_denials":[]}')
        } else { Finish $d 'mcp__hb-chat__room_open' 'opened' }
    }
    'subhand' {
        # a subagent that hands its report back with the SubagentHandback tool (2026-11-03 app session): the notification summary is only a notice
        Emit '{"type":"system","subtype":"task_started","task_id":"t_hand","tool_use_id":"toolu_hand","description":"Hand back","subagent_type":"Explore","is_backgrounded":false,"task_type":"local_agent"}'
        Emit '{"type":"assistant","parent_tool_use_id":"toolu_hand","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_hb","name":"SubagentHandback","input":{"message":"HANDED report"}}]}}'
        Emit '{"type":"system","subtype":"task_notification","task_id":"t_hand","tool_use_id":"toolu_hand","status":"completed","summary":"This agent''s report was delivered to you as a message from \"t_hand\" (its SubagentHandback call)."}'
        Emit '{"type":"result","subtype":"success","is_error":false,"result":"handed","total_cost_usd":0.001,"permission_denials":[]}'
    }
    'subcut' {
        # a foreground subagent cut off without task_notification (interrupt / error): started, one line, then the turn just ends
        Emit '{"type":"system","subtype":"task_started","task_id":"t_cut","tool_use_id":"toolu_cut","description":"Cut off","subagent_type":"general-purpose","is_backgrounded":false,"task_type":"local_agent"}'
        Emit '{"type":"assistant","parent_tool_use_id":"toolu_cut","message":{"role":"assistant","content":[{"type":"text","text":"half way"}]}}'
        Emit '{"type":"result","subtype":"success","is_error":false,"result":"cut","total_cost_usd":0.001,"permission_denials":[]}'
    }
    'bgagent' {
        # background subagent (feature D, real check 2026-10-28 / 2.1.316 shape): the turn ends first, then the subagent asks for approval while the
        # process rests, then task_notification, the task list empties and an automatic turn (init + result) follows.
        TaskList '{"task_id":"t_bg1","task_type":"local_agent","subagent_type":"general-purpose","description":"Make dir"}'
        Emit '{"type":"system","subtype":"task_started","task_id":"t_bg1","tool_use_id":"toolu_b","description":"Make dir","subagent_type":"general-purpose","is_backgrounded":true,"task_type":"local_agent"}'
        Emit '{"type":"result","subtype":"success","is_error":false,"result":"launched","total_cost_usd":0.001,"permission_denials":[]}'
        Emit '{"type":"assistant","parent_tool_use_id":"toolu_b","message":{"role":"assistant","content":[{"type":"text","text":"bg words"}]}}'
        $d = Ask 'req_bg' '{"subtype":"can_use_tool","tool_name":"Bash","display_name":"Bash","input":{"command":"mkdir bgdir","description":"Create bgdir"},"agent_id":"bg1","tool_use_id":"toolu_fake7"}'
        $status = if ($d -and $d.behavior -eq 'allow') { 'completed' } else { 'failed' }
        Emit ('{"type":"system","subtype":"task_notification","task_id":"t_bg1","tool_use_id":"toolu_b","status":"' + $status + '","subagent_type":"general-purpose","summary":"bg final"}')
        TaskList ''
        Init $Sid
        Finish $d 'Bash' 'bg done'
    }
    'bghang' {
        # background subagent that never reports done (security review 42f9d47dc4 C2): the turn ends, the process rests until the cap / wrap-up / stop
        TaskList '{"task_id":"t_hang","task_type":"local_agent","subagent_type":"general-purpose","description":"Never ends"}'
        Emit '{"type":"system","subtype":"task_started","task_id":"t_hang","tool_use_id":"toolu_c","description":"Never ends","subagent_type":"general-purpose","is_backgrounded":true,"task_type":"local_agent"}'
        Emit '{"type":"result","subtype":"success","is_error":false,"result":"launched","total_cost_usd":0.001,"permission_denials":[]}'
    }
    'bgmisc' {
        # C2: a task without subagent_type is not waited for, and a late progress line after done does not re-open the task
        Emit '{"type":"system","subtype":"task_started","task_id":"t_plain","description":"plain task"}'
        Emit '{"type":"system","subtype":"task_started","task_id":"t_late","description":"Quick","subagent_type":"general-purpose","is_backgrounded":true}'
        Emit '{"type":"system","subtype":"task_notification","task_id":"t_late","status":"completed"}'
        Emit '{"type":"system","subtype":"task_progress","task_id":"t_late","description":"late line","subagent_type":"general-purpose"}'
        Emit '{"type":"result","subtype":"success","is_error":false,"result":"misc","total_cost_usd":0.001,"permission_denials":[]}'
    }
    'bgbash' {
        # background Bash (real check 2026-10-29): approval input has run_in_background=true, task_started task_type local_bash,
        # main result first, then task_notification (no task_type) and a second result. The app must keep stdin open for the second answer.
        $d = Ask 'req_bgb' '{"subtype":"can_use_tool","tool_name":"Bash","display_name":"Bash","input":{"command":"python job.py","description":"Run job","run_in_background":true},"tool_use_id":"toolu_fake8"}'
        if (-not $d -or $d.behavior -ne 'allow') { Finish $d 'Bash' 'no'; break }
        TaskList '{"task_id":"b_job","task_type":"local_bash","description":"Run job"}'
        Emit '{"type":"system","subtype":"task_started","task_id":"b_job","tool_use_id":"toolu_fake8","description":"Run job","is_backgrounded":true,"task_type":"local_bash"}'
        Emit '{"type":"result","subtype":"success","is_error":false,"result":"started","total_cost_usd":0.001,"permission_denials":[]}'
        Start-Sleep -Milliseconds 1500
        Emit '{"type":"system","subtype":"task_notification","task_id":"b_job","status":"completed","output_file":"C:\\tmp\\b_job.output"}'
        TaskList ''
        # the automatic turn after the job ended (2.1.316: a turn of its own, init first, no user line back)
        Init $Sid
        Emit '{"type":"result","subtype":"success","is_error":false,"result":"job finished","total_cost_usd":0.002,"permission_denials":[]}'
    }
    'ghost' {
        # the ghost lines came before init (above); the user turn itself asks for a command so the approval must reach the app
        Emit '{"type":"assistant","message":{"content":[{"type":"tool_use","id":"tg","name":"Bash","input":{"command":"git status"}}]}}'
        $d = Ask 'req_ghost' '{"subtype":"can_use_tool","tool_name":"Bash","display_name":"Bash","input":{"command":"git status","description":"Show status"},"tool_use_id":"tg"}'
        Finish $d 'Bash' 'after ghost'
    }
    'ghostfail' {
        # the early ghost result was an error without text (above, self-review 6f64f4b039 C1); the user turn then fails the same way and must end the turn
        Emit '{"type":"result","subtype":"error_during_execution","is_error":true,"total_cost_usd":0,"permission_denials":[]}'
    }
    'bgfromagent' {
        # a subagent asks to run a background command (C5): the card must show the subagent wait cap (30 min)
        $d = Ask 'req_bga' '{"subtype":"can_use_tool","tool_name":"Bash","display_name":"Bash","input":{"command":"python serve.py","description":"Serve","run_in_background":true},"agent_id":"a9","tool_use_id":"toolu_fake10"}'
        Finish $d 'Bash' 'ok'
    }
    'bgserver' {
        # background server that never ends: after stdin closes the real CLI keeps running (real check), so the app must kill it after a grace
        TaskList '{"task_id":"b_srv","task_type":"local_bash","description":"Serve"}'
        Emit '{"type":"system","subtype":"task_started","task_id":"b_srv","tool_use_id":"toolu_fake9","description":"Serve","is_backgrounded":true,"task_type":"local_bash"}'
        Emit '{"type":"result","subtype":"success","is_error":false,"result":"server up","total_cost_usd":0.001,"permission_denials":[]}'
        Start-Sleep -Seconds 120
        exit 0
    }
    'hangpid' {
        # server restart test: write this process id (<tasks>/pid-<session>), append a line to the transcript like a real turn, then wait on a Bash approval
        New-Item -ItemType Directory -Force $TasksRoot | Out-Null
        [IO.File]::WriteAllText((Join-Path $TasksRoot ('pid-' + $Sid)), [string]$PID)
        if ($Transcript) { New-Item -ItemType Directory -Force (Split-Path $Transcript) | Out-Null; Add-Content -Path $Transcript -Value '{"type":"user"}' }
        $d = Ask 'req_hang' '{"subtype":"can_use_tool","tool_name":"Bash","display_name":"Bash","input":{"command":"git status","description":"Show status"},"tool_use_id":"toolu_hang"}'
        Finish $d 'Bash' 'after restart?'
    }
    'hb-selftest' {
        # card self-test (CliSelfTest): Bash approval for the mkdir in the prompt (allowed -> the folder is made, like the real CLI) ->
        # AskUserQuestion -> text piece -> result (permission_denials = what was denied, like the real CLI).
        # The test passes a variant in place of TasksRoot (self-test does not use tasks):
        #   badcmd: asks a different mkdir | noask: skips the question | ignoreallow: allowed but the folder is not made (answer did not take)
        #   preallowed: no approval request, the folder is made anyway (user allow rule) | extra: one more Bash (ls) after the mkdir
        #   denyanswer: the question answer is counted as a denial
        $v = $TasksRoot
        $nonce = [regex]::Match($prompt, 'mkdir hb-selftest-([0-9a-f]+)').Groups[1].Value
        $dir = 'hb-selftest-' + $nonce
        $denials = @()
        if ($v -eq 'preallowed') { New-Item -ItemType Directory -Force (Join-Path (Get-Location) $dir) | Out-Null }
        else {
            $cmd = if ($v -eq 'badcmd') { 'mkdir something-else' } else { 'mkdir ' + $dir }
            $d1 = Ask 'req_st1' ('{"subtype":"can_use_tool","tool_name":"Bash","display_name":"Bash","input":{"command":"' + $cmd + '","description":"self test"},"tool_use_id":"toolu_st1"}')
            if ($d1 -and $d1.behavior -eq 'allow') { if ($v -ne 'ignoreallow') { New-Item -ItemType Directory -Force (Join-Path (Get-Location) $dir) | Out-Null } }
            else { $denials += '{"tool_name":"Bash"}' }
        }
        if ($v -eq 'extra') {
            $dx = Ask 'req_stx' '{"subtype":"can_use_tool","tool_name":"Bash","display_name":"Bash","input":{"command":"ls","description":"check"},"tool_use_id":"toolu_stx"}'
            if (-not $dx -or $dx.behavior -ne 'allow') { $denials += '{"tool_name":"Bash"}' }
        }
        if ($v -ne 'noask') {
            $d2 = Ask 'req_st2' '{"subtype":"can_use_tool","tool_name":"AskUserQuestion","display_name":"AskUserQuestion","input":{"questions":[{"question":"self test question","header":"test","options":[{"label":"yes"},{"label":"no"}],"multiSelect":false}]},"tool_use_id":"toolu_st2"}'
            if ($v -eq 'denyanswer' -or -not $d2 -or $d2.behavior -ne 'allow') { $denials += '{"tool_name":"AskUserQuestion"}' }
        }
        # web fetch of a site outside the CLI's built-in docs list ('noweb': the CLI did not ask)
        if ($v -ne 'noweb') {
            $d3 = Ask 'req_st3' ('{"subtype":"can_use_tool","tool_name":"WebFetch","display_name":"WebFetch","input":{"url":"https://example.com/' + $dir + '","prompt":"title"},"tool_use_id":"toolu_st3"}')
            if (-not $d3 -or $d3.behavior -ne 'allow') { $denials += '{"tool_name":"WebFetch"}' }
        }
        Emit '{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"self test done"}}}'
        Emit ('{"type":"result","subtype":"success","is_error":false,"result":"self test done","total_cost_usd":0.0001,"permission_denials":[' + ($denials -join ',') + ']}')
    }
    'websearch' {
        # web search: the app allows without a card (input back unchanged)
        $d = Ask 'req_ws' '{"subtype":"can_use_tool","tool_name":"WebSearch","display_name":"WebSearch","input":{"query":"dotnet 10 release notes"},"tool_use_id":"toolu_ws"}'
        if ($d -and $d.behavior -eq 'allow' -and $d.updatedInput.query -ne 'dotnet 10 release notes') { $d = [pscustomobject]@{ behavior = 'deny'; message = 'updatedInput changed' } }
        Finish $d 'WebSearch' 'searched'
    }
    'webfetch' {
        # web fetch 'webfetch <url> [expected url back]': site card (or no card if the room already allowed the site / the address is blocked).
        # Allow must carry only url (the normalized one) + prompt back (the extra key is dropped).
        $parts = $body.Trim() -split '\s+'
        $url = $parts[1]
        $want = if ($parts.Count -gt 2) { $parts[2] } else { $url }
        $d = Ask 'req_wf' ('{"subtype":"can_use_tool","tool_name":"WebFetch","display_name":"WebFetch","input":{"url":' + (ConvertTo-Json $url -Compress) + ',"prompt":"summarize","extra":1},"tool_use_id":"toolu_wf"}')
        if ($d -and $d.behavior -eq 'allow' -and ($d.updatedInput.url -ne $want -or $d.updatedInput.prompt -ne 'summarize' -or $null -ne $d.updatedInput.extra)) {
            $d = [pscustomobject]@{ behavior = 'deny'; message = ('updatedInput changed: ' + $d.updatedInput.url) }
        }
        Finish $d 'WebFetch' 'fetched'
    }
    'askthenweb' {
        # downgrade mid-run: a question card first (the test lowers the room to chat while it waits), then web search + web fetch of an allowed site
        $q = Ask 'req_aw1' '{"subtype":"can_use_tool","tool_name":"AskUserQuestion","display_name":"AskUserQuestion","input":{"questions":[{"question":"go?","header":"go","options":[{"label":"yes"},{"label":"no"}],"multiSelect":false}]},"tool_use_id":"toolu_aw1"}'
        $s = Ask 'req_aw2' '{"subtype":"can_use_tool","tool_name":"WebSearch","display_name":"WebSearch","input":{"query":"x"},"tool_use_id":"toolu_aw2"}'
        $f = Ask 'req_aw3' '{"subtype":"can_use_tool","tool_name":"WebFetch","display_name":"WebFetch","input":{"url":"https://example.com/b","prompt":"p"},"tool_use_id":"toolu_aw3"}'
        $text = 'search=' + $s.behavior + ' fetch=' + $f.behavior
        Emit ('{"type":"result","subtype":"error_during_execution","is_error":true,"result":' + (ConvertTo-Json $text -Compress) + ',"total_cost_usd":0.001,"permission_denials":[]}')
    }
    'readout' {
        # Read outside the workspace: the app has no read card - denied without a card, with the reason (user report 2026-10-30)
        $d = Ask 'req_ro' '{"subtype":"can_use_tool","tool_name":"Read","display_name":"Read","input":{"file_path":"C:\\outside\\a.txt"},"tool_use_id":"toolu_ro"}'
        Finish $d 'Read' 'read'
    }
    'writeto' {
        # outside-workspace write card (command rooms): 'writeto <path>' asks Write for that path with long content (over the 2,000-char preview).
        # Allow must carry the input back unchanged.
        $path = ($body.Trim() -split '\s+')[1]
        $content = 'x' * 2500
        $d = Ask 'req_wt' ('{"subtype":"can_use_tool","tool_name":"Write","display_name":"Write","input":{"file_path":' + (ConvertTo-Json $path -Compress) + ',"content":"' + $content + '"},"tool_use_id":"toolu_wt"}')
        if ($d -and $d.behavior -eq 'allow' -and ($d.updatedInput.file_path -ne $path -or $d.updatedInput.content -ne $content)) { $d = [pscustomobject]@{ behavior = 'deny'; message = 'updatedInput changed' } }
        Finish $d 'Write' 'written'
    }
    'write' {
        # 앱이 카드로 받지 않는 승인 요청 (작업 폴더 밖 쓰기) — 카드 없이 거절돼야 한다
        $d = Ask 'req_write' '{"subtype":"can_use_tool","tool_name":"Write","display_name":"Write","input":{"file_path":"C:\\outside\\x.txt","content":"x"},"tool_use_id":"toolu_fake3"}'
        Finish $d 'Write' 'written'
    }
    default {
        Emit '{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","name":"Read"}}}'
        Emit '{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t0","name":"Read","input":{"file_path":"C:\\ws\\notes.md"}}]}}'
        Emit '{"type":"system","subtype":"permission_denied","tool_name":"Write","decision_reason":"no approval surface"}'
        Emit '{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Hel"}}}'
        Emit '{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"lo"}}}'
        Emit '{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":100,"cache_read_input_tokens":900,"output_tokens":8}}}'
        Emit '{"type":"assistant","parent_tool_use_id":"toolu_sub","message":{"usage":{"input_tokens":5,"cache_read_input_tokens":99999,"output_tokens":1}}}'
        Emit '{"type":"rate_limit_event"}'
        Emit '{"type":"system","subtype":"post_turn_summary","status_category":"completed","status_detail":"said hello","needs_action":""}'
        Emit '{"type":"result","subtype":"success","is_error":false,"result":"Hello","total_cost_usd":0.0125,"permission_denials":[{"tool_name":"Write"}],"modelUsage":{"m":{"contextWindow":200000}}}'
    }
}
}
# stdin closed (the app decided the process is done) - end like the real CLI
