# Sync-ProjectFiles.ps1 시험 (외부 호출 없음, 임시 폴더만). 사용: powershell -ExecutionPolicy Bypass -File tools\Test-SyncProjectFiles.ps1
# 2026-10-22: REVIEW_GUIDELINES.md "없으면 만듦" · -All 비교
$ErrorActionPreference = 'Stop'
$sync = Join-Path $PSScriptRoot 'Sync-ProjectFiles.ps1'
$orig = (Resolve-Path (Join-Path $PSScriptRoot '..\project-files')).ProviderPath
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('sync-test-' + [guid]::NewGuid().ToString('N'))
$src = Join-Path $root 'src'
$fail = 0

function Run([string[]]$a) {
    $out = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $sync @a 2>&1 | ForEach-Object { "$_" })
    return [pscustomobject]@{ Code = $LASTEXITCODE; Out = ($out -join "`n") }
}
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { Write-Host "PASS $name" } else { Write-Host "FAIL $name $detail"; $script:fail++ }
}
function Read([string]$p) { return [System.IO.File]::ReadAllText($p) }

try {
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    Copy-Item -Recurse -LiteralPath $orig -Destination $src
    $guide = Read (Join-Path $src 'REVIEW_GUIDELINES.md')

    # 1. 빈 대상: 비교 1 → -Apply로 지침까지 만듦 → 비교 0
    $t1 = Join-Path $root 't1'; New-Item -ItemType Directory -Path $t1 | Out-Null
    $r = Run @('-UnityProject', $t1, '-Source', $src)
    Check '빈 대상 비교는 1' ($r.Code -eq 1) $r.Out
    $r = Run @('-UnityProject', $t1, '-Source', $src, '-Apply')
    Check '빈 대상 -Apply는 0' ($r.Code -eq 0) $r.Out
    Check '지침이 없으면 만듦' ((Read (Join-Path $t1 'REVIEW_GUIDELINES.md')) -eq $guide)
    $r = Run @('-UnityProject', $t1, '-Source', $src)
    Check '맞춘 뒤 비교는 0' ($r.Code -eq 0) $r.Out
    # 2026-10-27: 서브에이전트 실수 목록은 퍼지고, 외부 에이전트 덧붙임(prompts)은 서버 전용이라 퍼지지 않는다
    Check '서브에이전트 실수 목록 복사' ((Read (Join-Path $t1 '.claude\mistakes\self-reviewer.md')) -eq (Read (Join-Path $src '.claude\mistakes\self-reviewer.md')))
    Check 'prompts는 복사 안 함' (-not (Test-Path -LiteralPath (Join-Path $t1 'prompts')))

    # 2. 지침만 프로젝트에 맞춰 고침: 비교 0 · 프로젝트 지침 표시 · -Apply가 덮지 않음
    $custom = "# 프로젝트 전용 지침`n- 시험`n"
    [System.IO.File]::WriteAllText((Join-Path $t1 'REVIEW_GUIDELINES.md'), $custom)
    $r = Run @('-UnityProject', $t1, '-Source', $src)
    Check '고친 지침만 다르면 비교 0' ($r.Code -eq 0) $r.Out
    Check '프로젝트 지침으로 표시' ($r.Out -match '프로젝트 지침') $r.Out
    $r = Run @('-UnityProject', $t1, '-Source', $src, '-Apply')
    Check '고친 지침 -Apply는 0' ($r.Code -eq 0) $r.Out
    Check '고친 지침을 덮지 않음' ((Read (Join-Path $t1 'REVIEW_GUIDELINES.md')) -eq $custom)

    # 3. 동기화 대상(CLAUDE.md)이 옛 판 + 고친 지침: -Apply 멈춤(1) · -Force는 CLAUDE.md만 덮고 백업, 지침은 그대로
    $t2 = Join-Path $root 't2'; Copy-Item -Recurse -LiteralPath $t1 -Destination $t2
    [System.IO.File]::WriteAllText((Join-Path $t2 'CLAUDE.md'), "# 옛 판`n")
    $r = Run @('-UnityProject', $t2, '-Source', $src, '-Apply')
    Check '옛 CLAUDE.md면 -Apply 멈춤' ($r.Code -eq 1 -and $r.Out -match '멈춤') $r.Out
    Check '멈춤 때 아무것도 안 바뀜' ((Read (Join-Path $t2 'CLAUDE.md')) -eq "# 옛 판`n")
    $r = Run @('-UnityProject', $t2, '-Source', $src, '-Apply', '-Force')
    Check '-Force는 0' ($r.Code -eq 0) $r.Out
    Check '-Force가 CLAUDE.md를 맞춤' ((Read (Join-Path $t2 'CLAUDE.md')) -eq (Read (Join-Path $src 'CLAUDE.md')))
    Check '-Force도 고친 지침은 그대로' ((Read (Join-Path $t2 'REVIEW_GUIDELINES.md')) -eq $custom)
    Check '-Force 백업에 옛 CLAUDE.md' (@(Get-ChildItem -Recurse -LiteralPath (Join-Path $t2 'review_tmp\sync-backup') -Filter 'CLAUDE.md').Count -eq 1)

    # 4. -All: 같은 대상 · 뒤처진 대상 · 없는 폴더 → 종료 2(가장 나쁜 값), 요약에 각각
    $t3 = Join-Path $root 't3'; Copy-Item -Recurse -LiteralPath $t1 -Destination $t3
    [System.IO.File]::WriteAllText((Join-Path $t3 '.claude\commands\self-review.md'), "옛 판`n")
    $list = Join-Path $root 'targets.txt'
    [System.IO.File]::WriteAllLines($list, @('# 시험 대상', $t1, $t3, (Join-Path $root 'no-such')))
    $r = Run @('-All', '-Source', $src, '-TargetsFile', $list)
    Check '-All 종료 코드는 가장 나쁜 값(2)' ($r.Code -eq 2) $r.Out
    Check '-All 같은 대상 표시' ($r.Out -match "\[같음\s*\] $([regex]::Escape($t1))") $r.Out
    Check '-All 뒤처진 대상과 파일 표시' ($r.Out -match "\[뒤처짐\] $([regex]::Escape($t3))" -and $r.Out -match 'self-review\.md') $r.Out
    Check '-All 오류 대상 표시' ($r.Out -match '\[오류\s*\]') $r.Out
    [System.IO.File]::WriteAllLines($list, @($t1, $t3))
    $r = Run @('-All', '-Source', $src, '-TargetsFile', $list)
    Check '-All 뒤처짐만 있으면 1' ($r.Code -eq 1) $r.Out

    # 4-2. -All 요약에 참고 항목(프로젝트 지침 · settings.json 다름)이 보이고, 대상 줄의 상대 경로 · 끝 구분자를 처리
    [System.IO.File]::WriteAllText((Join-Path $t1 '.claude\settings.json'), '{ "hooks": {} }')
    [System.IO.File]::WriteAllLines($list, @('t1\'))   # 목록 파일 폴더 기준 상대 경로 + 끝 \
    $r = Run @('-All', '-Source', $src, '-TargetsFile', $list)
    Check '-All 참고만 있으면 0' ($r.Code -eq 0) $r.Out
    Check '-All 참고: 프로젝트 지침' ($r.Out -match '참고:\s+REVIEW_GUIDELINES\.md \(project-guide\)') $r.Out
    Check '-All 참고: settings.json 다름' ($r.Out -match '참고:\s+\.claude/settings\.json \(check-differ\)') $r.Out
    Check '-All 상대 경로 대상 찾음' ($r.Out -match "\[같음\s*\] $([regex]::Escape($t1)) ") $r.Out

    # 4-3. 원본 기본 지침만 바뀜: 손대지 않은 프로젝트 사본(옛 판)은 '기본 지침 옛 판' → -Apply가 갱신 · 고친 사본은 그대로
    $gsrc = Join-Path $root 'gsrc'; Copy-Item -Recurse -LiteralPath $orig -Destination $gsrc
    # git 경고(stderr)가 Stop 설정에서 종료 오류가 되지 않게 · 임시 저장소는 줄 끝 변환 없이 (blob = 파일 바이트)
    $git = { param($d) $ErrorActionPreference = 'Continue'; & git -C $gsrc -c core.autocrlf=false @d 2>&1 | Out-Null }
    & $git @('init', '-q'); & $git @('-c', 'user.email=t@example.com', '-c', 'user.name=t', 'add', '.')
    & $git @('-c', 'user.email=t@example.com', '-c', 'user.name=t', 'commit', '-q', '-m', 'v1')
    $t4 = Join-Path $root 't4'; New-Item -ItemType Directory -Path $t4 | Out-Null
    $r = Run @('-UnityProject', $t4, '-Source', $gsrc, '-Apply')
    Check '옛 판 시험 준비(-Apply 0)' ($r.Code -eq 0) $r.Out
    [System.IO.File]::WriteAllText((Join-Path $gsrc 'REVIEW_GUIDELINES.md'), "# 기본 지침 v2`n")
    & $git @('-c', 'user.email=t@example.com', '-c', 'user.name=t', 'commit', '-q', '-am', 'v2')
    $r = Run @('-UnityProject', $t4, '-Source', $gsrc)
    Check '손대지 않은 옛 판은 뒤처짐(1)' ($r.Code -eq 1 -and $r.Out -match '기본 지침 옛 판') $r.Out
    $r = Run @('-UnityProject', $t4, '-Source', $gsrc, '-Apply')
    Check '옛 판은 -Apply로 갱신(-Force 없이)' ($r.Code -eq 0 -and (Read (Join-Path $t4 'REVIEW_GUIDELINES.md')) -eq "# 기본 지침 v2`n") $r.Out
    [System.IO.File]::WriteAllText((Join-Path $t4 'REVIEW_GUIDELINES.md'), $custom)
    $r = Run @('-UnityProject', $t4, '-Source', $gsrc)
    Check '고친 사본은 여전히 프로젝트 지침(0)' ($r.Code -eq 0 -and $r.Out -match '프로젝트 지침') $r.Out

    # 4-1. 다른 파일이 하나뿐일 때 결과 줄에 개수가 나옴 (PowerShell 5.1 원소 하나 배열 풀림)
    $r = Run @('-UnityProject', $t3, '-Source', $src)
    Check '하나만 다를 때 개수 1' ($r.Out -match '동기화 대상 1개가') $r.Out

    # 5. 잘못된 조합
    $r = Run @('-All', '-Apply', '-Source', $src, '-TargetsFile', $list)
    Check '-All -Apply 거절(2)' ($r.Code -eq 2) $r.Out
    $r = Run @('-Source', $src)
    Check '대상 없음 거절(2)' ($r.Code -eq 2) $r.Out
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $root -ErrorAction SilentlyContinue
}
if ($fail -eq 0) { Write-Host 'ALL PASS'; exit 0 } else { Write-Host "실패 $fail"; exit 1 }
