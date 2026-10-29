# fake codex (CodexTests): mimics `codex exec --json` events seen in the 2026-10-29 stage-0 probe.
# Args are the real CodexRunner.Args, passed as one base64 JSON array (PowerShell -File would read -C / -m as its own parameters);
# the prompt comes on stdin. The answer reports what the app passed:
#   "codex: resume=<bool> instr=<bool> model=<m> effort=<e> reminders=<system-reminder blocks in prompt> last=<last prompt line>"
# resume of thread "missing" behaves like a lost thread (stderr only, exit 1).
# Prompt modes (first word after any leading system-reminder block): fail -> error + turn.failed (exit 1), sleep -> wait 60s.
# ASCII only - Windows PowerShell reads this file as ANSI.
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[Console]::InputEncoding = [Text.Encoding]::UTF8
function Emit([string]$line) { [Console]::Out.WriteLine($line); [Console]::Out.Flush() }

$prompt = [Console]::In.ReadToEnd()
$body = [regex]::Replace($prompt, '(?s)^<system-reminder>.*?</system-reminder>\s*', '')
# mode = first word that is not a @mention (group messages start with @Name)
$mode = (($body.Trim() -split '\s+') | Where-Object { -not $_.StartsWith('@') } | Select-Object -First 1)

# Windows PowerShell 5.1 ConvertFrom-Json emits a JSON array as one object - enumerate it to get the items
$argv = @((ConvertFrom-Json ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($args[0])))) | ForEach-Object { $_ })
$resume = $false; $thread = $null; $instr = $false; $model = ''; $effort = ''
for ($i = 0; $i -lt $argv.Count; $i++) {
    if ($argv[$i] -ceq 'resume') { $resume = $true; $thread = $argv[$i + 1] }
    if ($argv[$i] -ceq '-m') { $model = $argv[$i + 1] }
    if ($argv[$i] -ceq '-c' -and $argv[$i + 1] -like 'developer_instructions=*') { $instr = $true }
    if ($argv[$i] -ceq '-c' -and $argv[$i + 1] -like 'model_reasoning_effort=*') { $effort = $argv[$i + 1].Substring(23).Trim('"') }
}
if ($thread -eq 'missing') {
    # real codex 0.165.0 with an unknown thread: no JSON events, one stderr line, exit 1 (stage-2 probe)
    [Console]::Error.WriteLine('Error: thread/resume: thread/resume failed: no rollout found for thread id missing (code -32600)')
    exit 1
}
if (-not $thread) { $thread = [guid]::NewGuid().ToString() }
$reminders = ([regex]::Matches($prompt, '<system-reminder>')).Count

Emit ('{"type":"thread.started","thread_id":"' + $thread + '"}')
Emit '{"type":"item.completed","item":{"id":"item_0","type":"error","message":"Under-development features enabled: prefer_mxc. Under-development features are incomplete."}}'
Emit '{"type":"turn.started"}'
if ($mode -eq 'sleep') { Start-Sleep -Seconds 60 }
if ($mode -eq 'fail') {
    Emit '{"type":"error","message":"boom"}'
    Emit '{"type":"turn.failed","error":{"message":"boom"}}'
    exit 1
}
Emit '{"type":"item.completed","item":{"id":"item_1","type":"command_execution","command":"Get-ChildItem","exit_code":0}}'
$last = ($body.Trim() -split "`n")[-1].Trim()
$text = "codex: resume=$resume instr=$instr model=$model effort=$effort reminders=$reminders last=$last"
Emit ('{"type":"item.completed","item":{"id":"item_2","type":"agent_message","text":' + (ConvertTo-Json $text -Compress) + '}}')
Emit '{"type":"turn.completed","usage":{"input_tokens":100,"cached_input_tokens":50,"cache_write_input_tokens":0,"output_tokens":7,"reasoning_output_tokens":0}}'
