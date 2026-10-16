<#
.SYNOPSIS
    project-files(원본)를 Unity 프로젝트에 맞춘다. 기본은 비교만 한다.

.DESCRIPTION
    원본 방향: project-files가 원본이고 Unity 프로젝트 쪽은 복사본이다. 규칙을 고칠 때는 원본을 고친 뒤 이 스크립트로 복사한다.

    파일 구분
      동기화   CLAUDE.md, REVIEW_GUIDELINES.md, .claude/commands/*.md, .claude/scripts/*.py (원본 폴더에 있는 것)  바이트 동일해야 한다
      확인만   .claude/settings.example.json → .claude/settings.json   덮지 않는다. 해시만 비교 (기존 파일이 있으면 allow 항목만 손으로 합친다)
      확인만   .mcp.example.json → .mcp.json                         덮지 않는다. 기기마다 경로가 달라 해시는 비교하지 않고 없음 · 자리표시 남음만 알린다

    템플릿은 실제 설정과 이름이 다르다. project-files를 폴더째 붙여넣어도 Unity 쪽 설정이 덮이지 않게 하기 위해서다.
    Unity 쪽에 템플릿 이름 파일이 있으면 'Unity에 템플릿 사본'으로 보여 주기만 한다. 확인만 행은 종료 코드에 영향이 없다.

    -Apply는 Unity 쪽 파일이 원본과 '다름'이면 아무것도 복사하지 않고 멈춘다 (Unity에서 직접 고친 내용을 지우지 않도록).
    그 내용을 원본에 먼저 합치거나, 버려도 되면 -Force로 덮는다. -Force는 덮기 전에 <Unity>/review_tmp/sync-backup/<시각>/에 백업한다.

    이스케이프 잔재(줄 맨 앞 \---, \_, \[, 1\. 등)가 보이면 경고만 한다 (.md 파일만). 종료 코드에는 영향이 없다.

    종료 코드: 0 동기화 대상이 모두 같음 / 1 다른 것이 있음(또는 -Apply가 멈춤) / 2 실행 오류

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\Sync-ProjectFiles.ps1 -UnityProject F:\MyGame
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\Sync-ProjectFiles.ps1 -UnityProject F:\MyGame -Apply
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$UnityProject,
    [switch]$Apply,
    [switch]$Force,
    # 테스트용. 비우면 이 스크립트 옆의 ..\project-files
    [string]$Source = ''
)

$ErrorActionPreference = 'Stop'
# 예상하지 못한 예외도 '다름'(1)과 구분되게 2로 끝낸다
trap {
    Write-Host "[오류] $_"
    exit 2
}

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
if ([string]::IsNullOrWhiteSpace($Source)) { $Source = Join-Path $PSScriptRoot '..\project-files' }
if (-not (Test-Path -LiteralPath $Source -PathType Container)) { Fail "원본 폴더가 없습니다: $Source" }
if (-not (Test-Path -LiteralPath $UnityProject -PathType Container)) { Fail "Unity 프로젝트 폴더가 없습니다: $UnityProject" }
$Source = (Resolve-Path -LiteralPath $Source).ProviderPath
$UnityProject = (Resolve-Path -LiteralPath $UnityProject).ProviderPath
if ($Source -eq $UnityProject) { Fail '원본과 Unity 프로젝트가 같은 폴더입니다.' }

# --- 대상 목록 ---------------------------------------------------------------

$syncFiles = @('CLAUDE.md', 'REVIEW_GUIDELINES.md')
foreach ($f in $syncFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $Source $f) -PathType Leaf)) { Fail "원본에 $f 가 없습니다: $Source" }
}
# 폴더 단위 대상: 원본 폴더에 있는 파일 전부. Unity 쪽에만 있는 파일은 '추가분'으로 보여 주기만 한다.
$syncDirs = @(
    @{ Dir = '.claude/commands'; Filter = '*.md' },   # 슬래시 명령
    @{ Dir = '.claude/scripts';  Filter = '*.py' }    # 명령이 부르는 도우미 (예: /work-order의 xlsx_dump.py)
)
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

function Test-AllSynced($rows) {
    return -not ($rows | Where-Object { $_.Kind -eq 'sync' -and $_.State -ne '같음' })
}

# --- 비교 --------------------------------------------------------------------

Write-Host "원본:  $Source"
Write-Host "Unity: $UnityProject"
Write-Host ''
$rows = Compare-All
Write-Rows $rows

$warnings = @()
foreach ($rel in ($syncFiles | Where-Object { $_ -like '*.md' })) {
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
if (@($rows | Where-Object { $_.State -eq 'Unity에 템플릿 사본' }).Count -gt 0) {
    $todo += '[참고] Unity에 템플릿 사본이 있습니다 (project-files를 폴더째 붙여넣은 흔적). 지워도 됩니다.'
}
if ($todo.Count -gt 0) {
    Write-Host ''
    $todo | ForEach-Object { Write-Host $_ }
}

$pending = @($rows | Where-Object { $_.Kind -eq 'sync' -and $_.State -ne '같음' })
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
