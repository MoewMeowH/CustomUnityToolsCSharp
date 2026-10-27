# 가짜 claude: stdin 프롬프트의 첫 낱말로 동작을 고르고 stream-json 줄을 낸다 (RoomHubTests · ApprovalFlowTests).
# 인자: 세션 ID, 승인 훅 주소(명령 단계만, 없으면 빈 값). 줄 모양은 0번 실측(Claude Code 2.1.311)에서 본 것만 흉내 낸다.
# approve 모드: 2차 0번 실측 A의 훅 본문 모양으로 승인 훅을 부르고, 허용이면 성공 · 거절이면 오류 결과를 낸다.
param([string]$Sid, [string]$HookUrl)
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$prompt = [Console]::In.ReadToEnd()
$mode = ($prompt.Trim() -split '\s+')[0]

function Emit([string]$line) { [Console]::Out.WriteLine($line); [Console]::Out.Flush() }

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
        Emit '{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","name":"Bash"}}}'
        $body = '{"session_id":"' + $Sid + '","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"git status","description":"Show status"},' +
            '"permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"git status"}],"behavior":"allow","destination":"localSettings"}]}'
        try {
            $r = Invoke-RestMethod -Method Post -Uri $HookUrl -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType 'application/json; charset=utf-8' -TimeoutSec 120
            $d = $r.hookSpecificOutput.decision
        } catch {
            $d = $null
        }
        if ($d -and $d.behavior -eq 'allow') {
            Emit '{"type":"result","subtype":"success","is_error":false,"result":"allowed","total_cost_usd":0.001,"permission_denials":[]}'
        } elseif ($d) {
            $msg = ($d.message -replace '"', "'")
            Emit ('{"type":"result","subtype":"error_during_execution","is_error":true,"result":"denied: ' + $msg + '","total_cost_usd":0.001,"permission_denials":[{"tool_name":"Bash"}]}')
        } else {
            Emit '{"type":"result","subtype":"error_during_execution","is_error":true,"result":"hook error","total_cost_usd":0.001,"permission_denials":[{"tool_name":"Bash"}]}'
        }
    }
    default {
        Emit '{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","name":"Read"}}}'
        Emit '{"type":"system","subtype":"permission_denied","tool_name":"Write","decision_reason":"no approval surface"}'
        Emit '{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Hel"}}}'
        Emit '{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"lo"}}}'
        Emit '{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":100,"cache_read_input_tokens":900,"output_tokens":8}}}'
        Emit '{"type":"assistant","parent_tool_use_id":"toolu_sub","message":{"usage":{"input_tokens":5,"cache_read_input_tokens":99999,"output_tokens":1}}}'
        Emit '{"type":"rate_limit_event"}'
        Emit '{"type":"result","subtype":"success","is_error":false,"result":"Hello","total_cost_usd":0.0125,"permission_denials":[{"tool_name":"Write"}],"modelUsage":{"m":{"contextWindow":200000}}}'
    }
}
