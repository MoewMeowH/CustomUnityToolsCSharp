# 가짜 claude: stdin 프롬프트의 첫 낱말로 동작을 고르고 stream-json 줄을 낸다 (RoomHubTests).
# 인자: 세션 ID 하나 (그 뒤에 붙는 인자는 무시). 줄 모양은 0번 실측(Claude Code 2.1.311)에서 본 것만 흉내 낸다.
param([string]$Sid)
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
    default {
        Emit '{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","name":"Read"}}}'
        Emit '{"type":"system","subtype":"permission_denied","tool_name":"Write","decision_reason":"no approval surface"}'
        Emit '{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Hel"}}}'
        Emit '{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"lo"}}}'
        Emit '{"type":"rate_limit_event"}'
        Emit '{"type":"result","subtype":"success","is_error":false,"result":"Hello","total_cost_usd":0.0125,"permission_denials":[{"tool_name":"Write"}]}'
    }
}
