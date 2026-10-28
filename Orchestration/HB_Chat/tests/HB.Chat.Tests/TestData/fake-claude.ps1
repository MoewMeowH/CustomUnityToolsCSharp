# 가짜 claude: stdin stream-json(SDK식 — initialize 제어 요청 → 사용자 말 한 줄)에서 사용자 말의 첫 낱말로 동작을 고르고 stream-json 줄을 낸다
# (RoomHubTests · ApprovalFlowTests). 인자: 세션 ID. 줄 모양은 0번 실측(Claude Code 2.1.311 · 2.1.312)에서 본 것만 흉내 낸다.
# approve · ask · write 모드: 3차 0번 실측의 control_request can_use_tool을 내고 stdin의 control_response를 기다려, 허용이면 성공 · 거절이면 오류 결과를 낸다.
# 두 번째 인자: 할 일 폴더(tasks 모드가 <폴더>/<세션>/<번호>.json을 쓴다 — 3차 b, 0번 실측 B 모양).
param([string]$Sid, [string]$TasksRoot)
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[Console]::InputEncoding = [Text.Encoding]::UTF8

function Emit([string]$line) { [Console]::Out.WriteLine($line); [Console]::Out.Flush() }

# 사용자 말이 올 때까지 읽는다 (initialize는 성공으로 답한다 — 진짜 CLI처럼).
$prompt = ''
while ($null -ne ($line = [Console]::In.ReadLine())) {
    $msg = $line | ConvertFrom-Json
    if ($msg.type -eq 'control_request' -and $msg.request.subtype -eq 'initialize') {
        Emit ('{"type":"control_response","response":{"subtype":"success","request_id":"' + $msg.request_id + '","response":{}}}')
    } elseif ($msg.type -eq 'user') {
        $prompt = [string]$msg.message.content
        break
    }
}
$mode = ($prompt.Trim() -split '\s+')[0]

# 승인 요청을 내고 같은 request_id의 답을 기다린다. 답(response 객체) 또는 입력이 끝나면 $null.
function Ask([string]$id, [string]$request) {
    Emit ('{"type":"control_request","request_id":"' + $id + '","request":' + $request + '}')
    while ($null -ne ($line = [Console]::In.ReadLine())) {
        $msg = $line | ConvertFrom-Json
        if ($msg.type -eq 'control_response' -and $msg.response.request_id -eq $id) { return $msg.response.response }
    }
    return $null
}

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

$useSid = if ($mode -eq 'other') { '00000000-0000-0000-0000-000000000000' } else { $Sid }
Emit ('{"type":"system","subtype":"init","session_id":"' + $useSid + '","claude_code_version":"fake","mcp_servers":[]}')

switch ($mode) {
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
        Emit '{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"TaskCreate","input":{"subject":"alpha","description":"a"}},{"type":"tool_use","id":"t2","name":"TaskCreate","input":{"subject":"beta","description":"b"}}]}}'
        Set-Content -Encoding UTF8 (Join-Path $dir '1.json') '{"id":"1","subject":"alpha","description":"a","status":"pending","blocks":[],"blockedBy":[]}'
        Set-Content -Encoding UTF8 (Join-Path $dir '2.json') '{"id":"2","subject":"beta","description":"b","status":"pending","blocks":[],"blockedBy":[]}'
        Emit '{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t3","name":"TaskUpdate","input":{"taskId":"1","status":"in_progress"}}]}}'
        Set-Content -Encoding UTF8 (Join-Path $dir '1.json') '{"id":"1","subject":"alpha","description":"a","status":"in_progress","blocks":[],"blockedBy":[]}'
        Emit '{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"working"}}}'
        Emit '{"type":"system","subtype":"post_turn_summary","status_category":"blocked","status_detail":"step alpha in progress","needs_action":"check alpha"}'
        Emit '{"type":"result","subtype":"success","is_error":false,"result":"working","total_cost_usd":0.001,"permission_denials":[]}'
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
# result 뒤에는 앱이 stdin을 닫는다 — 진짜 CLI처럼 입력 끝을 기다렸다가 끝낸다
while ($null -ne [Console]::In.ReadLine()) { }
