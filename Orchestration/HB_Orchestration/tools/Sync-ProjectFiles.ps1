<#
.SYNOPSIS
    project-files(원본)를 Unity 프로젝트에 맞춘다. 기본은 비교만 한다.

.DESCRIPTION
    원본 방향: project-files가 원본이고 Unity 프로젝트 쪽은 복사본이다. 규칙을 고칠 때는 원본을 고친 뒤 이 스크립트로 복사한다.

    파일 구분
      동기화   CLAUDE.md, .claude/commands/*.md, .claude/scripts/*.py, .claude/agents/*.md, .claude/mistakes/*.md (원본 폴더에 있는 것)  바이트 동일해야 한다
               prompts/(외부 에이전트 덧붙임)는 서버가 PromptsPath로 읽는 서버 전용이라 동기화하지 않는다 (2026-10-27).
      없으면 만듦  REVIEW_GUIDELINES.md   프로젝트마다 고쳐 쓰는 리뷰 지침 (2026-10-22). 없을 때만 원본(기본 지침)을 복사하고,
               있으면 다르더라도 '프로젝트 지침'으로 보여 주기만 한다 — 멈추지 않고 덮지 않는다. 프로젝트 전용 규칙은 PROJECT.md(공통 CLAUDE.md가 불러옴).
               단 원본 git 이력의 옛 판과 같으면(프로젝트에서 고치지 않음) '기본 지침 옛 판'으로 맞출 대상에 넣는다 (-Apply가 갱신, -Force 불필요).
      확인만   .claude/settings.example.json → .claude/settings.json   덮지 않는다. 해시만 비교 (기존 파일이 있으면 allow 항목만 손으로 합친다)
      확인만   .mcp.example.json → .mcp.json                         덮지 않는다. 기기마다 경로가 달라 해시는 비교하지 않고 없음 · 자리표시 남음만 알린다

    템플릿은 실제 설정과 이름이 다르다. project-files를 폴더째 붙여넣어도 Unity 쪽 설정이 덮이지 않게 하기 위해서다.
    Unity 쪽에 템플릿 이름 파일이 있으면 'Unity에 템플릿 사본'으로 보여 주기만 한다. 확인만 행은 종료 코드에 영향이 없다.

    -Apply는 Unity 쪽 파일이 원본과 '다름'이면 아무것도 복사하지 않고 멈춘다 (Unity에서 직접 고친 내용을 지우지 않도록).
    그 내용을 원본에 먼저 합치거나, 버려도 되면 -Force로 덮는다. -Force는 덮기 전에 <Unity>/review_tmp/sync-backup/<시각>/에 백업한다.

    이스케이프 잔재(줄 맨 앞 \---, \_, \[, 1\. 등)가 보이면 경고만 한다 (.md 파일만). 종료 코드에는 영향이 없다.

    서브에이전트 정의(.claude/agents/*.md)는 비교 전에 원본을 check_agents.py로 검사한다. tools가 없거나(MCP 도구까지 상속) 허용 밖 도구가 있으면
    아무것도 비교·복사하지 않고 2로 끝낸다 (경고가 아니다, 계획서 2026-10-19_서브에이전트-도입-검토 공통 규칙 8).

    종료 코드: 0 동기화 대상이 모두 같음 / 1 다른 것이 있음(또는 -Apply가 멈춤) / 2 실행 오류(정의 파일 검사 실패 포함)

    -All: 이 스크립트 옆의 sync-targets.local.txt(한 줄에 프로젝트 폴더 하나, # 주석, git 제외)에 적힌 모든 대상을 비교만 하고 요약한다.
    원본(project-files)을 바꾸는 커밋을 할 때마다 돌려 뒤처진 대상을 찾는다 (2026-10-22: 세 프로젝트가 옛 훅으로 이틀 돈 일).
    -Apply와 같이 쓸 수 없다 (맞추는 것은 대상마다 확인한 뒤 따로). 종료 코드는 대상들 중 가장 나쁜 값 (2 > 1 > 0).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\Sync-ProjectFiles.ps1 -UnityProject F:\MyGame
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\Sync-ProjectFiles.ps1 -UnityProject F:\MyGame -Apply
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\Sync-ProjectFiles.ps1 -All
#>
[CmdletBinding()]
param(
    [string]$UnityProject = '',
    [switch]$Apply,
    [switch]$Force,
    [switch]$All,
    # 테스트용. 비우면 이 스크립트 옆의 ..\project-files
    [string]$Source = '',
    # 테스트용. 비우면 이 스크립트 옆의 sync-targets.local.txt
    [string]$TargetsFile = ''
)

$ErrorActionPreference = 'Stop'
# 예상하지 못한 예외도 '다름'(1)과 구분되게 2로 끝낸다
trap {
    Write-Host "[오류] $_"
    exit 2
}

# -All이 부른 자식: 출력을 UTF-8로 (부모가 같은 인코딩으로 읽음 — 콘솔 코드 페이지와 무관하게)
if ($env:SYNC_PROJECTFILES_MACHINE -eq '1') { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) }

function Fail([string]$message) {
    Write-Host "[오류] $message"
    exit 2
}

function Get-Hash([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Short([string]$hash) {
    # [string] 매개변수는 $null을 ''로 받는다
    if ([string]::IsNullOrEmpty($hash)) { return '-' }
    return $hash.Substring(0, 12)
}

# 규칙서에서 실제로 나온 이스케이프 잔재 모양 (원인 미확인). 코드 블록 안의 정상 사용도 걸릴 수 있어 경고만 한다.
$EscapePatterns = @(
    @{ Name = '\---';  Regex = '^\s*\\-{3}' },
    @{ Name = '\_';    Regex = '\\_' },
    @{ Name = '\[';    Regex = '\\\[' },
    @{ Name = 'N\.';   Regex = '^\s*\d+\\\.' },
    @{ Name = '\* \-'; Regex = '^\s*\\[*\-+]\s' }
)

function Find-EscapeRemnants([string]$path, [string]$label) {
    $found = @()
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $found }
    $lines = [System.IO.File]::ReadAllLines($path, [System.Text.UTF8Encoding]::new($false))
    for ($i = 0; $i -lt $lines.Length; $i++) {
        foreach ($p in $EscapePatterns) {
            if ($lines[$i] -match $p.Regex) {
                $text = $lines[$i].Trim()
                if ($text.Length -gt 60) { $text = $text.Substring(0, 60) + '…' }
                $found += "[경고] 이스케이프 잔재 의심 ($($p.Name)): $label`:$($i + 1)  $text"
                break
            }
        }
    }
    return $found
}

# --- 입력 확인 ---------------------------------------------------------------

if ($Force -and -not $Apply) { Fail '-Force는 -Apply와 함께 써야 합니다.' }

# --- 모든 대상 비교 (-All) ---------------------------------------------------
if ($All) {
    if ($Apply) { Fail '-All은 비교만 합니다. 맞추는 것은 대상마다 -UnityProject로 따로 하세요.' }
    if (-not [string]::IsNullOrWhiteSpace($UnityProject)) { Fail '-All과 -UnityProject는 같이 쓸 수 없습니다.' }
    if ([string]::IsNullOrWhiteSpace($TargetsFile)) { $TargetsFile = Join-Path $PSScriptRoot 'sync-targets.local.txt' }
    if (-not (Test-Path -LiteralPath $TargetsFile -PathType Leaf)) {
        Fail "대상 목록이 없습니다: $TargetsFile (한 줄에 프로젝트 폴더 하나, # 주석. 이 PC의 경로라 git 제외)"
    }
    # 대상 줄: 상대 경로는 목록 파일 폴더 기준, 끝 구분자는 뗀다 (5.1 네이티브 인수 인용에서 `"...\"`가 깨지지 않게)
    $listDir = Split-Path -Parent ((Resolve-Path -LiteralPath $TargetsFile).ProviderPath)
    $targets = @(Get-Content -LiteralPath $TargetsFile -Encoding UTF8 | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') } | ForEach-Object {
        $p = if ([System.IO.Path]::IsPathRooted($_)) { $_ } else { Join-Path $listDir $_ }
        $p = [System.IO.Path]::GetFullPath($p)
        if ($p.Length -gt 3) { $p.TrimEnd('\', '/') } else { $p }
    })
    if ($targets.Count -eq 0) { Fail "대상 목록이 비어 있습니다: $TargetsFile" }
    # -Source 상대 경로는 이 셸의 현재 위치 기준 (5.1의 Set-Location은 프로세스 현재 폴더를 바꾸지 않아 자식이 다르게 풂)
    $childSource = if ([string]::IsNullOrWhiteSpace($Source)) { '' }
                   elseif ([System.IO.Path]::IsPathRooted($Source)) { $Source }
                   else { [System.IO.Path]::GetFullPath((Join-Path $PWD.ProviderPath $Source)) }
    $worst = 0
    $summary = @()
    $prevEncoding = [Console]::OutputEncoding
    $env:SYNC_PROJECTFILES_MACHINE = '1'   # 자식이 기계용 줄(##SYNC)을 UTF-8로 낸다
    try {
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        foreach ($t in $targets) {
            $childArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-UnityProject', $t)
            if ($childSource) { $childArgs += @('-Source', $childSource) }
            # 자식이 stderr에 써도(5.1 NativeCommandError) -All 전체가 멈추지 않게 이 호출만 Continue + 대상마다 잡는다
            $prevPref = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                $out = @(& powershell @childArgs 2>&1 | ForEach-Object { "$_" })
                $code = $LASTEXITCODE
            }
            catch { $out = @("[오류] 자식 실행 실패: $_"); $code = 2 }
            finally { $ErrorActionPreference = $prevPref }
            if ($code -notin @(0, 1, 2)) { $code = 2 }   # 구문 오류 등 예상 밖 종료 코드는 오류로
            if ($code -gt $worst) { $worst = $code }
            $machine = @($out | Where-Object { $_.StartsWith('##SYNC|') } | ForEach-Object { $f = $_.Split('|'); [pscustomobject]@{ Kind = $f[1]; Code = $f[2]; Path = $f[3] } })
            $result = ($out | Where-Object { $_ -match '^(결과:|\[오류\])' } | Select-Object -Last 1)
            $summary += [pscustomobject]@{
                Target = $t; Code = $code; Result = $result
                Behind = @($machine | Where-Object { $_.Kind -eq 'pending' })
                Notes  = @($machine | Where-Object { $_.Kind -eq 'note' })
            }
        }
    }
    finally {
        [Console]::OutputEncoding = $prevEncoding
        Remove-Item Env:\SYNC_PROJECTFILES_MACHINE -ErrorAction SilentlyContinue
    }
    Write-Host ''
    Write-Host '=== 모든 동기화 대상 비교 (비교만) ==='
    foreach ($s in $summary) {
        $mark = switch ($s.Code) { 0 { '같음  ' } 1 { '뒤처짐' } default { '오류  ' } }
        $notes = if ($s.Notes.Count -gt 0) { " · 참고 $($s.Notes.Count)건" } else { '' }
        Write-Host ("[{0}] {1} — {2}{3}" -f $mark, $s.Target, $s.Result, $notes)
        foreach ($b in $s.Behind) { Write-Host "         맞출 것: $($b.Path) ($($b.Code))" }
        foreach ($n in $s.Notes) { Write-Host "         참고:    $($n.Path) ($($n.Code))" }
    }
    $late = @($summary | Where-Object { $_.Code -ne 0 })
    $noted = @($summary | Where-Object { $_.Code -eq 0 -and $_.Notes.Count -gt 0 })
    Write-Host ''
    if ($late.Count -eq 0) { Write-Host "결과: 대상 $($summary.Count)개 모두 동기화 대상이 원본과 같습니다." }
    else { Write-Host "결과: 대상 $($summary.Count)개 중 $($late.Count)개가 뒤처졌거나 오류입니다. 사용자에게 보고하고, 맞추기(-Apply/-Force)는 확인 뒤 대상마다 따로." }
    if ($noted.Count -gt 0 -or @($summary | Where-Object { $_.Notes.Count -gt 0 }).Count -gt 0) {
        Write-Host '참고 항목(설정 · 프로젝트 지침 · 대상에만 있는 파일)은 종료 코드에 넣지 않지만 함께 보고한다 — 특히 settings.json 다름은 훅 등록이 다를 수 있다.'
    }
    exit $worst
}
if ([string]::IsNullOrWhiteSpace($UnityProject)) { Fail '-UnityProject <프로젝트 폴더> 또는 -All을 주세요.' }

if ([string]::IsNullOrWhiteSpace($Source)) { $Source = Join-Path $PSScriptRoot '..\project-files' }
if (-not (Test-Path -LiteralPath $Source -PathType Container)) { Fail "원본 폴더가 없습니다: $Source" }
if (-not (Test-Path -LiteralPath $UnityProject -PathType Container)) { Fail "Unity 프로젝트 폴더가 없습니다: $UnityProject" }
$Source = (Resolve-Path -LiteralPath $Source).ProviderPath
$UnityProject = (Resolve-Path -LiteralPath $UnityProject).ProviderPath
if ($Source -eq $UnityProject) { Fail '원본과 Unity 프로젝트가 같은 폴더입니다.' }

# --- 대상 목록 ---------------------------------------------------------------

$syncFiles = @('CLAUDE.md')
# 없으면 만듦: 프로젝트마다 고쳐 쓰는 파일. 없을 때만 복사하고, 있으면 달라도 멈추지 않고 덮지 않는다.
$seedFiles = @('REVIEW_GUIDELINES.md')
foreach ($f in ($syncFiles + $seedFiles)) {
    if (-not (Test-Path -LiteralPath (Join-Path $Source $f) -PathType Leaf)) { Fail "원본에 $f 가 없습니다: $Source" }
}
# 폴더 단위 대상: 원본 폴더에 있는 파일 전부. Unity 쪽에만 있는 파일은 '추가분'으로 보여 주기만 한다.
$syncDirs = @(
    @{ Dir = '.claude/commands'; Filter = '*.md' },   # 슬래시 명령
    @{ Dir = '.claude/scripts';  Filter = '*.py' },   # 명령이 부르는 도우미 (예: /work-order의 xlsx_dump.py, 서브에이전트 읽기 차단 훅)
    @{ Dir = '.claude/agents';   Filter = '*.md' },   # 서브에이전트 정의 (self-reviewer, evidence-collector)
    @{ Dir = '.claude/mistakes'; Filter = '*.md' }    # 서브에이전트 실수 목록 (계획서 2026-10-27_실수목록-분리)
)

# 서브에이전트 정의 검사: 원본에 정의가 있으면 비교·복사 전에 검사한다. 실패하면 Unity 쪽은 건드리지 않는다.
$agentsSrc = Join-Path $Source '.claude\agents'
if (Test-Path -LiteralPath $agentsSrc -PathType Container) {
    $checker = Join-Path $Source '.claude\scripts\check_agents.py'
    if (-not (Test-Path -LiteralPath $checker -PathType Leaf)) { Fail "서브에이전트 정의가 있는데 검사 스크립트가 없습니다: $checker" }
    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($null -eq $python) { Fail 'python을 찾을 수 없어 서브에이전트 정의를 검사하지 못했습니다. 아무것도 비교·복사하지 않았습니다.' }
    $out = & $python.Source -I $checker $agentsSrc 2>&1
    $out | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { Fail '서브에이전트 정의 검사 실패 (위 [실패] 줄). 아무것도 비교·복사하지 않았습니다.' }
    Write-Host ''
}
$dirNames = @{}
foreach ($d in $syncDirs) {
    $src = Join-Path $Source ($d.Dir -replace '/', '\')
    $names = @()
    if (Test-Path -LiteralPath $src -PathType Container) {
        $names = @(Get-ChildItem -LiteralPath $src -Filter $d.Filter -File -Force | Sort-Object Name | ForEach-Object { $_.Name })
    }
    $dirNames[$d.Dir] = $names
    $syncFiles += $names | ForEach-Object { "$($d.Dir)/$_" }
}
# 확인만 대상: 원본의 템플릿 이름 → Unity의 실제 이름. 복사하지 않는다.
#   Hash  true  = 템플릿과 해시 비교 (settings.json)
#         false = 기기마다 내용이 달라 비교하지 않고 없음 · 자리표시만 본다 (.mcp.json)
$checkOnlyFiles = @(
    @{ Template = '.claude/settings.example.json'; Target = '.claude/settings.json'; Hash = $true },
    @{ Template = '.mcp.example.json';             Target = '.mcp.json';             Hash = $false }
)
# 템플릿에 들어 있는 자리표시. Unity 쪽 파일에 남아 있으면 경로를 고치지 않은 것이다.
$Placeholder = '<publish 폴더>'

function Get-CheckOnlyState($c, [string]$targetHash) {
    # [string] 매개변수는 $null을 ''로 받는다
    if ([string]::IsNullOrEmpty($targetHash)) { return '확인만-Unity에 없음' }
    if ($c.Hash) {
        $s = Get-Hash (Join-Path $Source $c.Template)
        if ($s -eq $targetHash) { return '같음' } else { return '확인만-다름' }
    }
    $text = [System.IO.File]::ReadAllText((Join-Path $UnityProject $c.Target), [System.Text.UTF8Encoding]::new($false))
    if ($text.Contains($Placeholder)) { return '확인만-자리표시 남음' }
    return '확인만-있음'
}

function Compare-All {
    $rows = @()
    foreach ($rel in $syncFiles) {
        $s = Get-Hash (Join-Path $Source $rel)
        $u = Get-Hash (Join-Path $UnityProject $rel)
        $state = if (Test-Path -LiteralPath (Join-Path $UnityProject $rel) -PathType Container) { 'Unity에 폴더' }
                 elseif ($null -eq $u) { 'Unity에 없음' } elseif ($s -eq $u) { '같음' } else { '다름' }
        $rows += [pscustomobject]@{ Kind = 'sync'; Path = $rel; State = $state; Src = $s; Unity = $u }
    }
    foreach ($rel in $seedFiles) {
        $s = Get-Hash (Join-Path $Source $rel)
        $u = Get-Hash (Join-Path $UnityProject $rel)
        $state = if (Test-Path -LiteralPath (Join-Path $UnityProject $rel) -PathType Container) { 'Unity에 폴더' }
                 elseif ($null -eq $u) { 'Unity에 없음' } elseif ($s -eq $u) { '같음' }
                 elseif (Test-OldDefault $rel (Join-Path $UnityProject $rel)) { '기본 지침 옛 판' } else { '프로젝트 지침' }
        $rows += [pscustomobject]@{ Kind = 'seed'; Path = $rel; State = $state; Src = $s; Unity = $u }
    }
    foreach ($c in $checkOnlyFiles) {
        $s = Get-Hash (Join-Path $Source $c.Template)
        if ($null -eq $s) { continue }
        $u = Get-Hash (Join-Path $UnityProject $c.Target)
        $state = Get-CheckOnlyState $c $u
        $rows += [pscustomobject]@{ Kind = 'check'; Path = $c.Target; State = $state; Src = $(if ($c.Hash) { $s } else { $null }); Unity = $u; Template = $c.Template }
        # 폴더째 붙여넣은 흔적. 지워도 된다.
        $copy = Join-Path $UnityProject $c.Template
        if (Test-Path -LiteralPath $copy -PathType Leaf) {
            $rows += [pscustomobject]@{ Kind = 'extra'; Path = $c.Template; State = 'Unity에 템플릿 사본'; Src = $s; Unity = (Get-Hash $copy) }
        }
    }
    foreach ($d in $syncDirs) {
        $unityDir = Join-Path $UnityProject ($d.Dir -replace '/', '\')
        if (-not (Test-Path -LiteralPath $unityDir -PathType Container)) { continue }
        foreach ($f in (Get-ChildItem -LiteralPath $unityDir -Filter $d.Filter -File -Force | Sort-Object Name)) {
            if ($dirNames[$d.Dir] -notcontains $f.Name) {
                $rows += [pscustomobject]@{ Kind = 'extra'; Path = "$($d.Dir)/$($f.Name)"; State = 'Unity에만 있음'; Src = $null; Unity = (Get-Hash $f.FullName) }
            }
        }
    }
    return $rows
}

function Write-Rows($rows) {
    $width = ($rows | ForEach-Object { $_.State.Length } | Measure-Object -Maximum).Maximum
    $pathWidth = ($rows | ForEach-Object { $_.Path.Length } | Measure-Object -Maximum).Maximum
    foreach ($r in $rows) {
        Write-Host ('{0}  {1}  {2}  {3}' -f $r.State.PadRight($width), $r.Path.PadRight($pathWidth), (Short $r.Src), (Short $r.Unity))
    }
}

# 맞춰야 할 행: 동기화 대상 중 같지 않은 것 + 없으면 만듦 대상 중 없거나 원본 기본 지침의 옛 판인 것 (프로젝트 지침은 제외)
function Get-Pending($rows) {
    return @($rows | Where-Object { ($_.Kind -eq 'sync' -and $_.State -ne '같음') -or ($_.Kind -eq 'seed' -and $_.State -in @('Unity에 없음', 'Unity에 폴더', '기본 지침 옛 판')) })
}

# 없으면 만듦 대상이 원본 git 이력의 어느 판과 같은지 (= 프로젝트에서 고치지 않은 옛 기본 지침).
# 원본 기본 지침을 바꿔도 손대지 않은 프로젝트가 '프로젝트 지침'으로 조용히 남지 않게 (self-review 2026-10-22 C1).
# git이 없거나 원본이 저장소가 아니면 판단하지 않는다 ('프로젝트 지침'으로 둠).
$script:SeedBlobs = @{}
function Test-OldDefault([string]$rel, [string]$targetPath) {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { return $false }
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if (-not $script:SeedBlobs.ContainsKey($rel)) {
            $commits = @(& git -C $Source log --format=%H -- $rel 2>$null)
            $script:SeedBlobs[$rel] = if ($LASTEXITCODE -ne 0) { @() } else { @($commits | ForEach-Object { & git -C $Source rev-parse "$($_):./$rel" 2>$null } | Where-Object { $_ }) }
        }
        if ($script:SeedBlobs[$rel].Count -eq 0) { return $false }
        $blob = & git hash-object --no-filters -- $targetPath 2>$null
        return ($LASTEXITCODE -eq 0 -and $script:SeedBlobs[$rel] -contains $blob)
    }
    finally { $ErrorActionPreference = $prev }
}

function Test-AllSynced($rows) {
    # 함수 반환은 원소 하나짜리 배열을 풀어 버리므로 받는 쪽에서 @()로 다시 감싼다 (PowerShell 5.1에서 그 객체의 .Count는 비어 있음)
    return @(Get-Pending $rows).Count -eq 0
}

# --- 비교 --------------------------------------------------------------------

Write-Host "원본:  $Source"
Write-Host "Unity: $UnityProject"
Write-Host ''
$rows = Compare-All
Write-Rows $rows

# -All이 부른 자식: 사람용 표와 별도로 기계용 줄 (ASCII 표지, 한국어 상태 이름에 기대지 않게). pending = 맞출 것, note = 참고
if ($env:SYNC_PROJECTFILES_MACHINE -eq '1') {
    $pendingRows = @(Get-Pending $rows)
    foreach ($r in $rows) {
        $code = switch ($r.State) {
            '다름' { 'differ' } 'Unity에 없음' { 'missing' } 'Unity에 폴더' { 'folder' }
            '기본 지침 옛 판' { 'old-default' } '프로젝트 지침' { 'project-guide' }
            '확인만-Unity에 없음' { 'check-missing' } '확인만-다름' { 'check-differ' } '확인만-자리표시 남음' { 'check-placeholder' }
            'Unity에만 있음' { 'only-in-target' } 'Unity에 템플릿 사본' { 'template-copy' }
            default { $null }   # 같음 · 확인만-있음
        }
        if ($null -eq $code) { continue }
        $kind = if ($pendingRows -contains $r) { 'pending' } else { 'note' }
        Write-Host "##SYNC|$kind|$code|$($r.Path)"
    }
}

$warnings = @()
foreach ($rel in (($syncFiles + $seedFiles) | Where-Object { $_ -like '*.md' })) {
    $warnings += @(Find-EscapeRemnants (Join-Path $Source $rel) "원본/$rel")
    $warnings += @(Find-EscapeRemnants (Join-Path $UnityProject $rel) "Unity/$rel")
}
foreach ($r in ($rows | Where-Object { $_.Kind -eq 'extra' -and $_.Path -like '*.md' })) {
    $warnings += @(Find-EscapeRemnants (Join-Path $UnityProject $r.Path) "Unity/$($r.Path)")
}
if ($warnings.Count -gt 0) {
    Write-Host ''
    $warnings | ForEach-Object { Write-Host $_ }
}

# 확인만 행의 할 일. 스크립트는 이 파일들을 만들거나 고치지 않는다.
$todo = @()
foreach ($r in ($rows | Where-Object { $_.Kind -eq 'check' })) {
    switch ($r.State) {
        '확인만-Unity에 없음'   { $todo += "[할 일] $($r.Path) 없음: 원본의 $($r.Template)을(를) Unity에 $($r.Path)(으)로 복사한 뒤 고치세요." }
        '확인만-자리표시 남음' { $todo += "[할 일] $($r.Path)에 '$Placeholder'가 남아 있습니다: dll 경로를 publish 폴더로 고치세요 (JSON 안에서는 '/')." }
        '확인만-다름'           { $todo += "[참고] $($r.Path)이(가) $($r.Template)과(와) 다릅니다. 새 allow 항목이 있으면 손으로 합치세요." }
    }
}
foreach ($r in ($rows | Where-Object { $_.Kind -eq 'seed' -and $_.State -eq '기본 지침 옛 판' })) {
    $todo += "[할 일] $($r.Path)이(가) 원본 기본 지침의 옛 판입니다 (프로젝트에서 고치지 않음). -Apply로 새 판으로 맞춥니다 (git 이력에 있으므로 백업 없이)."
}
foreach ($r in ($rows | Where-Object { $_.Kind -eq 'seed' -and $_.State -eq '프로젝트 지침' })) {
    $todo += "[참고] $($r.Path)은(는) 이 프로젝트의 지침입니다 (원본 기본 지침과 다름 — 덮지 않음). 원본의 새 항목이 필요하면 손으로 합치세요."
}
if (@($rows | Where-Object { $_.State -eq 'Unity에 템플릿 사본' }).Count -gt 0) {
    $todo += '[참고] Unity에 템플릿 사본이 있습니다 (project-files를 폴더째 붙여넣은 흔적). 지워도 됩니다.'
}
if ($todo.Count -gt 0) {
    Write-Host ''
    $todo | ForEach-Object { Write-Host $_ }
}

$pending = @(Get-Pending $rows)
$differ = @($pending | Where-Object { $_.State -eq '다름' })

if (-not $Apply) {
    Write-Host ''
    if ($pending.Count -eq 0) { Write-Host '결과: 동기화 대상이 모두 같습니다.'; exit 0 }
    Write-Host "결과: 동기화 대상 $($pending.Count)개가 원본과 다릅니다. -Apply로 맞출 수 있습니다."
    exit 1
}

# --- 적용 --------------------------------------------------------------------

Write-Host ''
if ($pending.Count -eq 0) { Write-Host '결과: 동기화 대상이 모두 같습니다. 복사할 것이 없습니다.'; exit 0 }

# 파일 자리에 폴더가 있으면 Copy-Item이 그 폴더 안에 복사한다. 아무것도 하지 않고 끝낸다.
$folders = @($pending | Where-Object { $_.State -eq 'Unity에 폴더' })
if ($folders.Count -gt 0) {
    Fail ("파일이어야 할 위치에 폴더가 있습니다. 아무것도 복사하지 않았습니다: " + (($folders | ForEach-Object { $_.Path }) -join ', '))
}

if ($differ.Count -gt 0 -and -not $Force) {
    Write-Host "멈춤: Unity 쪽 파일 $($differ.Count)개가 원본과 다릅니다. 아무것도 복사하지 않았습니다."
    $differ | ForEach-Object { Write-Host "  $($_.Path)" }
    Write-Host 'Unity에서 고친 내용이 있으면 원본(project-files)에 먼저 합치세요. 버려도 되면 -Force로 덮습니다 (덮기 전에 백업).'
    exit 1
}

if ($differ.Count -gt 0) {
    $backup = Join-Path $UnityProject ('review_tmp\sync-backup\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    foreach ($r in $differ) {
        $to = Join-Path $backup $r.Path
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $to) | Out-Null
        Copy-Item -LiteralPath (Join-Path $UnityProject $r.Path) -Destination $to
    }
    Write-Host "백업: $backup ($($differ.Count)개)"
}

foreach ($r in $pending) {
    $to = Join-Path $UnityProject $r.Path
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $to) | Out-Null
    Copy-Item -LiteralPath (Join-Path $Source $r.Path) -Destination $to -Force
    Write-Host "복사: $($r.Path)"
}

Write-Host ''
$after = Compare-All
Write-Rows $after
Write-Host ''
if (Test-AllSynced $after) { Write-Host '결과: 복사 후 동기화 대상의 SHA-256이 모두 원본과 같습니다.'; exit 0 }
Write-Host '결과: 복사 후에도 원본과 다른 파일이 있습니다.'
exit 1
