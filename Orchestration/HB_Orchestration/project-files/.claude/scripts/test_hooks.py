"""cleanup_guard · push_guard · run_mode_guard · share_guard · subagent_input_guard · habit_guard · commit_notice · plan_ref_guard 회귀 시험 (/cross-review 10bfc8de41 반영).
사용: python -I .claude/scripts/test_hooks.py  → 마지막 줄 'ALL True'면 통과 (exit 0), 아니면 exit 1.
H2는 임시 폴더에 저장소와 bare 원격을 만들어 시험한다 (실제 원격·실제 저장소는 건드리지 않는다).
입력은 UTF-8 바이트로 보낸다 (Claude Code가 보내는 것과 같게 — 한글 깨짐 회귀 시험).
"""
import datetime
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unicodedata

sys.stdout.reconfigure(encoding='utf-8')
HERE = os.path.dirname(os.path.abspath(__file__))
ok = True


def run(script: str, payload: dict, env: dict | None = None) -> tuple[int, str]:
    r = subprocess.run([sys.executable, '-I', os.path.join(HERE, script)], input=json.dumps(payload, ensure_ascii=False).encode('utf-8'),
                       capture_output=True, env=env or dict(os.environ))
    return r.returncode, r.stderr.decode('utf-8', 'replace').strip()


def expect(name: str, got: tuple[int, str], want: int, contains: str | None = None) -> None:
    global ok
    good = got[0] == want and (contains is None or contains in got[1])
    ok &= good
    print(('PASS' if good else 'FAIL'), name, got[0], got[1][:140])


# ── H1 cleanup_guard ──────────────────────────────────────────────────
OUT = 'C:\\Users\\x' if os.name == 'nt' else '/home/x'  # 셸 현재 폴더 (대상 밖)


def h1(cmd: str, tool: str = 'Bash', cwd: str = OUT) -> tuple[int, str]:
    return run('cleanup_guard.py', {'tool_name': tool, 'tool_input': {'command': cmd}, 'cwd': cwd})

expect('H1 절대 경로 단독 rm -rf', h1('rm -rf /f/AI/ToolWithCS/subagent_in/x'), 0)
expect('H1 Windows 절대 경로', h1('rm -rf "F:\\AI\\ToolWithCS\\subagent_in\\x"'), 0)
expect('H1 PowerShell 절대 경로', h1('Remove-Item -LiteralPath "F:\\AI\\x" -Recurse -Force -Confirm:$false', 'PowerShell'), 0)
expect('H1 PowerShell -ErrorAction 값은 경로 아님', h1('Remove-Item -LiteralPath "F:\\AI\\x" -Recurse -Force -ErrorAction SilentlyContinue', 'PowerShell'), 0)
expect('H1 PowerShell 쉼표 여러 경로', h1('Remove-Item -LiteralPath "F:\\AI\\a","F:\\AI\\b" -Recurse -Force', 'PowerShell'), 0)
expect('H1 PowerShell 쉼표 경로 중 상대', h1('Remove-Item -Recurse "F:\\AI\\a",b', 'PowerShell'), 2, "'b'")
expect('H1 && 로 묶음', h1('dotnet test && rm -rf /f/AI/x'), 2, '묶여')
expect('H1 cd 뒤', h1('cd /f/AI && rm -rf x'), 2)
expect('H1 상대 경로', h1('rm -rf subagent_in/x'), 2, '절대 경로')
# 2026-11-07 오탐 완화: 지난 기록에서 되풀이된 무해한 묶음은 통과, 작성 · 복사 · 실행과 섞인 삭제는 계속 막음
expect('H1 완화 삭제 뒤 Test-Path', h1("Remove-Item -LiteralPath 'F:\\AI\\P\\subagent_in\\x' -Recurse -Force; Test-Path 'F:\\AI\\P\\subagent_in\\x'", 'PowerShell'), 0)
expect('H1 완화 루트로 Set-Location 뒤 삭제', h1("Set-Location F:\\AI\\P; Remove-Item 'F:\\AI\\P\\subagent_in\\x' -Recurse -Force", 'PowerShell'), 0)
expect('H1 완화 밖 cd 뒤 rm', h1('cd /f/AI && rm -rf "/f/AI/P/subagent_in/x"'), 0)
expect('H1 완화 여러 삭제', h1('rm -rf /f/AI/P/a && rm -rf "/f/AI/P/b"'), 0)
expect('H1 완화 삭제 뒤 ls | grep', h1('rm -rf /f/AI/P/a; ls /f/AI/P | grep a'), 0)
expect('H1 완화 확인의 2>/dev/null', h1('rm -rf /f/AI/P/a; ls /f/AI/P/a 2>/dev/null'), 0)
expect('H1 Git Bash /tmp는 계속 막음 (/tmp · /tmp/.. · 현재 폴더 비교 불가)', h1('rm -rf /tmp/hb-stats'), 2, '절대 경로')
expect('H1 /tmp/.. 막음', h1('rm -rf /tmp/..'), 2, '절대 경로')
# /cross-review 2cb5d051ff 반영: 읽기 확인 조각의 경로 · 괄호 · 붙여 쓴 리다이렉트
expect('H1 묶음 경로 붙은 ls', h1('rm -rf /f/AI/P/x; ./ls'), 2, '묶여')
expect('H1 묶음 tools/cat', h1('rm -rf /f/AI/P/x && tools/cat a'), 2, '묶여')
expect('H1 묶음 PS 하위 식', h1("Write-Host (python -I run.py); Remove-Item 'F:\\AI\\P\\x' -Recurse", 'PowerShell'), 2, '묶여')
expect('H1 묶음 PS 스크립트 블록 속 Set-Location', h1("gi . | Select-Object @{e={Set-Location 'F:\\AI\\P\\x\\s'}}; Remove-Item 'F:\\AI\\P\\x' -Recurse", 'PowerShell'), 2)
expect('H1 묶음 프로세스 치환', h1('rm -rf /f/AI/P/x; cat <(python -I run.py)'), 2)
expect('H1 묶음 붙여 쓴 리다이렉트', h1('rm -rf /f/AI/P/x; echo ok>/f/AI/P/out.txt'), 2, '묶여')
expect('H1 묶음 따옴표 속 > 는 글자', h1('rm -rf /f/AI/P/x; echo "a > b"'), 0)
expect('H1 완화 git rm -r와 commit', h1('git rm -r --cached GoldenSet && git commit -m "x"'), 0)
expect('H1 완화 cd 뒤 작업 로그 heredoc 속 rmdir /s', h1("cd /f/AI/P && cat >> docs/log.md <<'EOF'\n- rmdir /s 로 지움\nEOF"), 0)
expect('H1 따옴표 글 속 rm과 실제 삭제는 계속 막음 (따옴표 검사)', h1('echo "rm -rf 끝"; rm -rf /f/AI/P/a'), 2, '따옴표')
expect('H1 묶음 mkdir · cp와 삭제', h1('mkdir -p /f/AI/P/x && cp a /f/AI/P/x && rm -rf /f/AI/P/y'), 2, '묶여')
expect('H1 묶음 삭제 뒤 실행', h1('rm -rf /f/AI/P/x && python -I run.py'), 2, '묶여')
expect('H1 묶음 확인에 리다이렉트', h1('rm -rf /f/AI/P/x; ls > /f/AI/P/out.txt'), 2, '묶여')
expect('H1 묶음 cd 상대 경로', h1('cd sub && rm -rf /f/AI/P/x'), 2, '절대 경로')
expect('H1 묶음 cd 변수', h1('cd $D; rm -rf /f/AI/P/x'), 2, '변수')
expect('H1 묶음 cd로 대상 안에 들어감', h1('cd /f/AI/P/x/sub; rm -rf /f/AI/P/x'), 2, '현재 폴더')
expect('H1 묶음 처음 현재 폴더도 봄 (cd 실패 대비)', h1('cd /f/AI; rm -rf /f/AI/P', cwd='F:\\AI\\P\\src'), 2, '현재 폴더')
expect('H1 묶음 PS 삭제 뒤 Copy-Item', h1("Remove-Item 'F:\\AI\\x' -Recurse; Copy-Item a b", 'PowerShell'), 2, '묶여')
expect('H1 변수', h1('rm -rf "$S/raw"'), 2, '변수')
expect('H1 -fr', h1('rm -fr x'), 2)
expect('H1 --recursive', h1('rm --recursive -f x'), 2)
expect('H1 -- 뒤 경로', h1('rm -rf -- x'), 2, "'x'")
expect('H1 PowerShell 삭제 뒤 Test-Path는 통과 (2026-11-07 완화)', h1('Remove-Item -Recurse -Force "F:\\x"; Test-Path "F:\\x"', 'PowerShell'), 0)
expect('H1 PowerShell ; 로 작성과 묶음', h1('Remove-Item -Recurse -Force "F:\\x"; Set-Content F:\\y.txt a', 'PowerShell'), 2, '묶여')
expect('H1 PowerShell 끝 \\ 경로 따옴표 (\\는 이스케이프 아님)', h1('Set-Location "F:\\AI\\x\\"; Remove-Item -Recurse -Force F:\\AI\\x', 'PowerShell'), 2, '현재 폴더')
expect('H1 rmdir /s', h1('rmdir /s /q build'), 2)
expect('H1 rm 단일 파일은 통과', h1('rm -f a.txt && echo ok'), 0)
expect('H1 git rm -r --cached 는 대상 아님', h1('git rm -r --cached GoldenSet'), 0)
expect('H1 경로 붙은 rm', h1('/usr/bin/rm -rf x'), 2, "'x'")
expect('H1 \\rm', h1('\\rm -rf x'), 2)
expect('H1 명령 치환 안', h1('echo "$(rm -rf x)"'), 2, '명령 치환')
expect('H1 백틱 안', h1('echo `rm -rf /f/x`'), 2, '명령 치환')
expect('H1 루트', h1('rm -rf /'), 2, '루트')
expect('H1 드라이브 루트', h1('rm -rf /f/'), 2, '루트')
expect('H1 PowerShell 드라이브 상대 \\x', h1('Remove-Item -Recurse \\AI\\x', 'PowerShell'), 2, '절대 경로')
expect('H1 셸 현재 폴더가 대상 안', h1('rm -rf /f/AI/x', cwd='F:\\AI\\x\\sub'), 2, '현재 폴더')
expect('H1 셸 현재 폴더 = 대상', h1('Remove-Item -Recurse -Force "F:\\AI\\x"', 'PowerShell', cwd='F:\\AI\\x'), 2, '현재 폴더')
expect('H1 셸 현재 폴더가 대상 밖', h1('rm -rf /f/AI/x', cwd='F:\\AI'), 0)
expect('H1 따옴표 안 문자열은 명령 아님', h1('git commit -m "rm -rf 규칙 && 정리"'), 0)
expect('H1 grep -r 은 삭제 아님', h1('grep -rn foo src | head'), 0)
# 본문에 재귀 삭제 글자가 있으면 묶음의 모든 조각이 데이터 전용이어야 한다 (H5와 같은 규칙)
expect('H1 heredoc 본문의 rm -rf + cd · echo 묶음은 막힘', h1("cd /f/x && cat >> log.md <<'EOF'\n- `cd … && rm -rf y`가 막히는지\nEOF\necho ok"), 2, '데이터 전용')
expect('H1 heredoc 본문의 rm -rf 는 명령 아님 (cat 단독)', h1("cat >> /f/x/log.md <<'EOF'\n- `cd … && rm -rf y`가 막히는지\nEOF"), 0)
expect('H1 heredoc 뒤의 진짜 rm -rf', h1("cat > a <<'EOF'\nx\nEOF\nrm -rf /f/x"), 2, '묶여')
expect('H1 끝 표시 없는 heredoc 은 본문을 지우지 않음', h1("cat > a <<'EOF'\nrm -rf x"), 2)
expect('H1 따옴표 안 <<EOF 는 heredoc 아님', h1('echo "a<<EOF b"\nrm -rf x'), 2)
expect('H1 here-string <<< 는 heredoc 아님', h1('cat <<< "x"\nrm -rf x'), 2)
expect('H1 PowerShell here-string 본문', h1("git commit -m @'\nrm -rf 정리 규칙\n'@", 'PowerShell'), 0)
# 따옴표 짝 · 주석 · heredoc 경계 (이전 판 실측 exit 0)
expect('H1 주석 속 따옴표로 숨긴 rm', h1("ls # '\nrm -rf build\n'"), 2, '묶여')
expect("H1 $'…'로 숨긴 rm", h1("echo $'\\'' ; rm -rf build ; 'z'"), 2, '묶여')
# 본문 검사가 먼저 막는다 (본문의 $( )는 실행되므로 data_only도 아님) — 이전에는 '명령 치환'으로 막힘
expect('H1 따옴표 없는 heredoc 속 $()', h1('cat <<EOF\n$(rm -rf build)\nEOF'), 2, '[정리 명령 차단]')
expect('H1 PowerShell 굽은 따옴표로 숨긴 삭제', h1("echo 'a’; Remove-Item -Recurse -Force x; ‘'", 'PowerShell'), 2)
expect('H1 $#는 주석 아님', h1('echo $#; rm -rf x'), 2, '절대 경로')
expect('H1 ${#a}는 주석 아님', h1('echo ${#a}; rm -rf x'), 2, '절대 경로')
expect('H1 커밋 "$(cat heredoc)" 본문의 rm -rf 는 명령 아님', h1("git commit -m \"$(cat <<'EOF'\nrm -rf 정리 (don't)\nEOF\n)\""), 0)
# /cross-review 8722c947f7 반영 (bash · PowerShell 실측과 같게)
for name, cmd, tool in [
    ('$( ) 뒤 #는 낱말의 일부 (bash)', 'echo $(true)#; rm -rf x', 'Bash'),
    ('<<<의 둘째 <는 heredoc 아님', 'cat <<<true\nrm -rf build\ntrue', 'Bash'),
    ('이스케이프 공백 뒤 #는 낱말 가운데', 'echo \\ #; rm -rf build', 'Bash'),
    ('끝 표시 뒤 공백 줄은 끝 아님', "cat <<'EOF'\nEOF \n'\nEOF\nrm -rf build\n# '\n", 'Bash'),
    ("끝 표시 이어 붙임 <<E'OF'", "cat <<E'OF'\nEOF\nrm -rf x\nE", 'Bash'),
    ('> 뒤 #는 주석', "echo hi >#'\nrm -rf build\n'", 'Bash'),
    ('산술 << 는 heredoc 아님', 'y=1; echo $(( 1<<y ))\nrm -rf x\ny', 'Bash'),
    ('큰따옴표 $( ) 속 case', 'echo "$(case x in x) rm -rf y;; esac)"', 'Bash'),
    ('PS { 뒤 #는 주석', "& {#'\n}\nRemove-Item -Recurse -Force x\n'", 'PowerShell'),
    ("PS ${a'b} 속 따옴표", "${a'b} = 1\nRemove-Item -Recurse -Force x\n'", 'PowerShell'),
    ('PS --% 뒤는 글자 그대로', "cmd /c echo --% a'\nRemove-Item -Recurse -Force x\n'", 'PowerShell'),
]:
    expect(f'H1 셸 문법 {name}', h1(cmd, tool), 2, '[정리 명령 차단]')
# /self-review security 반영분 재검토 (실측 exit 0이던 형식)
for name, cmd, tool in [
    ('공백 없는 & 뒤 rm', 'ls&rm -rf build', 'Bash'),
    ('${…} 속 산술 <<', 'y=1; echo ${a:0:1<<y}\nrm -rf x\ny}', 'Bash'),
    ('$[…] 속 <<', 'y=1; echo $[1<<y]\nrm -rf x\ny]', 'Bash'),
    ("${…} 속 따옴표 } (${a:-'}'})", "echo ${a:-'}'}\nrm -rf x\n'", 'Bash'),
    ('PS a=#b 뒤 ;', 'echo a=#b; Remove-Item -Recurse -Force x', 'PowerShell'),
    ('PS 낱말 가운데 <#', 'echo a<#c; Remove-Item -Recurse -Force x #>b', 'PowerShell'),
    ('PS ${a`}b} 속 따옴표', "${a`}b'} = 1\nRemove-Item -Recurse -Force x\n'", 'PowerShell'),
    ('끝 표시 없는 heredoc 뒤 따옴표', "cat <<'EOF'\n'\nrm -rf x", 'Bash'),
]:
    expect(f'H1 재검토 {name}', h1(cmd, tool), 2, '[정리 명령 차단]')
expect('H1 리다이렉트 2>&1 은 묶음 아님', h1('rm -rf /f/AI/ToolWithCS/subagent_in/x 2>&1'), 0)
# heredoc 본문 · 재귀 삭제 옵션 형식 (실측: 모두 재귀 삭제)
for name, cmd, tool in [
    ('heredoc을 bash에', "bash <<'EOF'\nrm -rf build\nEOF", 'Bash'),
    ('heredoc을 python에', "python - <<'EOF'\nimport os; os.system('rm -rf build')\nEOF", 'Bash'),
    ('here-string을 iex에', "@'\nRemove-Item -Recurse -Force x\n'@ | iex", 'PowerShell'),
    ('rm --rec 상대 경로', 'rm --rec -f build', 'Bash'),
    ('rm --r 상대 경로', 'rm --r build', 'Bash'),
    ('-Recurse:$true 상대 경로', 'Remove-Item build -Recurse:$true -Force', 'PowerShell'),
    ('en dash –Recurse', 'Remove-Item build –Recurse -Force', 'PowerShell'),
    ('em dash —Recurse', 'Remove-Item build —Recurse -Force', 'PowerShell'),
    ('horizontal bar ―r', 'rm build ―r', 'PowerShell'),
]:
    expect(f'H1 형식 {name}', h1(cmd, tool), 2, '[정리 명령 차단]')
expect('H1 rm --rec 절대 경로 단독은 통과', h1('rm --rec -f /f/AI/ToolWithCS/subagent_in/x'), 0)
expect('H1 -Recurse:$false는 재귀 아님', h1('Remove-Item build -Recurse:$false -Force', 'PowerShell'), 0)
expect('H1 커밋 heredoc 본문의 rm -rf 문구', h1("git commit -F - <<'EOF'\n정리: rm -rf build 대신 Remove-Item\nEOF"), 0)
expect('H1 cat > 파일 heredoc 본문의 rm -rf', h1("cat > notes.md <<'EOF'\nrm -rf build\nEOF"), 0)
expect('H1 rm --force는 재귀 아님', h1('rm --force build'), 0)
# /self-review security 625b223591 반영 (실측 exit 0이던 형식)
for name, cmd, tool in [
    ('따옴표 없는 heredoc을 bash에', 'bash <<EOF\nrm -rf build\nEOF', 'Bash'),
    ('@"…"@ 를 iex에', '@"\nRemove-Item build -Recurse -Force\n"@ | iex', 'PowerShell'),
    ('$env: 대입 + git commit', "$env:GIT_EDITOR = @'\nrm -rf build\n'@\ngit commit", 'PowerShell'),
    ('훅 파일 쓰기 + git commit', "cat > .git/hooks/pre-commit <<'EOF'\nrm -rf build\nEOF\ngit commit -m x", 'Bash'),
    ('bash 줄 이음 뒤 -rf', 'rm \\\n -rf build', 'Bash'),
    ('PS 백틱 줄 이음 뒤 -Recurse', 'Remove-Item build `\n -Recurse -Force', 'PowerShell'),
    ('PS splatting', 'Remove-Item @p', 'PowerShell'),
]:
    expect(f'H1 재검토2 {name}', h1(cmd, tool), 2, '[정리 명령 차단]')
expect('H1 en dash 폴더 · 셸 현재 폴더가 그 안', h1('Remove-Item -LiteralPath "F:\\a\u2013b" -Recurse -Force', 'PowerShell', cwd='F:\\a\u2013b\\sub'), 2, '현재 폴더')
expect('H1 en dash 폴더 · 밖에서 지움', h1('Remove-Item -LiteralPath "F:\\a\u2013b" -Recurse -Force', 'PowerShell', cwd='F:\\AI'), 0)
expect('H1 cat <<EOF | git commit -F - (파일 안 씀)', h1("cat <<'EOF' | git commit -F -\nrm -rf 정리\nEOF"), 0)
expect('H1 bash 줄 이음 절대 경로 단독', h1('rm \\\n -rf /f/AI/ToolWithCS/subagent_in/x'), 0)
# 따옴표 문자열로 넘긴 명령 — 허용 목록
for name, cmd, tool in [
    ('bash -c', 'bash -c "rm -rf build"', 'Bash'),
    ('powershell -Command', 'powershell -Command "Remove-Item build -Recurse -Force"', 'Bash'),
    ('iex', "iex 'Remove-Item build -Recurse -Force'", 'PowerShell'),
    ('cmd /c', 'cmd /c "rmdir /s /q build"', 'PowerShell'),
    ('eval', "eval 'rm -rf build'", 'Bash'),
    ('python -c', "python -c \"import os; os.system('rm -rf build')\"", 'Bash'),
    ('tclsh (목록 밖 실행기)', 'tclsh -c "rm -rf build"', 'Bash'),
    ('echo | bash', 'echo "rm -rf build" | bash', 'Bash'),
    ('rg --pre', 'rg --pre sh "rm -rf build" .', 'Bash'),
]:
    expect(f'H1 따옴표 명령 {name}', h1(cmd, tool), 2, '따옴표 문자열')
for name, cmd, tool in [
    ('git commit -m', 'git commit -m "정리: rm -rf build 대신 Remove-Item"', 'Bash'),
    ('grep', 'grep -rn "rm -rf" docs', 'Bash'),
    ('Select-String', "Select-String -Path README.md -Pattern 'Remove-Item x -Recurse'", 'PowerShell'),
    ('echo', 'echo "rm -rf 은 쓰지 않는다"', 'Bash'),
    ('git log --grep', 'git log --grep "rm -rf"', 'Bash'),
]:
    expect(f'H1 따옴표 글자 통과 {name}', h1(cmd, tool), 0)
# /self-review security ddb47044d1 반영 (실측 exit 0이던 형식)
for name, cmd, tool, why in [
    ('겹친 따옴표 -Command', 'powershell -Command "Remove-Item "$p" -Recurse -Force"', 'Bash', '따옴표 문자열'),
    ('cmd /c rmdir 따옴표 없음', 'cmd /c rmdir /s /q build', 'Bash', '해석하지 못했다'),
    ('-Command 따옴표 없음', 'powershell -Command Remove-Item build -Recurse -Force', 'Bash', '해석하지 못했다'),
    ('env rm', 'env rm -rf build', 'Bash', '해석하지 못했다'),
    ('timeout rm', 'timeout 5 rm -rf build', 'Bash', '해석하지 못했다'),
    ('git grep -iO', 'git grep -iO"rm -rf build" pattern', 'Bash', '따옴표 문자열'),
    ('git log --output 훅 + commit', 'git log -1 --format="rm -rf build" --output=.git/hooks/pre-commit; git commit -m x', 'Bash', '따옴표 문자열'),
    ('echo >> config + git status', 'echo "fsmonitor = rm -rf build" >> .git/config; git status', 'Bash', '따옴표 문자열'),
    ('쪼갠 이름 r""m', "bash -c 'r\"\"m -rf build'", 'Bash', '따옴표 문자열'),
    ('백틱으로 쪼갠 Remove-It`em', "iex 'Remove-It`em x -Recurse'", 'PowerShell', '따옴표 문자열'),
]:
    expect(f'H1 재검토3 {name}', h1(cmd, tool), 2, why)
expect('H1 git log 단독 (쓰기 없음)', h1('git log --grep "rm -rf"'), 0)
expect('H1 git -C 경로 commit heredoc 본문의 rm', h1("git -C /f/x commit -q -F - <<'EOF'\ncmd /c rmdir /s 막음\nEOF"), 0)
expect('H1 git -c 설정 + commit heredoc은 막힘', h1("git -c core.hooksPath=x commit -F - <<'EOF'\nrm -rf build\nEOF"), 2, '데이터 전용')
expect('H1 줄 이음 뒤 #는 앞 상태 (주석)', h1('echo a \\\n#c; rm -rf x'), 0)
expect('H1 PS $( ) 뒤 #는 주석', h1('echo $(1)#; Remove-Item -Recurse -Force x', 'PowerShell'), 0)
expect("H1 끝 표시 이어 붙임 본문은 데이터 (<<'E'OF)", h1("cat <<'E'OF\nE\nrm -rf build\nEOF\n"), 0)
expect('H1 다른 도구는 무시', run('cleanup_guard.py', {'tool_name': 'Read', 'tool_input': {}}), 0)
expect('H1 깨진 입력은 막음', (lambda r: (r.returncode, r.stderr.decode('utf-8', 'replace')))(subprocess.run([sys.executable, '-I', os.path.join(HERE, 'cleanup_guard.py')], input=b'{', capture_output=True)), 2)

# ── H2 push_guard ─────────────────────────────────────────────────────
tmp = tempfile.mkdtemp(prefix='hb-pushguard-')
try:
    remote = os.path.join(tmp, 'remote.git')
    private = os.path.join(tmp, 'private.git')
    repo = os.path.join(tmp, 'repo')
    proj = os.path.join(tmp, 'proj')  # CLAUDE_PROJECT_DIR (개인 패턴 파일 위치)
    os.makedirs(os.path.join(proj, '.claude'))
    with open(os.path.join(proj, '.claude', 'push_guard.local.txt'), 'w', encoding='utf-8') as f:
        f.write('# 개인 값\nperson@example\\.com\nallow: ^sk-ALLOWED\n')

    def g(*args: str, cwd: str = repo) -> None:
        subprocess.run(['git', *args], cwd=cwd, check=True, capture_output=True)

    for bare in (remote, private):
        subprocess.run(['git', 'init', '--bare', '-q', bare], check=True, capture_output=True)
    subprocess.run(['git', 'init', '-q', '-b', 'main', repo], check=True, capture_output=True)
    g('config', 'user.email', 't@t'); g('config', 'user.name', 't'); g('config', 'commit.gpgsign', 'false')
    g('remote', 'add', 'origin', remote)
    g('remote', 'add', 'private', private)

    def commit(path: str, text: str) -> None:
        full = os.path.join(repo, path)
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, 'w', encoding='utf-8') as f:
            f.write(text)
        g('add', '-A'); g('commit', '-q', '-m', 'c')

    env = dict(os.environ, CLAUDE_PROJECT_DIR=proj)

    def h2(cmd: str = 'git push origin HEAD:main', tool: str = 'Bash', extra: dict | None = None) -> tuple[int, str]:
        return run('push_guard.py', {'tool_name': tool, 'tool_input': {'command': cmd}, 'cwd': repo}, dict(env, **(extra or {})))

    SECRET = 'sk-' + 'Q7vR2mK9pL4xN8wT3bZ6'  # 이어 붙여 만든다 — 이 파일 자체가 공개 저장소 푸시 검사(H2)에 걸리지 않게
    commit('a.txt', 'hello\n')
    expect('H2 깨끗한 새 커밋', h2(), 0)
    g('push', '-q', 'origin', 'HEAD:main')
    expect('H2 푸시할 것 없음', h2(), 0)
    # 2026-11-07 오탐 완화
    expect('H2 완화 push | tail', h2('git push origin HEAD:main 2>&1 | tail -3'), 0)
    expect('H2 완화 앞 git log', h2('git log --oneline origin/main..HEAD; git push origin HEAD:main | tail -3'), 0)
    expect('H2 완화 PS Select-Object', h2('git push origin main 2>&1 | Select-Object -Last 3', 'PowerShell'), 0)
    expect('H2 완화 git stash push', h2('git stash push src/a.cs && git stash list'), 0)
    expect('H2 완화 커밋 메시지 속 git push', h2('git add -A && git commit -m "git push 전 비밀 값 검사 고침"'), 0)
    expect('H2 완화 cd 뒤 작업 로그 heredoc 속 push', h2("cd /tmp && cat >> log.md <<'EOF'\ngit push origin main 끝\nEOF"), 0)
    expect('H2 push 뒤 다른 명령', h2('git push origin HEAD:main && echo ok'), 2, '묶여')
    expect('H2 push 앞 git fetch', h2('git fetch && git push origin HEAD:main'), 2, '묶여')
    expect('H2 push 둘', h2('git push origin HEAD:main; git push origin HEAD:dev'), 2, '묶여')
    expect('H2 push 뒤 파일로 리다이렉트', h2('git push origin HEAD:main | tail -3 > out.txt'), 2, '묶여')
    expect('H2 bash -c 속 git push는 계속 막음', h2('git add -A && bash -c "git push origin main"'), 2, '따옴표')
    # /cross-review 2cb5d051ff 반영
    expect('H2 단독 git stash push', h2('git stash push -m "x" src/a.cs'), 0)
    expect('H2 앞 git log --output은 막음 (브랜치 ref 바꾸기)', h2('git log -1 --format=%H --output=.git/refs/heads/main x; git push origin main'), 2, '묶여')
    expect('H2 앞 git log --outp 줄임도 막음', h2('git log --outp=f -1; git push origin main'), 2, '묶여')
    expect('H2 뒤 붙여 쓴 리다이렉트', h2('git push origin HEAD:main | tail -3>out.txt'), 2, '묶여')
    expect('H2 경로 붙은 tail', h2('git push origin HEAD:main | ./tail -3'), 2, '묶여')
    expect('H2 커밋 메시지 묶음의 붙여 쓴 리다이렉트', h2('git add -A && git commit -m "git push 고침">log.txt'), 2, '따옴표')

    commit('b.cs', f'var k = "{SECRET}";\n')
    expect('H2 API 키 모양', h2(), 2, 'sk-Q…')
    expect('H2 원격 생략 (origin 기본)', h2('git push'), 2, '비밀 값')
    expect('H2 PowerShell', h2('git push origin main', 'PowerShell'), 2, '비밀 값')
    expect('H2 commit 과 묶음 (실행 전이라 못 봄)', h2('git add -A && git commit -m x && git push'), 2, '묶여')
    expect('H2 완화 묶음에서도 비밀 값 검사', h2('git log -1; git push origin HEAD:main 2>&1 | tail -3'), 2, '비밀 값')
    expect('H2 값이 stash로 끝나도 push (빈틈 회귀)', h2('git -c user.name=stash push origin main'), 2, '전역 옵션')
    expect('H2 묶음에서 값이 stash로 끝나도 push', h2('git log -1; git -c x=stash push origin main'), 2, '전역 옵션')
    expect('H2 cd 와 함께', h2(f'cd {repo} ; git push'), 2)
    expect('H2 전역 옵션', h2('git --no-pager push origin main'), 2, '전역 옵션')
    expect('H2 --all', h2('git push --all origin'), 2, '--all')
    expect('H2 강제 refspec', h2('git push origin +main'), 2, '강제')
    expect('H2 명령 치환', h2('echo "$(git push)"'), 2, '명령 치환')
    g('reset', '-q', '--hard', 'HEAD~1')

    commit('c.md', '연락: person@example.com\n')
    expect('H2 개인 값 파일 패턴 (-C 경로)', h2('git -C "%s" push' % repo), 2, '비밀 값')
    g('reset', '-q', '--hard', 'HEAD~1')

    commit('Client/docs/image/2026-10-19_x/a.png', 'x')
    expect('H2 docs/image 아래 파일', h2(), 2, 'docs/image/')
    g('reset', '-q', '--hard', 'HEAD~1')

    commit('GoldenSet/골든사례/input.md', 'x')
    expect('H2 GoldenSet 아래 한글 경로 (quotePath)', h2(), 2, 'GoldenSet/')
    g('reset', '-q', '--hard', 'HEAD~1')

    commit('d.txt', f'이 줄은 지워진다 {SECRET}\n')
    commit('d.txt', 'clean\n')
    expect('H2 앞 커밋이 추가한 비밀은 뒤에서 지워도 막음', h2(), 2, '비밀 값')
    g('reset', '-q', '--hard', 'HEAD~2')

    commit('e.cs', 'InlineData("sk-abcdefghijklmnopqrstu")\n')
    expect('H2 시험용 가짜 키는 통과', h2(), 0)
    g('reset', '-q', '--hard', 'HEAD~1')

    commit('e2.cs', f'a("sk-abcdefghijklmnopqrstu") b("{SECRET}")\n')
    expect('H2 같은 줄 가짜 뒤 진짜', h2(), 2, '비밀 값')
    g('reset', '-q', '--hard', 'HEAD~1')

    commit('f.cs', 'k = "' + 'sk-' + 'ALLOWEDq9w8e7r6t5y4u3"\n')
    expect('H2 allow: 예외', h2(), 0)
    g('reset', '-q', '--hard', 'HEAD~1')

    commit('h.md', '++ 로 시작하는 줄 ' + SECRET + '\n'.replace('\n', '') + '\n')
    with open(os.path.join(repo, 'h.md'), 'w', encoding='utf-8') as f:
        f.write('++ ' + SECRET + '\n')
    g('add', '-A'); g('commit', '-q', '--amend', '-m', 'c')
    expect("H2 '++ '로 시작하는 추가 줄 (diff에서 '+++ ')", h2(), 2, '비밀 값')
    g('reset', '-q', '--hard', 'HEAD~1')

    # 다른 브랜치: HEAD는 깨끗하고 feature에만 비밀
    g('checkout', '-q', '-b', 'feature'); commit('g.cs', f'"{SECRET}"\n'); g('checkout', '-q', 'main')
    expect('H2 HEAD 깨끗', h2(), 0)
    expect('H2 다른 브랜치 push', h2('git push origin feature'), 2, '비밀 값')
    # 비공개 원격에만 올린 커밋을 공개 원격으로
    g('push', '-q', 'private', 'feature')
    expect('H2 비공개 원격에 있어도 대상 원격 기준', h2('git push origin feature'), 2, '비밀 값')
    # merge 커밋에만 들어간 줄
    g('checkout', '-q', '-b', 'side', 'main'); commit('m.txt', 'side\n'); g('checkout', '-q', 'main'); commit('m2.txt', 'main\n')
    subprocess.run(['git', 'merge', '-q', '--no-ff', '--no-commit', 'side'], cwd=repo, capture_output=True)
    with open(os.path.join(repo, 'merged.txt'), 'w', encoding='utf-8') as f:
        f.write(SECRET + '\n')
    g('add', '-A'); g('commit', '-q', '-m', 'merge')
    expect('H2 merge 커밋에만 들어간 줄', h2(), 2, '비밀 값')
    expect('H2 커밋 수 상한', h2(extra={'PUSH_GUARD_MAX_COMMITS': '1'}), 2, '상한')
    g('reset', '-q', '--hard', 'origin/main')

    if os.name == 'nt':
        drive, rest = os.path.splitdrive(repo)
        msys = '/' + drive[0].lower() + rest.replace('\\', '/')
        commit('i.cs', f'var k = "{SECRET}";\n')
        expect('H2 Git Bash 경로 (-C /c/…)', h2(f'git -C {msys} push --dry-run origin main'), 2, '비밀 값')
        g('reset', '-q', '--hard', 'HEAD~1')
        expect('H2 Git Bash 경로 깨끗함', h2(f'git -C {msys} push --dry-run origin main'), 0)

    expect('H2 push가 아닌 git 명령', h2('git status'), 0)
    expect('H2 따옴표 안 push', h2('git commit -m "git push 전에 검사"'), 0)
    expect('H2 heredoc 커밋 메시지 속 push', h2("git commit -q -F - <<'EOF'\n푸시 전 git push 검사\nEOF"), 0)
    expect('H2 주석 속 따옴표로 숨긴 push (이전 판 exit 0)', h2("ls # '\ngit push origin main && echo\n'"), 2, '묶여')
    expect('H2 heredoc을 bash에', h2("bash <<'EOF'\ngit push origin main\nEOF"), 2, '데이터 전용')
    expect('H2 here-string을 iex에', h2("@'\ngit push origin main\n'@ | iex", 'PowerShell'), 2, '데이터 전용')
    expect('H2 커밋 heredoc 본문의 git push 문구', h2("git commit -q -F - <<'EOF'\n다음에 git push 할 것\nEOF"), 0)
    expect('H2 따옴표 없는 heredoc을 bash에', h2('bash <<EOF\ngit push origin main\nEOF'), 2, '데이터 전용')
    expect('H2 줄 이음 뒤 push', h2('git \\\n push origin main && echo'), 2, '묶여')
    expect('H2 bash -c 속 push', h2('bash -c "git push origin main"'), 2, '따옴표 문자열')
    expect('H2 iex 속 push', h2("iex 'git push origin main'", 'PowerShell'), 2, '따옴표 문자열')
    expect('H2 grep 속 push 글자', h2('grep -rn "git push" docs'), 0)
    expect('H2 겹친 따옴표 bash -c', h2('bash -c "git -C "$R" push"'), 2, '따옴표 문자열')
    expect('H2 쪼갠 이름 gi""t push', h2("bash -c 'gi\"\"t push origin main'"), 2, '따옴표 문자열')
    expect('H2 없는 원격', h2('git push nowhere main'), 2, '원격')
    expect('H2 저장소가 아닌 곳', run('push_guard.py', {'tool_name': 'Bash', 'tool_input': {'command': 'git push'}, 'cwd': tmp}, env), 2, '검사 중 오류')
finally:
    shutil.rmtree(tmp, ignore_errors=True)

# ── H3·H4 run_mode_guard ──────────────────────────────────────────────
tmp = tempfile.mkdtemp(prefix='hb-runguard-')
try:
    env = dict(os.environ, CLAUDE_PROJECT_DIR=tmp)

    def h3(mode: str, reason: str = '이유', own: str = '', key: str = 'ownReview') -> tuple[int, str]:
        return run('run_mode_guard.py', {'tool_name': 'mcp__hb-orchestration__run_mode',
                                         'tool_input': {'mode': mode, 'input': 'x', 'reason': reason, key: own}}, env)

    expect('H3 계획서 없음 review_main', h3('review_main'), 2, '계획서')
    expect('H3 design_lite는 계획서 검사 안 함', h3('design_lite'), 0)
    plans = os.path.join(tmp, 'A', 'B', 'docs', 'plans')
    os.makedirs(plans)
    old = os.path.join(plans, '2026-01-01_예전.md')
    open(old, 'w', encoding='utf-8').write('x')
    os.utime(old, (0, 0))
    expect('H3 예전 계획서만', h3('review_main'), 2)
    # 직접 코딩 흐름: [plan:파일] 태그면 날짜와 상관없이 그 파일이 있는지만 · 7일 안의 이름 · 고친 날
    expect('H3 오래된 계획서 + [plan:태그]', h3('review_main', '[plan:2026-01-01_예전.md] 이유'), 0)
    expect('H3 ownReview 태그 뒤의 [plan:태그]', h3('review_main', '이유 [PLAN:2026-01-01_예전.md]'), 0)
    expect('H3 [plan:태그]의 파일이 없음', h3('review_main', '[plan:없는-계획.md] 이유'), 2, '[plan:')
    expect('H3 [plan:태그]에 경로는 형식 오류', h3('review_main', '[plan:../x/2026-01-01_예전.md] 이유'), 2, '형식')
    expect('H3 [plan:태그]에 .md 빠짐은 형식 오류', h3('review_main', '[plan:2026-01-01_예전] 이유'), 2, '형식')
    expect('H3 [plan:태그] 뒤 공백은 형식 오류', h3('review_main', '[plan:2026-01-01_예전.md ] 이유'), 2, '형식')
    nfd = unicodedata.normalize('NFD', '2026-01-01_예전.md')
    expect('H3 [plan:태그] NFD로 써도 같은 계획서', h3('review_main', f'[plan:{nfd}] 이유'), 0)
    today = datetime.date.today()

    def dated(days: int, name: str, mtime_days: int | None = None) -> str:
        path = os.path.join(plans, f'{(today - datetime.timedelta(days=days)).isoformat()}_{name}.md')
        open(path, 'w', encoding='utf-8').write('x')
        when = (datetime.datetime.now() - datetime.timedelta(days=mtime_days)).timestamp() if mtime_days is not None else 0
        os.utime(path, (when, when))
        return path

    p = dated(6, '엿새')
    expect('H3 6일 전 날짜 이름', h3('review_main'), 0)
    os.remove(p)
    p = dated(7, '이레')
    expect('H3 7일 전 날짜 이름은 막음', h3('review_main'), 2, '[plan:')
    os.remove(p)
    p = dated(400, '고친지-사흘', mtime_days=3)
    expect('H3 이름은 오래돼도 사흘 전에 고침', h3('review_main'), 0)
    os.remove(p)
    open(os.path.join(plans, f'{today.isoformat()}_오늘.md'), 'w', encoding='utf-8').write('x')
    expect('H3 오늘 계획서 (깊이 4)', h3('review_main'), 0)
    expect('H3 review_high', h3('review_high'), 0)
    expect('H3 예전 이름 review는 검사 안 함 (서버가 거절)', h3('review'), 0)

    good = '## 지적\n- C1 [major] a.cs:1 — 문제\n## 자료 부족\n- 없음\n## 읽은 파일\n- subagent_in/2026-10-19_x/a.cs:1-10\n'
    tag = '[ownReview:self-reviewer] 이유'
    expect('H4 원문 형식 (한글, UTF-8)', h3('review_main', tag, good), 0)
    # 실제 /cross-review 모양: [ownReview:…] 원문이 H4를 지난 뒤 H3가 [plan:] 태그로 본다 (오늘 계획서가 있어도 태그가 우선)
    expect('H3+H4 ownReview 태그 + [plan:있는 파일]', h3('review_main', tag + ' [plan:2026-01-01_예전.md]', good), 0)
    expect('H3+H4 ownReview 태그 + [plan:없는 파일]', h3('review_main', tag + ' [plan:없는.md]', good), 2, '[plan:없는.md]')
    expect('H4 키 이름 own_review', h3('review_main', tag, good, key='own_review'), 0)
    expect('H4 요약 (읽은 파일 없음)', h3('review_main', tag, '## 지적\n- C1 요약\n(상세는 첫 호출과 같다)'), 2, "'## 읽은 파일'")
    expect('H4 폴더 밖 읽음', h3('review_main', tag, good.replace('subagent_in/2026-10-19_x/a.cs', 'docs/worklog.md')), 2, 'subagent_in/ 밖')
    expect('H4 비슷한 폴더 이름', h3('review_main', tag, good.replace('subagent_in/', 'not_subagent_in/')), 2, 'subagent_in/ 밖')
    expect('H4 .. 로 나감', h3('review_main', tag, good.replace('subagent_in/2026-10-19_x/a.cs', 'subagent_in/../docs/worklog.md')), 2, 'subagent_in/ 밖')
    expect('H4 Windows 절대 경로', h3('review_main', tag, good.replace('subagent_in/2026-10-19_x/a.cs:1-10', 'F:\\AI\\subagent_in\\x\\a.cs:1-44 (첨부 포함)')), 0)
    expect('H4 검색 기록 줄 (경로 없음)', h3('review_main', tag, good + '- Grep: 폴더 전체에서 `Tool` 사용처\n'), 0)
    expect('H4 검색 기록 줄 (폴더 안 경로)', h3('review_main', tag, good + '- Glob: subagent_in/2026-10-19_x/*.cs\n'), 0)
    expect('H4 검색 기록 줄 (폴더 밖 경로)', h3('review_main', tag, good + '- Grep: F:\\AI\\ToolWithCS\\docs 에서 Tool\n'), 2, 'subagent_in/ 밖')
    expect('H4 검색 줄 아닌 자유 문장', h3('review_main', tag, good + '- 폴더 전체를 훑어봄\n'), 2, 'subagent_in/ 밖')
    expect('H4 태그 없으면 형식 검사 안 함', h3('design_lite', '이유', '자유 형식'), 0)
    expect('H4 지적 없음', h3('review_main', tag, '## 읽은 파일\n- subagent_in/x\n'), 2, "'## 지적'")
    expect('H4 perf_main 원문 형식', h3('perf_main', tag, good), 0)
    expect('H4 perf_main 요약 (안내에 /mode)', h3('perf_main', tag, '## 지적\n- C1 요약'), 2, '/mode 3단계')
    # 2026-10-22 '## 비고' 칸: 읽은 파일 검사는 그대로, 비고는 경로 검사 안 함
    explain = '- 폴더 안에서 Grep으로 StepResult 정의를 찾았으나 없었다.\n'
    expect('H4 비고 칸의 설명 줄', h3('review_main', tag, good + '## 비고\n' + explain), 0)
    expect('H4 비고 칸의 폴더 밖 경로 (검사 대상 아님)', h3('review_main', tag, good + '## 비고\n- docs/worklog.md 는 받지 못함\n'), 0)
    expect('H4 읽은 파일의 설명 줄은 계속 막음 (10-22 실사용 차단 문구)', h3('review_main', tag, good + explain + '## 비고\n- 없음\n'), 2, 'subagent_in/ 밖')
    expect('H4 두 번째 읽은 파일 절의 폴더 밖 경로', h3('review_main', tag, good + '## 비고\n- 없음\n## 읽은 파일\n- docs/worklog.md:1-5\n'), 2, 'subagent_in/ 밖')
    # 검토형 서브에이전트 추가
    expect('H4 perf-reviewer 태그', h3('perf_main', '[ownReview:perf-reviewer] 이유', good), 0)
    expect('H4 perf-reviewer 요약은 막음', h3('perf_main', '[ownReview:perf-reviewer] 이유', '## 지적\n- C1 요약'), 2, 'perf-reviewer 결과 원문이 아니다')
    both = good + '\n' + good
    expect('H4 두 서브에이전트 이어 붙임', h3('review_main', '[ownReview:self-reviewer+security-reviewer] 이유', both), 0)
    expect('H4 두 서브에이전트인데 결과 하나', h3('review_main', '[ownReview:self-reviewer+security-reviewer] 이유', good), 2, "'## 지적' 절이 1개")
    expect('H4 두 번째 결과의 폴더 밖 경로', h3('review_main', '[ownReview:self-reviewer+security-reviewer] 이유',
                                          good + '\n' + good.replace('subagent_in/2026-10-19_x/a.cs', 'docs/worklog.md')), 2, 'subagent_in/ 밖')
    expect('H4 모르는 서브에이전트', h3('review_main', '[ownReview:web-searcher] 이유', good), 2, '모른다')
    expect('H4 겹친 이름', h3('review_main', '[ownReview:self-reviewer+self-reviewer] 이유', both), 2, '두 번')
    # /self-review fffbc4440a 반영: 블록마다 원문 · 대소문자 · 들여쓴 제목
    expect('H4 둘째 결과가 요약', h3('review_main', '[ownReview:self-reviewer+security-reviewer] 이유', good + '\n## 지적\n- C1 요약\n'), 2, '2번째 결과')
    expect('H4 들여쓴 지적 줄은 절 아님', h3('review_main', '[ownReview:self-reviewer+security-reviewer] 이유', good + '## 비고\n  ## 지적\n'), 2, "'## 지적' 절이 1개")
    expect('H4 소문자 접두어도 검사', h3('review_main', '[ownreview:self-reviewer] 이유', '## 지적\n- C1 요약'), 2, '결과 원문이 아니다')
    expect('H4 대문자 이름 받음', h3('review_main', '[ownReview:Self-Reviewer] 이유', good), 0)
    expect('H4 빈 태그', h3('review_main', '[ownReview:] 이유', good), 2, '모른다')
    # 태그 빠짐 · '- ' 아닌 줄 (security-reviewer 7282089076 C10 · C11)
    expect('H4 review_main 태그 없는 ownReview', h3('review_main', '이유', good), 2, 'reason 맨 앞')
    expect('H4 태그 없는 서브에이전트 원문 (perf_main)', h3('perf_main', '이유', good), 2, '태그가 없다')
    expect('H4 perf_main 태그 없는 지휘자 의견 (설계안)', h3('perf_main', '이유', '설계안 검토: 할당 줄이기'), 0)
    expect('H4 perf_main 태그 없는 지휘자 의견 제목', h3('perf_main', '이유', '## 지휘자 의견\n- 할당 줄이기'), 0)
    expect("H4 perf_main 태그 없는 요약('## 지적'만)", h3('perf_main', '이유', '## 지적\n- C1 요약'), 2, '지휘자 의견')
    expect("H4 crash_main 지휘자 조사의 '## 지적'은 그대로", h3('crash_main', '이유', '## 지적\n- 의심 1'), 0)
    expect("H4 perf_main 태그 없는 '# 지적'(h1)", h3('perf_main', '이유', '# 지적\n- C1 요약'), 2, '지휘자 의견')
    expect("H4 읽은 파일의 번호 줄", h3('review_main', tag, good + '1. docs/worklog.md\n'), 2, "'- '로 시작하지 않는")
    expect('H4 읽은 파일의 맨 경로 줄', h3('review_main', tag, good + 'docs/worklog.md:1-5\n'), 2, "'- '로 시작하지 않는")
    expect('H4 변형 제목 읽은 파일 절도 검사', h3('review_main', tag, good + '## 읽은 파일 (추가)\n- docs/worklog.md:1-5\n'), 2, 'subagent_in/ 밖')
    expect('H4 태그 없는 변형 제목', h3('perf_main', '이유', '## 지적\n- x\n## 읽은 파일 (추가)\n- subagent_in/x/a.cs\n'), 2, '태그가 없다')
    expect('H4 들여쓴 · ### 읽은 파일 절도 검사', h3('review_main', tag, good + '## 비고\n- 없음\n  ### 읽은 파일\n- docs/worklog.md:1-5\n'), 2, 'subagent_in/ 밖')
    # API 리뷰(sub_*) 결과 — 작업 폴더 기준 상대 경로 (2차 리뷰 9ea51e562a 뒤 결정)
    api = good.replace('subagent_in/2026-10-19_x/a.cs', 'Orchestration/src/a.cs')
    api_tag = '[ownReview:sub_self] 이유'
    expect('H4 sub_self 상대 경로', h3('review_main', api_tag, api), 0)
    expect('H4 sub_perf', h3('perf_main', '[ownReview:sub_perf] 이유', api), 0)
    expect('H4 sub_self+sub_security 이어 붙임', h3('review_main', '[ownReview:sub_self+sub_security] 이유', api + '\n' + api), 0)
    expect('H4 sub_self 요약은 막음', h3('review_main', api_tag, '## 지적\n- C1 요약'), 2, "'## 지적'부터")
    expect('H4 sub_self 절대 경로', h3('review_main', api_tag, api.replace('Orchestration/src/a.cs', 'C:\\Users\\x\\review_in\\a.cs')), 2, '상대 경로가 아닌')
    expect('H4 sub_self / 로 시작', h3('review_main', api_tag, api.replace('Orchestration/src/a.cs', '/tmp/a.cs')), 2, '상대 경로가 아닌')
    expect('H4 sub_self .. 로 나감', h3('review_main', api_tag, api.replace('Orchestration/src/a.cs', 'src/../../a.cs')), 2, '상대 경로가 아닌')
    expect('H4 sub_self ~ 경로', h3('review_main', api_tag, api.replace('Orchestration/src/a.cs', '~/a.cs')), 2, '상대 경로가 아닌')
    expect('H4 sub_self 읽은 파일의 머리 줄', h3('review_main', api_tag, api + 'runId: abc\n'), 2, "'- '로 시작하지 않는")
    expect('H4 서브에이전트와 sub_* 섞음', h3('review_main', '[ownReview:self-reviewer+sub_security] 이유', good + '\n' + api), 2, '섞였다')
    expect('H4 sub_direction은 모름', h3('review_main', '[ownReview:sub_direction] 이유', api), 2, '모른다')
    expect('H4 서브에이전트 태그에 상대 경로는 계속 막음', h3('review_main', tag, api), 2, 'subagent_in/ 밖')

    # 나가는 글 (모든 모드) · diff는 파일로 (review_* · sub_self/perf/security · perf_*)
    def rm(mode: str, text: str, context=None) -> tuple[int, str]:
        ti = {'mode': mode, 'input': text, 'reason': '이유'}
        if context is not None:
            ti['context'] = context
        return run('run_mode_guard.py', {'tool_name': 'mcp__hb-orchestration__run_mode', 'tool_input': ti}, env)

    expect('나가는 글 사용자 폴더 경로', rm('design_lite', '로그: C:\\Users\\kim\\AppData\\Local\\HB_Chat\\x.log'), 2, '사용자 폴더 경로')
    expect('나가는 글 Git Bash 사용자 폴더', rm('crash_main', '스택: /c/Users/kim/proj/a.cs:12'), 2, '사용자 폴더 경로')
    expect('나가는 글 비밀 값', rm('research_main', 'key=sk-' + 'Z' * 30), 2, '비밀')
    expect('나가는 글 가짜 값 표식도 막음', rm('design_lite', 'sk-abcdefghijklmnopqrstu'), 2, '비밀')
    expect('나가는 글 작업 경로는 통과', rm('design_lite', '대상 F:\\AI\\ToolWithCS\\Orchestration\\a.cs'), 0)
    expect('나가는 글 환경 변수 꼴은 통과', rm('design_lite', '데이터 폴더 기본값은 %LOCALAPPDATA%\\HB_Chat'), 0)
    expect('나가는 글 @추가 루트는 통과', rm('sub_self', '중심: @orch/src/Foo.cs', [{'path': '@orch/src/Foo.cs', 'startLine': 1, 'endLine': 9}]), 0)
    os.makedirs(os.path.join(tmp, '.claude'), exist_ok=True)
    open(os.path.join(tmp, '.claude', 'share_guard.local.txt'), 'w', encoding='utf-8').write('# 사내 이름\nAcmeInternal\n')
    expect('나가는 글 프로젝트 패턴', rm('design_lite', 'AcmeInternal 서버 구조'), 2, '프로젝트 패턴')
    os.remove(os.path.join(tmp, '.claude', 'share_guard.local.txt'))
    hunk = 'diff --git a/x.cs b/x.cs\n--- a/x.cs\n+++ b/x.cs\n@@ -1,2 +1,3 @@\n+a\n'
    expect('diff 본문 sub_self', rm('sub_self', '의도: x\n' + hunk), 2, 'diff 본문')
    expect('diff 본문 perf_main', rm('perf_main', '@@ -10 +10,2 @@\n+b'), 2, 'diff 본문')
    expect('diff 본문 design_lite는 대상 아님', rm('design_lite', hunk), 0)
    expect('diff 글자를 설명한 줄은 통과', rm('sub_self', '변경은 `diff --git` 파일로 첨부'), 0)
    first = {'path': 'review_tmp/2026-11-07_x.diff', 'startLine': 1, 'endLine': 40}
    other = {'path': 'src/a.cs', 'startLine': 1, 'endLine': 9}
    expect('.diff 첫 항목', rm('sub_self', '의도: x', [first, other]), 0)
    expect('.diff 둘째 항목', rm('sub_self', '의도: x', [other, first]), 2, '첫 항목')
    expect('.diff 없음', rm('sub_security', '의도: x', [other]), 0)
    expect('context가 JSON 글', rm('sub_perf', '의도: x', json.dumps([other, first])), 2, '첫 항목')
finally:
    shutil.rmtree(tmp, ignore_errors=True)

# ── H5 share_guard ────
tmp = tempfile.mkdtemp(prefix='hb-shareguard-')
try:
    proj = os.path.join(tmp, 'proj')  # CLAUDE_PROJECT_DIR · 셸 현재 폴더
    bot = os.path.join(tmp, 'SlackPlanBot')  # SLACKPLAN_ROOT
    os.makedirs(os.path.join(proj, '.claude'))
    os.makedirs(os.path.join(proj, 'docs', 'plans'))
    os.makedirs(os.path.join(bot, 'threads', 'C1-2'))
    with open(os.path.join(proj, '.claude', 'push_guard.local.txt'), 'w', encoding='utf-8') as f:
        f.write('person@example\\.com\n')
    with open(os.path.join(proj, '.claude', 'share_guard.local.txt'), 'w', encoding='utf-8') as f:
        f.write('(?i)projectx\nallow: ^F:/ok/\n')
    env = dict(os.environ, CLAUDE_PROJECT_DIR=proj, SLACKPLAN_ROOT=bot)
    SECRET = 'sk-' + 'Q7vR2mK9pL4xN8wT3bZ6'

    def plan(name: str, text: str) -> str:
        with open(os.path.join(proj, 'docs', 'plans', name), 'w', encoding='utf-8') as f:
            f.write(text)
        return f'docs/plans/{name}'

    def h5(args: str, tool: str = 'Bash', cwd: str = proj, extra: dict | None = None) -> tuple[int, str]:
        root = '$env:SLACKPLAN_ROOT' if tool == 'PowerShell' else '$SLACKPLAN_ROOT'
        cmd = f'dotnet run --no-build --project "{root}/src/SlackPlanBot" -- {args}'
        return run('share_guard.py', {'tool_name': tool, 'tool_input': {'command': cmd}, 'cwd': cwd}, dict(env, **(extra or {})))

    def h5raw(cmd: str, tool: str = 'Bash', cwd: str = proj) -> tuple[int, str]:
        return run('share_guard.py', {'tool_name': tool, 'tool_input': {'command': cmd}, 'cwd': cwd}, env)

    clean = plan('clean.md', '# 계획\n- 내용\n')
    expect('H5 깨끗한 계획서 new', h5(f'new {clean} --title "제목"'), 0)
    expect('H5 깨끗한 계획서 new (PowerShell)', h5(f'new {clean}', 'PowerShell'), 0)
    expect('H5 SLACKPLAN_ROOT 경로의 계획서', h5('revise C1-2 "$SLACKPLAN_ROOT/threads/C1-2/../../../proj/' + clean + '"'), 0)
    expect('H5 읽기 명령 find', h5(f'find {clean}'), 0)
    expect('H5 읽기 명령 thread', h5('thread C1-2 --reader 지휘자'), 0)
    expect('H5 읽기 명령 status', h5('status C1-2'), 0)
    expect('H5 slackplan 직접 호출', h5raw(f'slackplan new {clean}'), 0)
    expect('H5 따옴표 안 글 (커밋 메시지 -m은 막힘, 2026-10-23 결정)', h5raw('git commit -m "slackplan new 형식 정리"'), 2)
    expect('H5 grep 인자', h5raw('grep -rn slackplan README.md'), 0)
    expect('H5 dotnet test (run 아님)', h5raw('dotnet test F:/AI/SlackPlanBot/tests'), 0)
    expect('H5 heredoc 본문', h5raw("git commit -F - <<'EOF'\nslackplan new x.md --allow-warnings\nEOF"), 0)

    expect('H5 --allow-warnings', h5(f'new {clean} --allow-warnings'), 2, '--allow-warnings')
    expect('H5 --allow-warnings + --dry-run도 막음', h5(f'new {clean} --dry-run --allow-warnings'), 2, '--allow-warnings')
    bad = plan('secret.md', f'키 {SECRET}\n')
    expect('H5 비밀 모양 (값은 가림)', h5(f'new {bad}'), 2, 'sk-Q…')
    expect('H5 비밀 모양이어도 --dry-run은 통과', h5(f'new {bad} --dry-run'), 0)
    fake = plan('fake.md', 'InlineData("sk-abcdefghijklmnopqrstu")\n')
    expect('H5 가짜 값 표식도 막음 (밖으로 나감)', h5(f'new {fake}'), 2, '비밀')
    expect('H5 개인 값 (push_guard.local)', h5(f'new {plan("p.md", "person@example.com")}'), 2, '비밀 · 개인 값')
    expect('H5 프로젝트 패턴 (share_guard.local)', h5(f'new {plan("x.md", "ProjectX 출시")}'), 2, '프로젝트 패턴')
    expect('H5 GoldenSet 언급', h5(f'new {plan("g.md", "GoldenSet/case1 참고")}'), 2, 'GoldenSet/')
    img = plan('i.md', 'docs\\image\\a.png')
    expect('H5 docs/image 언급 (역슬래시)', h5(f'new {img}'), 2, 'docs/image/')
    expect('H5 드라이브 문자 경로', h5(f'new {plan("d.md", "F:/AI/ToolWithCS 에서")}'), 2, '드라이브')
    expect('H5 allow: 예외', h5(f'new {plan("ok.md", "F:/ok/a 에서")}'), 0)
    expect('H5 Git Bash 경로', h5(f'new {plan("b.md", "cd /f/AI/x")}'), 2, 'Git Bash')
    unc = plan('u.md', '\\\\srv\\share')
    expect('H5 UNC 경로', h5(f'new {unc}'), 2, 'UNC')
    expect('H5 URL은 통과', h5(f'new {plan("url.md", "https://docs.unity.com/a/b")}'), 0)
    expect('H5 --title에 비밀', h5(f'new {clean} --title "{SECRET}"'), 2, '--title')
    expect('H5 reply 글', h5(f'reply C1-2 "경로 C:/Users/me"'), 2, 'reply 글')
    expect('H5 ask-consent --text', h5(f'ask-consent C1-2 --text "{SECRET}"'), 2, '--text')
    expect('H5 파일 없음', h5('new docs/plans/none.md'), 2, '읽지 못했다')
    expect('H5 풀 수 없는 변수', h5('new "$HOME/x.md"'), 2, '변수')
    expect('H5 SLACKPLAN_ROOT 없음', h5('new "$SLACKPLAN_ROOT/x.md"', extra={'SLACKPLAN_ROOT': ''}), 2, 'SLACKPLAN_ROOT')
    expect('H5 모르는 옵션', h5(f'new {clean} --file x'), 2, '모른다')
    expect('H5 revise 인자 수', h5(f'revise {clean}'), 2, '인자 수')
    expect('H5 --text 없는 update-summary', h5('update-summary C1-2'), 2, '--text')
    expect('H5 update-summary --text', h5('update-summary C1-2 --text "깨끗한 요약"'), 0)
    # /cross-review 32ab98a8a9 반영
    expect('H5 reply 글의 셸 변수', h5('reply C1-2 "$OPENAI_API_KEY"'), 2, '셸 변수')
    expect('H5 --title의 %변수%', h5(f'new {clean} --title "%USERPROFILE%"'), 2, '셸 변수')
    expect('H5 --text의 PowerShell 변수', h5('ask-consent C1-2 --text "$env:SECRET_X"', 'PowerShell'), 2, '셸 변수')
    expect('H5 따옴표로 감싼 확장자 없는 실행 파일', h5raw(f'"$SLACKPLAN_ROOT/bin/slackplan" new {clean} --allow-warnings'), 2, '--allow-warnings')
    expect("H5 PowerShell & 'slackplan'", h5raw(f"& 'slackplan' new {bad}", 'PowerShell'), 2, '비밀')
    expect('H5 dotnet <dll>', h5raw(f'dotnet "$SLACKPLAN_ROOT/bin/slackplan.dll" new {bad}'), 2, '비밀')
    expect('H5 dotnet exec <dll>', h5raw(f'dotnet exec bin/slackplan.dll new {bad}'), 2, '비밀')
    expect('H5 커밋 메시지 속 slackplan.exe (-m은 막힘, 2026-10-23 결정)', h5raw('git commit -m "bin/Debug의 slackplan.exe 잠금"'), 2)
    expect('H5 파일 이름 (제목 기본값)', h5(f'new {plan("ProjectX-출시.md", "깨끗")}'), 2, '파일 이름')
    os.makedirs(os.path.join(proj, 'GoldenSet'))
    with open(os.path.join(proj, 'GoldenSet', 'a.md'), 'w', encoding='utf-8') as f:
        f.write('깨끗\n')
    expect('H5 무시 폴더 아래 계획서', h5('new GoldenSet/a.md'), 2, 'GoldenSet/')
    expect('H5 PowerShell here-string 인자', h5raw("slackplan reply C1-2 @'\nProjectX\n'@", 'PowerShell'), 2, 'here-string')
    expect('H5 모르는 하위 명령', h5(f'publish {clean}'), 2, '모른다')
    expect('H5 react (이모지만)', h5('react C1-2 eyes'), 0)
    expect('H5 list', h5('list'), 0)
    expect('H5 approval-record', h5(f'approval-record C1-2 --plan {clean}'), 0)
    expect('H5 status --link', h5('status C1-2 --link'), 0)
    expect('H5 unlink 미리보기', h5('unlink C1-2'), 0)
    expect('H5 purge 미리보기', h5('purge --days 30'), 0)
    expect('H5 unlink --yes', h5('unlink C1-2 --yes'), 2, 'unlink --yes')
    expect('H5 purge --yes', h5('purge --days 30 --yes'), 2, 'purge --yes')
    expect('H5 purge --yes (PowerShell 실행 파일)', h5raw(f'& "{bot}\\bin\\slackplan.exe" purge --yes', 'PowerShell'), 2, 'purge --yes')
    expect('H5 unlink 실제 스레드 키 모양', h5('unlink C0AB12CD_1792741463.590179'), 0)
    # /self-review 8c1412729e C1: 셸이 펼쳐 --yes가 되는 인자
    expect("H5 purge $'--yes'", h5raw("slackplan purge --days 30 $'--yes'"), 2, '셸이 펼칠')
    expect('H5 unlink {--yes,}', h5raw('slackplan unlink C1-2 {--yes,}'), 2, '셸이 펼칠')
    expect('H5 purge ${V:---yes}', h5raw('slackplan purge --days 30 ${V:---yes}'), 2, '셸이 펼칠')
    # tokenize가 PowerShell 백틱 이스케이프를 셸처럼 풀어 --yes로 본다 (/self-review security C6 — 이전에는 '셸이 펼칠'로 막힘)
    expect('H5 unlink PS 백틱', h5raw('& slackplan.exe unlink C1-2 --`yes', 'PowerShell'), 2, 'unlink --yes')
    expect("H5 purge PS 괄호 식", h5raw("& slackplan.exe purge --days 30 ('--yes')", 'PowerShell'), 2, '셸이 펼칠')
    expect("H5 따옴표로 감싼 '--yes'", h5raw("slackplan unlink C1-2 '--yes'"), 2, 'unlink --yes')
    expect('H5 inbox --note (로컬 작업 로그)', h5('inbox done C1-3 --note "처리함"'), 0)
    expect('H5 reply의 --dry-run은 통과 아님 (봇이 글로 올림)', h5(f'reply C1-2 "{SECRET}" --dry-run'), 2, "'--dry-run'")
    expect('H5 옵션 값 자리의 --dry-run (봇도 dry-run)', h5(f'new {bad} --title --dry-run'), 0)
    expect('H5 리다이렉트 2>&1', h5(f'new {clean} 2>&1'), 2, '리다이렉트')
    expect('H5 new --thread · --item', h5(f'new {clean} --thread C1-2 --item C1-3'), 0)
    expect('H5 --thread 값이 ID가 아님', h5(f'new {clean} --thread "a b"'), 2, 'ID 모양')
    expect('H5 슬래시 UNC', h5(f'new {plan("u2.md", "//srv/share/x")}'), 2, 'UNC')
    expect('H5 POSIX 절대 경로', h5(f'new {plan("t.md", "로그 /tmp/x.log")}'), 2, 'POSIX')
    expect('H5 URL 속 /var/ (오탐 아님)', h5(f'new {plan("url2.md", "https://example.org/var/x")}'), 0)
    # 봇 secret_scan 시험과 같은 비교 목록 (봇 test_secret_scan.ShareCheck SHARED_HIT/SHARED_PASS)
    for i, s in enumerate(['sk-' + 'Q7vR2mK9pL4xN8wT3bZ6', '-----BEGIN RSA ' + 'PRIVATE KEY-----', 'F:/AI/x', 'C:\\Users\\me', '\\\\srv\\share', '/Users/me/x', '/f/AI/x']):
        expect(f'H5 비교 목록 걸림 {i}', h5(f'new {plan(f"hit{i}.md", s)}'), 2, '막는 패턴')
    for i, s in enumerate(['https://example.org/var/x', 'e.g. 1:2', 'and/or', 'A/B', '1/2/3', 'a/b/c']):
        expect(f'H5 비교 목록 안 걸림 {s}', h5(f'new {plan(f"pass{i}.md", s)}'), 0)
    expect('H5 묶음', h5raw(f'cd {proj} && dotnet run --project "$SLACKPLAN_ROOT/src/SlackPlanBot" -- new {clean}'), 2)
    expect('H5 명령 치환', h5raw('dotnet run --project "$SLACKPLAN_ROOT/src/SlackPlanBot" -- new "$(echo x.md)"'), 2, '명령 치환')
    expect('H5 -- 없음', h5raw(f'dotnet run --project "$SLACKPLAN_ROOT/src/SlackPlanBot" new {clean}'), 2, '`--`')
    bad_abs = os.path.join(proj, bad).replace('\\', '/')
    expect('H5 셸 현재 폴더가 SlackPlanBot', h5raw(f'dotnet run -- new {bad_abs}', cwd=bot), 2, '비밀')
    expect('H5 PowerShell 실행 파일 경로', h5raw(f'& "{bot}\\bin\\slackplan.exe" new {bad}', 'PowerShell'), 2, '비밀')
    expect('H5 cmd /c 형식', h5raw(f'cmd /c slackplan new {clean}'), 2, '해석하지 못했다')
    # 앞 낱말 · 따옴표 · 줄 이음으로 감싸면 이전에는 검사 없이 통과 (/self-review e256053430 — 실측 exit 0)
    unparsed, chained = '해석하지 못했다', '다른 명령과 묶여'
    for pre in ['env', 'X=1', 'env X=1', 'command', 'exec', 'nice', 'time', 'sudo', 'nohup', 'eval']:
        expect(f'H5 앞 낱말 {pre} purge --yes', h5raw(f'{pre} slackplan purge --yes'), 2, unparsed)
        expect(f'H5 앞 낱말 {pre} new', h5raw(f'{pre} slackplan new {clean}'), 2, unparsed)
    for name, cmd, why in [
        ('따옴표 경로', 'env "$SLACKPLAN_ROOT/bin/slackplan" purge --yes', unparsed),
        ('공백 든 따옴표 경로', 'env "C:/Program Files/x/slackplan.exe" new secret.md', unparsed),
        ('따옴표 하위 명령', "env slackplan 'purge' --yes", unparsed),
        ('쪼갠 하위 명령', "nohup slackplan pur''ge --yes", unparsed),
        ("$'…' 하위 명령", "env slackplan $'purge' --yes", unparsed),
        ('이스케이프 하위 명령', 'env slackplan p\\urge --yes', unparsed),
        ('쪼갠 실행 파일 이름', "env slack''plan purge --yes", unparsed),
        ('전역 옵션 뒤 하위 명령', 'env slackplan --verbose purge --yes', unparsed),
        ('env dotnet …dll', 'env dotnet "$SLACKPLAN_ROOT/bin/slackplan.dll" purge --yes', unparsed),
        ('eval 따옴표', 'eval "slackplan purge --yes"', unparsed),
        ('env -S', "env -S 'slackplan purge --yes'", unparsed),
        ('xargs -I', 'xargs -I{} slackplan {} --yes', unparsed),
        ('stdin xargs', 'echo purge --yes | xargs slackplan', chained),
        ('xargs 하위 명령', 'echo a | xargs slackplan purge --yes', chained),
        ('줄 이음', 'env slackplan \\\npurge --yes', chained),
        ('검색 뒤 실행', 'grep x f | xargs slackplan purge', chained),
        ('git alias', "git -c alias.x='!slackplan purge --yes' x", unparsed),
        ('echo 명령 치환', 'echo $(slackplan purge --yes)', '명령 치환'),
    ]:
        expect(f'H5 우회 {name}', h5raw(cmd), 2, why)
    expect('H5 앞 낱말 env + .exe 경로', h5raw(f'env {bot}/bin/slackplan.exe unlink C1-2 --yes'), 2, unparsed)
    expect('H5 PowerShell 점 연산자', h5raw('. slackplan.exe purge --yes', 'PowerShell'), 2, unparsed)
    expect('H5 PowerShell 점 연산자 따옴표', h5raw(". 'slackplan.exe' purge --yes", 'PowerShell'), 2, unparsed)
    expect('H5 PowerShell 점 연산자 (읽기도 막음)', h5raw('. slackplan list', 'PowerShell'), 2, unparsed)
    expect('H5 PowerShell 하위 식', h5raw('Get-Content (slackplan purge --yes)', 'PowerShell'), 2)
    # 2회차 (/self-review 3efd4d0922, 실측 exit 0이던 형식)
    for name, cmd, tool in [
        ('공백 없는 &', 'echo x&env slackplan purge --yes', 'Bash'),
        ("$'' 쪼개기", "env slack$''plan purge --yes", 'Bash'),
        ('$"" 쪼개기', 'env slack$""plan purge --yes', 'Bash'),
        ('스크립트 블록', 'gci x | Get-Content -Path {slackplan purge --yes}', 'PowerShell'),
        ('해시 테이블', 'echo @{a=slackplan purge --yes}', 'PowerShell'),
        ('git grep -O', "git grep -O'slackplan purge --yes #' -e .", 'Bash'),
        ('rg --pre', 'rg --pre slackplan x purge', 'Bash'),
        ('heredoc을 bash에', "bash <<'EOF'\nslackplan purge --yes\nEOF", 'Bash'),
        ('here-string을 iex에', "@'\nslackplan purge --yes\n'@ | iex", 'PowerShell'),
        ('리다이렉트 + git hook', 'echo slackplan purge --yes > .git/hooks/post-commit; git commit -m x', 'Bash'),
        ('백슬래시 경로 (PowerShell)', '. C:\\bot\\bin\\slackplan.exe purge --yes', 'PowerShell'),
        ('백슬래시 경로 (Bash)', 'env C:\\\\bot\\\\slackplan.exe purge --yes', 'Bash'),
    ]:
        expect(f'H5 우회 {name}', h5raw(cmd, tool), 2, '[공유 차단]')
    # 단독 읽기 명령의 인자로만 나오면 통과
    for cmd in ['grep -rn "slackplan purge" docs', 'grep slackplan purge.txt', 'grep -rn slackplan files/', 'rg slackplan list',
                'git log --grep slackplan']:
        expect(f'H5 읽기 명령 {cmd}', h5raw(cmd), 0)
    expect('H5 읽기 명령 (PowerShell)', h5raw('Select-String -Path README.md -Pattern slackplan', 'PowerShell'), 0)
    expect('H5 heredoc 본문을 git에 (셸 아님)', h5raw("git commit -F - <<'EOF'\nslackplan purge --yes 정리\nEOF"), 0)
    # 따옴표 짝 · 주석 · heredoc 경계 + 본문은 허용 목록 (security-reviewer 7282089076 — 이전 판 실측 exit 0)
    for name, cmd, tool in [
        ("C2 $'\\''로 묶음 숨김", "slackplan status $'\\'' ; slackplan purge X --yes ; 'z'", 'Bash'),
        ("C2 읽기 예외로 묶음 숨김", "grep $'\\'' ; slackplan purge X --yes ; 'z' f", 'Bash'),
        ('C3 따옴표 없는 heredoc 속 $()', 'cat <<EOF\n$(slackplan purge X --yes)\nEOF', 'Bash'),
        ('C3 PS @"…"@ 속 $()', '$m = @"\n$(slackplan purge X --yes)\n"@', 'PowerShell'),
        ('C4 따옴표 안 <<EOF', 'echo "<<EOF"\nslackplan purge X --yes\nEOF', 'Bash'),
        ("C4 따옴표 안 <<'EOF'", "echo \"<<'EOF'\"\nslackplan purge X --yes\nEOF", 'Bash'),
        ("C4 PS 따옴표 안 @'", "echo \"@'\nslackplan purge X --yes\n'@\"", 'PowerShell'),
        ('C7 heredoc을 tclsh', "tclsh <<'EOF'\nslackplan purge --yes\nEOF", 'Bash'),
        ('C7 heredoc을 perl', "perl <<'EOF'\nslackplan purge --yes\nEOF", 'Bash'),
        ('C7 heredoc을 python 스크립트', "python -I x.py <<'EOF'\nslackplan purge --yes\nEOF", 'Bash'),
        ('C7 대입 뒤 Invoke-Expression', "$m = @'\nslackplan purge --yes\n'@\nInvoke-Expression $m", 'PowerShell'),
        ('주석 속 따옴표', "grep x f # '\nslackplan purge X --yes\n'", 'Bash'),
        ('PS 주석 속 따옴표', "Select-String x f # '\nslackplan purge X --yes\n'", 'PowerShell'),
        ('C1 PS 굽은 따옴표', "sls 'a’; slackplan purge X --yes ; ‘' f", 'PowerShell'),
        ('C6 ~ 펼침', 'slackplan reply C1-2 로그는 ~/x 참고', 'Bash'),
        ('C6 글롭', 'slackplan reply C1-2 파일 *.md', 'Bash'),
        ('C6 따옴표 밖 물음표', 'slackplan reply C1-2 할까요?', 'Bash'),
        ('중괄호 펼침 {~,x}', 'slackplan reply C1-2 {~,x}/a', 'Bash'),
        # /cross-review 8722c947f7 반영
        ('PS <# #> 뒤 reply 글', 'slackplan reply C1-2 a <# c #> C:/Users/me/secret', 'PowerShell'),
        ('PS 굽은 따옴표로 쪼갠 이름', '& { sl\u2018\u2019ackplan purge X --yes }', 'PowerShell'),
        ('경로 붙은 cat', "./cat <<'EOF'\nslackplan purge --yes\nEOF", 'Bash'),
        ('<<<의 둘째 <', 'grep x <<<true\nslackplan purge X --yes\ntrue', 'Bash'),
        ('이스케이프 공백 뒤 # (읽기 예외)', 'grep \\ #; slackplan purge X --yes', 'Bash'),
        ('본문의 dotnet run SlackPlanBot', "bash <<'EOF'\ndotnet run --project \"$SLACKPLAN_ROOT/src/SlackPlanBot\" -- purge --yes\nEOF", 'Bash'),
        ('cat &bash heredoc', "cat &bash <<'EOF'\nslackplan purge --yes\nEOF", 'Bash'),
        ('PS 백틱으로 숨긴 --allow-warnings', 'slackplan ask-consent C1-2 `"x --allow-warnings', 'PowerShell'),
    ]:
        expect(f'H5 셸 문법 {name}', h5raw(cmd, tool), 2, '[공유 차단]')
    for name, cmd, tool in [
        ('커밋 -m "$(cat heredoc)"', "git commit -m \"$(cat <<'EOF'\nslackplan 정리 don't (x)\nEOF\n)\"", 'Bash'),
        ('커밋 -F - 따옴표 든 본문', "git commit -F - <<'EOF'\nslackplan don't \"x\" $(y)\nEOF", 'Bash'),
        ('cat > 파일', "cat > notes.md <<'EOF'\nslackplan new\nEOF", 'Bash'),
        ('PS 대입', "$m = @'\nslackplan new\n'@", 'PowerShell'),
        ('PS here-string | Set-Content', "@'\nslackplan new\n'@ | Set-Content x.md", 'PowerShell'),
        ('reply 따옴표 안 물음표', 'slackplan reply C1-2 "할까요?"', 'Bash'),
        ('주석 속 낱말 (읽기)', 'grep -n x f # slackplan 메모', 'Bash'),
    ]:
        expect(f'H5 셸 문법 통과 {name}', h5raw(cmd, tool), 0)
    # 읽기 예외를 좁혀 막히는 것 (단독 읽기 명령만, git commit 속 낱말도 막힘 — 사용자 "git 명령어는 크게 신경 안써도되")
    for cmd in ['cat notes.md | grep slackplan', 'echo slackplan list', 'ls bin/slackplan.exe', 'git commit -m "slackplan new 형식"']:
        expect(f'H5 좁힌 예외 {cmd}', h5raw(cmd), 2)
    # 2026-11-07 오탐 완화: cd · mkdir 조각은 데이터 전용 판정에서 중립 (작업 로그 heredoc 속 SlackPlanBot · slackplan 글자)
    expect('H5 완화 cd 뒤 작업 로그 heredoc', h5raw("cd /tmp && cat >> docs/log.md <<'EOF'\n- SlackPlanBot 고침, slackplan new 로 올림\nEOF"), 0)
    expect('H5 완화 mkdir 뒤 heredoc', h5raw("mkdir -p /tmp/d && cat > /tmp/d/a.md <<'EOF'\nslackplan reply X 글\nEOF"), 0)
    expect('H5 완화 PS Set-Location 뒤 here-string', h5raw("Set-Location C:\\t; @'\nslackplan new a.md\n'@ | Add-Content log.md", 'PowerShell'), 0)
    expect('H5 cd 뒤 bash heredoc은 계속 막음', h5raw("cd /tmp && bash <<'EOF'\nslackplan new a.md\nEOF"), 2, '데이터 전용')
    expect('H5 cd 뒤 heredoc + echo는 계속 막음', h5raw("cd /tmp && cat >> log.md <<'EOF'\nslackplan new a.md\nEOF\necho done"), 2, '데이터 전용')
    expect('H5 셸 감싸개 속 SlackPlanBot은 계속 대상', h5raw('powershell -Command "foreach ($t in \'F:\\AI\\SlackPlanBot\') { Write-Host $t }"'), 2)
    subprocess.run(['git', 'init', '-q', proj], check=True, capture_output=True)
    subprocess.run(['git', '-C', proj, 'add', '-f', '.claude/share_guard.local.txt'], check=True, capture_output=True)
    expect('H5 share_guard.local.txt 추적됨', h5(f'new {clean}'), 2, '추적')
    expect('H5 깨진 입력은 막음', (lambda r: (r.returncode, r.stderr.decode('utf-8', 'replace')))(subprocess.run([sys.executable, '-I', os.path.join(HERE, 'share_guard.py')], input=b'{', capture_output=True)), 2)
finally:
    shutil.rmtree(tmp, ignore_errors=True)

# ── H6 subagent_input_guard ──
proj6 = tempfile.mkdtemp(prefix='h6-')
try:
    env6 = dict(os.environ, CLAUDE_PROJECT_DIR=proj6)
    run_dir = os.path.join(proj6, 'subagent_in', '2026-10-27_x')
    os.makedirs(os.path.join(run_dir, 'src', 'deep'))
    open(os.path.join(run_dir, 'src', 'a.cs'), 'w').close()

    # CLAUDE.md 검사는 서브에이전트 종류와 상관없다 — 입력 준비물 검사(검토형)는 아래 따로
    def h6(prompt, tool: str = 'Agent', agent: str = 'general-purpose') -> tuple[int, str]:
        return run('subagent_input_guard.py', {'tool_name': tool, 'tool_input': {'subagent_type': agent, 'prompt': prompt}}, env6)

    def put(rel: str, is_dir: bool = False) -> str:
        p = os.path.join(proj6, rel)
        os.makedirs(p) if is_dir else open(p, 'w').close()
        return p

    win = f'폴더: {run_dir}\\ (이 폴더 안만 읽는다)\n대상: src/a.cs'
    rel = '폴더: `subagent_in/2026-10-27_x/`\n대상: src/a.cs:1-10'
    expect('H6 깨끗한 폴더 (Windows 경로)', h6(win), 0)
    expect('H6 깨끗한 폴더 (상대 경로)', h6(rel), 0)
    expect('H6 subagent_in 언급 없음', h6('웹에서 X를 찾아 요약'), 0)
    expect('H6 자리표시 <실행>만', h6('`subagent_in/<실행>/` 밖은 읽지 않는다'), 0)
    expect('H6 없는 실행 폴더', h6('폴더: subagent_in/없는폴더/'), 0)
    expect('H6 이름 바꾼 CLAUDE.md.txt', (put('subagent_in/2026-10-27_x/CLAUDE.md.txt'), h6(win))[1], 0)
    for name, rel_path, is_dir in [('CLAUDE.md', 'subagent_in/2026-10-27_x/CLAUDE.md', False),
                                   ('소문자 claude.md', 'subagent_in/2026-10-27_x/src/claude.md', False),
                                   ('깊은 곳', 'subagent_in/2026-10-27_x/src/deep/CLAUDE.md', False),
                                   ('CLAUDE.local.md', 'subagent_in/2026-10-27_x/CLAUDE.local.md', False),
                                   ('.claude 폴더', 'subagent_in/2026-10-27_x/.claude', True),
                                   ('subagent_in 바로 아래', 'subagent_in/CLAUDE.md', False)]:
        p = put(rel_path, is_dir)
        expect(f'H6 막음 {name} (Windows 경로)', h6(win), 2, '[입력 차단]')
        expect(f'H6 막음 {name} (상대 경로)', h6(rel), 2, 'CLAUDE.md.txt' if 'subagent_in/2026' in rel_path else '[입력 차단]')
        os.rmdir(p) if is_dir else os.remove(p)
    if os.name == 'nt':
        put('subagent_in/2026-10-27_x/CLAUDE.md')
        gb = '/' + proj6[0].lower() + proj6[2:].replace('\\', '/')
        expect('H6 Git Bash 경로', h6(f'폴더: {gb}/subagent_in/2026-10-27_x/'), 2, '[입력 차단]')
        os.remove(os.path.join(run_dir, 'CLAUDE.md'))
    # /self-review security 78e1a3aaca C1 · C2: 경로 해석이 빗나가도 subagent_in/ 전체를 검사한다
    p = put('subagent_in/2026-10-27_x/CLAUDE.md')
    for name, prompt in [('붙여 쓴 콜론', '폴더:subagent_in/2026-10-27_x/'), ('path= 꼴', 'path=subagent_in/2026-10-27_x'),
                         ('대소문자', f'폴더: {proj6}\\Subagent_In\\2026-10-27_x\\'), ('한국어 조사', 'subagent_in/2026-10-27_x에서 읽는다'),
                         ('자리표시만', '`subagent_in/<실행>/` 안만 읽는다'), ('띄어 쓴 설명', 'subagent_in 폴더의 2026-10-27_x')]:
        expect(f'H6 해석 빗나가도 막음 {name}', h6(prompt), 2, '[입력 차단]')
    os.remove(p)
    other = put('subagent_in/2026-10-26_old', True)
    put('subagent_in/2026-10-26_old/CLAUDE.md')
    expect('H6 다른 실행 폴더의 CLAUDE.md도 막음', h6(win), 2, '2026-10-26_old')
    shutil.rmtree(other)
    expect('H6 SendMessage 이어 부르기 (깨끗)', run('subagent_input_guard.py', {'tool_name': 'SendMessage', 'tool_input': {'to': 'a1', 'message': rel}}, env6), 0)
    p = put('subagent_in/2026-10-27_x/CLAUDE.md')
    expect('H6 SendMessage 이어 부르기 (CLAUDE.md)', run('subagent_input_guard.py', {'tool_name': 'SendMessage', 'tool_input': {'to': 'a1', 'message': rel}}, env6), 2, '[입력 차단]')
    expect('H6 SendMessage 언급 없음', run('subagent_input_guard.py', {'tool_name': 'SendMessage', 'tool_input': {'to': 'peer', 'message': '테스트 끝났어'}}, env6), 0)
    os.remove(p)
    # C3: 폴더 링크 · 정션은 안을 볼 수 없으니 막는다 (Python 3.10 os.walk는 정션 안으로 들어감)
    outside = tempfile.mkdtemp(prefix='h6-out-')
    open(os.path.join(outside, 'CLAUDE.md'), 'w').close()
    link = os.path.join(run_dir, 'linked')
    made = False
    if os.name == 'nt':
        made = subprocess.run(['cmd', '/c', 'mklink', '/J', link, outside], capture_output=True).returncode == 0
    else:
        os.symlink(outside, link, target_is_directory=True)
        made = True
    if made:
        expect('H6 폴더 링크 · 정션 막음', h6(win), 2, '링크')
        os.rmdir(link) if os.name == 'nt' else os.remove(link)
    else:
        expect('H6 정션 만들기 (시험 준비)', (1, 'mklink /J 실패'), 0)
    shutil.rmtree(outside, ignore_errors=True)
    expect('H6 Task 이름', h6(rel, tool='Task'), 0)
    expect('H6 .. 로 밖', h6('폴더: subagent_in/../docs/'), 2, '밖이다')
    expect('H6 다른 프로젝트의 subagent_in', h6('폴더: F:\\Other\\subagent_in\\x\\' if os.name == 'nt' else '폴더: /other/subagent_in/x/'), 2, '밖이다')
    expect('H6 비슷한 낱말은 경로 아님', h6('not_subagent_in/x 와 subagent_inbox'), 0)
    # 문자열이 아닌 prompt는 JSON 글로 바꿔 언급을 찾는다 (형식이 바뀌어도 놓치지 않게)
    expect('H6 prompt가 문자열 아님 (언급 없음)', run('subagent_input_guard.py', {'tool_name': 'Agent', 'tool_input': {'prompt': ['x']}}, env6), 0)
    p = put('subagent_in/2026-10-27_x/CLAUDE.md')
    expect('H6 prompt가 문자열 아님 (언급 있음)', run('subagent_input_guard.py', {'tool_name': 'Agent', 'tool_input': {'prompt': {'folder': 'subagent_in/2026-10-27_x'}}}, env6), 2, '[입력 차단]')
    os.remove(p)
    expect('H6 깨진 입력은 막음', (lambda r: (r.returncode, r.stderr.decode('utf-8', 'replace')))(subprocess.run([sys.executable, '-I', os.path.join(HERE, 'subagent_input_guard.py')], input=b'{', capture_output=True, env=env6)), 2)
    # 입력 준비물: 리뷰 지침 · 실수 목록 사본
    expect('H6 self-reviewer 지침 없음', h6(rel, agent='self-reviewer'), 2, 'REVIEW_GUIDELINES.md')
    expect('H6 perf-reviewer 지침 없음 (Windows 경로)', h6(win, agent='perf-reviewer'), 2, 'REVIEW_GUIDELINES.md')
    expect('H6 self-reviewer 폴더 언급 없음', h6('대상: src/a.cs', agent='self-reviewer'), 2, '폴더가 없다')
    expect('H6 self-reviewer 없는 폴더', h6('폴더: subagent_in/없는폴더/', agent='self-reviewer'), 2, '폴더가 없다')
    put('subagent_in/2026-10-27_x/REVIEW_GUIDELINES.md')
    expect('H6 self-reviewer 지침 있음', h6(rel, agent='self-reviewer'), 0)
    expect('H6 SendMessage는 준비물 안 봄', run('subagent_input_guard.py', {'tool_name': 'SendMessage', 'tool_input': {'to': 'a1', 'message': '대상: x'}}, env6), 0)
    os.makedirs(os.path.join(proj6, '.claude', 'mistakes'))
    mk = os.path.join(proj6, '.claude', 'mistakes', 'self-reviewer.md')
    open(mk, 'w', encoding='utf-8').write('# self-reviewer 실수\n<!--\n- 주석 안 예시\n-->\n')
    expect('H6 실수 목록이 주석뿐', h6(rel, agent='self-reviewer'), 0)
    open(mk, 'a', encoding='utf-8').write('- 하지 말 것 → 대신 할 것 (2026-11-07, 시험)\n')
    expect('H6 실수 목록 사본 없음', h6(rel, agent='self-reviewer'), 2, '_SUBAGENT_MISTAKES.md')
    put('subagent_in/2026-10-27_x/_SUBAGENT_MISTAKES.md')
    expect('H6 실수 목록 사본 있음', h6(rel, agent='self-reviewer'), 0)
    open(os.path.join(proj6, '.claude', 'mistakes', 'direction-reviewer.md'), 'w', encoding='utf-8').write('- 항목 (2026-11-07, 시험)\n')
    os.makedirs(os.path.join(proj6, 'subagent_in', '2026-11-07_d'))
    expect('H6 direction 실수 사본 없음 (지침은 안 봄)', h6('폴더: subagent_in/2026-11-07_d/', agent='direction-reviewer'), 2, '_SUBAGENT_MISTAKES.md')
    expect('H6 direction 폴더 언급 없음은 통과', h6('요청 원문과 계획서', agent='direction-reviewer'), 0)
    # web-searcher 위임 메시지: 비밀 · 사용자 폴더 경로
    expect('H6 web-searcher 사용자 폴더 경로', h6('C:\\Users\\kim\\AppData\\x 크래시 원인 검색', agent='web-searcher'), 2, '사용자 폴더 경로')
    expect('H6 web-searcher 비밀 값', h6('키 sk-' + 'Q' * 24 + ' 로 된 오류', agent='web-searcher'), 2, '비밀')
    expect('H6 web-searcher 보통 검색', h6('Unity 6 IL2CPP NullReferenceException in Addressables 사례, 범위: 2025~', agent='web-searcher'), 0)
    expect('H6 web-searcher 환경 변수 꼴 경로', h6('%LOCALAPPDATA%\\Unity 로그 위치 문서', agent='web-searcher'), 0)
    expect('H6 web-searcher WSL 사용자 폴더', h6('/mnt/c/Users/kim/x.log 오류', agent='web-searcher'), 2, '사용자 폴더 경로')
    expect('H6 web-searcher UNC 사용자 폴더', h6('\\\\localhost\\c$\\Users\\kim\\x.log 오류', agent='web-searcher'), 2, '사용자 폴더 경로')
    expect('H6 SendMessage 사용자 폴더 경로', run('subagent_input_guard.py', {'tool_name': 'SendMessage', 'tool_input': {'to': 'a1', 'message': 'C:\\Users\\kim\\AppData\\x 로 다시 찾아봐'}}, env6), 2, 'SendMessage')
    expect('H6 SendMessage 보통 글', run('subagent_input_guard.py', {'tool_name': 'SendMessage', 'tool_input': {'to': 'a1', 'message': 'Unity 6 사례를 더 찾아봐'}}, env6), 0)
    expect('H6 general-purpose 첫 위임의 사용자 경로는 안 봄', h6('C:\\Users\\kim\\.claude\\projects 기록을 집계', agent='general-purpose'), 0)
finally:
    shutil.rmtree(proj6, ignore_errors=True)

# ── H7 habit_guard ──
# (가) 백슬래시 코드: 셸 종류와 상관없이 돈다. (나) cd: Windows 경로 기준이라 Windows에서만.
ROOT7 = 'F:\\AI\\Proj' if os.name == 'nt' else '/proj'
env7 = dict(os.environ, CLAUDE_PROJECT_DIR=ROOT7, LOCALAPPDATA='C:\\Users\\x\\AppData\\Local', TEMP='C:\\Users\\x\\AppData\\Local\\Temp')


def h7(cmd: str, tool: str = 'Bash', cwd: str | None = ROOT7, env: dict | None = None) -> tuple[int, str]:
    payload = {'tool_name': tool, 'tool_input': {'command': cmd}}
    if cwd is not None:
        payload['cwd'] = cwd
    return run('habit_guard.py', payload, env or env7)


expect('H7 python heredoc 백슬래시', h7("python -I - <<'EOF'\nprint('a\\nb')\nEOF"), 2, '[셸 습관 차단]')
expect('H7 python heredoc 백슬래시 없음', h7("python -I - <<'EOF'\nprint(1)\nEOF"), 0)
expect('H7 cat | python heredoc 백슬래시', h7("cat <<'EOF' | python -\nx = '\\t'\nEOF"), 2, '실행기')
expect('H7 bash heredoc 백슬래시', h7("bash <<'EOF'\necho a\\b\nEOF"), 2)
expect('H7 따옴표 없는 heredoc도', h7("python - <<EOF\nprint('\\n')\nEOF"), 2)
expect('H7 python -c 백슬래시', h7('python -c "print(\'a\\nb\')"'), 2, '-c')
expect('H7 python -I -X utf8 -c 백슬래시', h7('python -I -X utf8 -c "print(\'\\t\')"'), 2)
expect('H7 묶음 뒤 python -c 백슬래시', h7('ls && python -c "import re; re.compile(\'\\d\')"'), 2)
expect('H7 python -c 백슬래시 없음', h7('python -c "print(1)"'), 0)
expect('H7 node -e 백슬래시', h7('node -e "console.log(\'a\\n\')"'), 2)
expect('H7 python 스크립트 실행', h7('python -I F:/x/run.py "C:\\in.txt"'), 0)
expect('H7 cat으로 코드 파일 쓰기', h7("cat > /f/x/a.py <<'EOF'\ns = '\\n'\nEOF"), 2, '코드 파일')
expect('H7 cat heredoc | tee 코드 파일', h7("cat <<'EOF' | tee a.cs\nvar s = \"\\n\";\nEOF"), 2, '코드 파일')
expect('H7 작업 로그 heredoc 뒤 다른 명령의 .py 쓰기', h7("cat >> docs/worklog.md <<'EOF'\n- F:\\AI\\x\nEOF\ngit show HEAD:a.py > b.py"), 0)
expect('H7 작업 로그 heredoc 백슬래시',h7("cat >> docs/worklog.md <<'EOF'\n- 경로 F:\\AI\\x\nEOF"), 0)
expect('H7 git commit heredoc 백슬래시', h7("git commit -F - <<'EOF'\nfix C:\\x path\nEOF"), 0)
expect('H7 cat heredoc 뒤 git add', h7("cat > docs/a.md <<'EOF'\nF:\\AI\nEOF\ngit add docs/a.md"), 0)
expect('H7 grep 인자의 py 낱말', h7("cat <<'EOF' | grep py\nC:\\x\nEOF"), 0)
expect('H7 PS here-string → python', h7("$c = @'\nprint('a\\n')\n'@\n$c | python -", tool='PowerShell'), 2)
expect('H7 PS here-string 커밋 메시지', h7("git commit -m @'\nfix C:\\x\n'@", tool='PowerShell'), 0)
expect('H7 PS here-string 작업 로그', h7("@'\n- F:\\AI\\x\n'@ | Add-Content docs/worklog.md", tool='PowerShell'), 0)
expect('H7 PS python -c 백슬래시', h7('python -c "print(\'\\n\')"', tool='PowerShell'), 2)
expect('H7 PS 코드 파일로 Set-Content', h7("@'\nx = '\\n'\n'@ | Set-Content -Path a.py", tool='PowerShell'), 2, '코드 파일')
expect('H7 다른 도구는 무시', run('habit_guard.py', {'tool_name': 'Read', 'tool_input': {}}, env7), 0)
expect('H7 깨진 입력은 막음', (lambda r: (r.returncode, r.stderr.decode('utf-8', 'replace')))(subprocess.run([sys.executable, '-I', os.path.join(HERE, 'habit_guard.py')], input=b'{', capture_output=True, env=env7)), 2)

if os.name == 'nt':
    SUB7 = 'F:\\AI\\Proj\\src'
    expect('H7 cd 상대 하위', h7('cd docs && ls'), 2, '하위 폴더')
    expect('H7 cd 절대 하위 (Git Bash)', h7('cd /f/AI/Proj/src && dotnet build'), 2)
    expect('H7 cd 하위 ; 묶음', h7('cd src; grep -n x y'), 2)
    expect('H7 cd 하위 단독', h7('cd src'), 2)
    expect('H7 서브셸 cd', h7('(cd docs && ls)'), 0)
    expect('H7 명령 치환 안 cd', h7('x=$(cd src && pwd); echo $x'), 0)
    expect('H7 bash -c 안 cd', h7('bash -c "cd src && ls"'), 0)
    expect('H7 밖 cd /tmp', h7('cd /tmp && ls'), 0)
    expect('H7 다른 드라이브', h7('cd /c/Users/x && ls'), 0)
    expect('H7 하위에서 루트로 돌아감', h7('cd /f/AI/Proj', cwd=SUB7), 0)
    expect('H7 하위에서 cd 없음', h7('ls', cwd=SUB7), 0)
    expect('H7 하위에서 다른 하위로', h7('cd ../docs && ls', cwd=SUB7), 2)
    expect('H7 하위에서 갔다가 돌아옴', h7('cd /f/AI/Proj/docs && ls && cd /f/AI/Proj/src', cwd=SUB7), 0)
    expect('H7 갔다가 루트로', h7('cd src && make && cd /f/AI/Proj'), 0)
    expect('H7 ~ 밖', h7('cd ~/.claude/projects && ls'), 0)
    expect('H7 인자 없는 cd', h7('cd'), 0)
    expect('H7 환경 변수 밖', h7('cd "$LOCALAPPDATA/Temp" && ls'), 0)
    expect('H7 환경 변수가 하위', h7('cd "$CLAUDE_PROJECT_DIR/src"'), 2, '하위 폴더')
    expect('H7 셸 변수는 못 풂', h7('S=/f/AI/Proj/src; cd $S && ls'), 2, '풀 수 없다')
    expect('H7 같은 이름 환경 변수도 대입하면 못 풂', h7('LOCALAPPDATA=/f/AI/Proj; cd $LOCALAPPDATA'), 2, '풀 수 없다')
    expect('H7 cd -', h7('cd -'), 2, '풀 수 없다')
    expect('H7 pushd popd 짝', h7('pushd src && make && popd'), 0)
    expect('H7 pushd만', h7('pushd src && make'), 2)
    expect('H7 git -C', h7('git -C src status'), 0)
    expect('H7 if 안 cd 하위', h7('if true; then cd src; fi'), 2)
    expect('H7 if 안 cd 밖', h7('if true; then cd /tmp; fi'), 0)
    expect('H7 heredoc 본문의 cd 글자', h7("cat >> docs/a.md <<'EOF'\ncd src && ls\nEOF"), 0)
    expect('H7 따옴표 없는 heredoc 본문의 cd 글자', h7("cat >> docs/a.md <<EOF\ncd src\nEOF"), 0)
    expect('H7 따옴표 안 cd 글자', h7('echo "cd src && ls"'), 0)
    expect('H7 주석의 cd', h7('ls # cd src'), 0)
    expect('H7 ${#x} 뒤 cd도 봄', h7('a=(1 2); echo ${#a}; cd src'), 2)
    expect('H7 cwd 없이 상대 cd', h7('cd src', cwd=None), 2, '셸 현재 폴더를 몰라')
    expect('H7 cwd 없이 절대 밖 cd', h7('cd /tmp', cwd=None), 0)
    expect('H7 CLAUDE_PROJECT_DIR 없음', h7('cd src', env={k: v for k, v in env7.items() if k != 'CLAUDE_PROJECT_DIR'}), 2, 'CLAUDE_PROJECT_DIR')
    expect('H7 PS Set-Location 하위', h7('Set-Location src; Get-ChildItem', tool='PowerShell'), 2, '하위 폴더')
    expect('H7 PS cd 절대 하위', h7('cd F:\\AI\\Proj\\src', tool='PowerShell'), 2)
    expect('H7 PS -Path 하위', h7('Set-Location -Path src', tool='PowerShell'), 2)
    expect('H7 PS $env 밖', h7('Set-Location $env:TEMP', tool='PowerShell'), 0)
    expect('H7 PS 셸 변수 못 풂', h7('$d = "src"; Set-Location $d', tool='PowerShell'), 2, '풀 수 없다')
    expect('H7 PS Push/Pop 짝', h7('Push-Location src; dotnet build; Pop-Location', tool='PowerShell'), 0)
    expect('H7 PS 드라이브 상대 \\', h7('cd \\Windows', tool='PowerShell'), 0)
    expect('H7 PS 공급자 경로', h7('Set-Location HKCU:\\Software', tool='PowerShell'), 0)
    expect('H7 PS 스크립트 블록 안 cd', h7('& { Set-Location src }', tool='PowerShell'), 2)
    expect('H7 PS 하위에서 루트로', h7('Set-Location F:\\AI\\Proj', tool='PowerShell', cwd=SUB7), 0)

# (다) 사용자용 HB 앱 실행 · (라) Sync -Force — 명령 자리만
CHAT = '/f/AI/X/HB_Chat/src/HB.Chat'
DASH = 'F:\\AI\\X\\HB_Orchestration\\src\\HB.Orchestration.Dashboard'
expect('H7 HB_Chat.bat', h7('/f/AI/X/HB_Chat/HB_Chat.bat'), 2, '사용자용')
expect('H7 cmd /c HB_Chat_Desktop.bat', h7('cmd /c F:\\AI\\X\\HB_Chat\\HB_Chat_Desktop.bat'), 2, '사용자용')
expect('H7 PS Start-Process 데스크톱 exe', h7("Start-Process -FilePath 'F:\\AI\\X\\bin\\HB.Chat.Desktop.exe'", tool='PowerShell'), 2, '사용자용')
expect('H7 dotnet run 데스크톱', h7('dotnet run --project /f/AI/X/HB_Chat/src/HB.Chat.Desktop'), 2, '데스크톱')
expect('H7 HB_Orchestration.bat dashboard', h7('/f/AI/X/HB_Orchestration/HB_Orchestration.bat dashboard'), 2, '대시보드')
expect('H7 HB_Orchestration.bat 메뉴', h7('cmd /c F:\\AI\\X\\HB_Orchestration\\HB_Orchestration.bat'), 2, '메뉴')
expect('H7 HB_Orchestration.bat all', h7('HB_Orchestration.bat all', tool='PowerShell'), 2)
expect('H7 HB_Orchestration.bat publish', h7('echo Y| /f/AI/X/HB_Orchestration/HB_Orchestration.bat publish'), 0)
expect('H7 대시보드 5290', h7(f'dotnet run --project {DASH} --no-build -- --urls http://localhost:5290', tool='PowerShell'), 2, '사용자 포트')
expect('H7 대시보드 urls 없음', h7(f'dotnet run --project {DASH}', tool='PowerShell'), 2, '--urls 없이')
expect('H7 대시보드 시험 포트', h7(f'dotnet run --project {DASH} -c Release --urls http://localhost:5299', tool='PowerShell'), 0)
expect('H7 대시보드 exe 5280', h7('./HB.Orchestration.Dashboard.exe --Urls http://localhost:5280'), 2, '사용자 포트')
expect('H7 대시보드 exe 시험 포트', h7('/f/AI/X/bin/HB.Orchestration.Dashboard.exe --Urls http://localhost:5299 --contentRoot /f/AI/X'), 0)
expect('H7 HB.Chat 현재 폴더 run 포트 없음', h7(f'( cd {CHAT} && dotnet run -c Debug --no-build )'), 2, '--urls 없이')
expect('H7 HB.Chat DataDir 없음', h7(f'dotnet run --project {CHAT} -- --Urls=http://localhost:5297'), 2, 'DataDir')
expect('H7 HB.Chat 시험 서버', h7(f'( cd {CHAT} && S=/c/Users/x/AppData/Local/Temp/t && dotnet run -c Debug --no-build -- --Urls=http://localhost:5291 "--Chat:DataDir=$S/data" )'), 0)
expect('H7 HB.Chat 환경 변수 DataDir', h7(f'Chat__DataDir=/tmp/d dotnet run --project {CHAT} -- --urls=http://localhost:5291'), 0)
expect('H7 HB.Chat ASPNETCORE_URLS 5290', h7(f'ASPNETCORE_URLS=http://localhost:5290 Chat__DataDir=/tmp/d dotnet run --project {CHAT}'), 2, '사용자 포트')
expect('H7 HB.Chat exe 시험 서버', h7('F:\\AI\\X\\bin\\HB.Chat.exe --urls http://localhost:5293 --Chat:DataDir=C:\\t', tool='PowerShell'), 0)
expect('H7 cmd /c 안 dotnet run', h7(f'cmd /c "dotnet run --project {CHAT}"'), 2, '--urls 없이')
expect('H7 dotnet build HB.Chat', h7(f'dotnet build {CHAT} -c Release'), 0)
expect('H7 dotnet test HB.Chat', h7('dotnet test /f/AI/X/HB_Chat/tests/HB.Chat.Tests'), 0)
expect('H7 dotnet run 다른 프로젝트', h7('dotnet run --project /f/AI/X/tools/Show -- a b'), 0)
expect('H7 dotnet run 파일 앱', h7('dotnet run show.cs -- src/a.cs', cwd='C:\\Users\\x\\AppData\\Local\\Temp\\idx'), 0)
expect('H7 bat 읽기', h7('cat /f/AI/X/HB_Chat/HB_Chat.bat; grep -n 5290 /f/AI/X/HB_Chat/HB_Chat.bat'), 0)
expect('H7 작업 로그 heredoc 속 실행 글', h7(f"cat >> docs/a.md <<'EOF'\ndotnet run --project {CHAT} -- --urls http://localhost:5290\nHB_Chat.bat\nEOF"), 0)
expect('H7 따옴표 속 실행 글', h7('git commit -m "HB_Chat.bat 고침 · dotnet run 5290"'), 0)
SYNC = 'F:\\AI\\X\\tools\\Sync-ProjectFiles.ps1'
expect('H7 Sync -Force', h7(f'powershell -ExecutionPolicy Bypass -File {SYNC} -UnityProject F:\\AI\\T -Apply -Force'), 2, '-Force')
expect('H7 Sync & -force', h7(f"& '{SYNC}' -All -Apply -force", tool='PowerShell'), 2, '-Force')
expect('H7 Sync 줄임 -Fo', h7(f'powershell -File {SYNC} -Apply -Fo'), 2)
expect('H7 Sync -Force:$true', h7(f'& {SYNC} -Apply -Force:$true', tool='PowerShell'), 2)
expect('H7 Sync 긴 대시 –Force', h7(f'& {SYNC} -Apply –Force', tool='PowerShell'), 2)
expect('H7 Sync -Force:$false', h7(f'& {SYNC} -Apply -Force:$false', tool='PowerShell'), 0)
expect('H7 Sync -All 비교', h7(f'powershell -ExecutionPolicy Bypass -File {SYNC} -All'), 0)
expect('H7 Sync -Apply', h7(f'powershell -File {SYNC} -UnityProject F:\\AI\\T -Apply'), 0)
expect('H7 Sync 글 grep -Force', h7(f"grep -n -- '-Force' {SYNC}"), 0)
# /cross-review 2cb5d051ff 반영: 감싸개 · 본문 · 형식
DESK = 'F:\\AI\\X\\HB_Chat\\src\\HB.Chat.Desktop'
expect('H7 powershell.exe -File Sync -Force', h7(f"powershell.exe -ExecutionPolicy Bypass -File '{SYNC}' -UnityProject F:\\T -Apply -Force"), 2, '-Force')
expect('H7 cmd.exe /c HB_Chat.bat', h7("cmd.exe /c 'F:\\AI\\X\\HB_Chat\\HB_Chat.bat'"), 2, '사용자용')
expect('H7 powershell -Command 속 & Sync -Force', h7(f'powershell -Command "& \'{SYNC}\' -Apply -Force"'), 2, '-Force')
expect('H7 bash -c 속 bat', h7("bash -c '/f/AI/X/HB_Chat/HB_Chat.bat'"), 2, '사용자용')
expect('H7 iex 속 dotnet run', h7(f'iex "dotnet run --project {DESK}"', tool='PowerShell'), 2, '데스크톱')
expect('H7 env 뒤 dotnet run', h7(f'env FOO=1 dotnet run --project {CHAT}'), 2, '--urls 없이')
expect('H7 nohup dotnet run', h7(f'nohup dotnet run --project {CHAT} &'), 2, '--urls 없이')
expect('H7 Start-Process -ArgumentList 한 덩어리', h7(f"Start-Process dotnet -ArgumentList 'run --project {DESK}'", tool='PowerShell'), 2, '데스크톱')
expect('H7 Start-Process -ArgumentList 쉼표', h7(f"Start-Process dotnet -ArgumentList 'run','--project','{DESK}'", tool='PowerShell'), 2, '데스크톱')
expect('H7 bash heredoc 본문 속 bat', h7("bash <<'EOF'\n/f/AI/X/HB_Chat/HB_Chat.bat\nEOF"), 2, '사용자용')
expect('H7 here-string | powershell -Command -', h7("@'\ndotnet run --project F:/AI/X/HB_Chat/src/HB.Chat.Desktop\n'@ | powershell -Command -", tool='PowerShell'), 2, '데스크톱')
expect('H7 확장자 없는 HB_Chat', h7('cmd /c F:\\AI\\X\\HB_Chat\\HB_Chat', tool='PowerShell'), 2, '사용자용')
expect('H7 감싸개 폴더 인자는 대상 아님 (cmd dir)', h7('cmd /c dir F:\\AI\\X\\Orchestration\\HB_Orchestration', tool='PowerShell'), 0)
expect('H7 감싸개 폴더 인자는 대상 아님 (-File 뒤 인자)', h7('powershell -File build.ps1 -Root F:\\AI\\X\\HB_Orchestration', tool='PowerShell'), 0)
expect('H7 Start-Process explorer 폴더', h7(f"Start-Process explorer '{DESK}'", tool='PowerShell'), 0)
expect('H7 powershell -EncodedCommand', h7('powershell -EncodedCommand ZABpAHIA'), 2, 'EncodedCommand')
expect('H7 --urls 여러 번 (마지막 5290)', h7(f'dotnet run --project {DASH} --urls http://localhost:5299 --urls http://localhost:5290', tool='PowerShell'), 2, '사용자 포트')
expect('H7 --urls :05290', h7(f'dotnet run --project {DASH} --urls http://localhost:05290', tool='PowerShell'), 2, '사용자 포트')
expect('H7 ASPNETCORE_URLS만은 근거 아님', h7(f'ASPNETCORE_URLS=http://localhost:5299 dotnet run --project {DASH}'), 2, '--urls 없이')
expect('H7 다른 조각의 ASPNETCORE_URLS', h7(f'ASPNETCORE_URLS=http://localhost:5299 true; dotnet run --project {DASH}'), 2, '--urls 없이')
expect('H7 다른 조각의 Chat__DataDir', h7(f'export Chat__DataDir=/tmp/d; dotnet run --project {CHAT} -- --urls=http://localhost:5291'), 2, 'DataDir')
expect('H7 빈 --Chat:DataDir=', h7(f'dotnet run --project {CHAT} -- --urls=http://localhost:5299 --Chat:DataDir='), 2, 'DataDir')
expect('H7 --Chat:DataDir 값 따로', h7(f'dotnet run --project {CHAT} -- --urls=http://localhost:5299 --Chat:DataDir /tmp/d'), 0)
expect('H7 --project . (데스크톱 폴더)', h7('( cd /f/AI/X/HB_Chat/src/HB.Chat.Desktop && dotnet run --project . )'), 2, '데스크톱')
expect('H7 --project a/../HB.Chat.Desktop', h7('dotnet run --project /f/AI/X/HB_Chat/src/HB.Chat/../HB.Chat.Desktop'), 2, '데스크톱')
expect('H7 dotnet watch만', h7(f'dotnet watch --project {CHAT}'), 2, '--urls 없이')
expect('H7 dotnet 앱.dll', h7('dotnet /f/AI/X/bin/HB.Chat.dll'), 2, '--urls 없이')
expect('H7 dotnet exec 대시보드 시험 포트', h7('dotnet exec /f/AI/X/bin/HB.Orchestration.Dashboard.dll --urls http://localhost:5299'), 0)
expect('H7 dotnet run -p 프로젝트', h7(f'dotnet run -p {CHAT}'), 2, '--urls 없이')
expect('H7 dotnet run -p:속성은 프로젝트 아님', h7('dotnet run -p:Configuration=Release', cwd='C:\\Users\\x\\AppData\\Local\\Temp\\idx'), 0)
expect('H7 서브셸 변수 cd 뒤 dotnet run', h7('P=/f/AI/X/HB_Chat/src/HB.Chat; ( cd "$P" && dotnet run )'), 2, '풀 수 없어')
# 보안 재리뷰 5c04b8aca1 반영
expect('H7 bash -c 글 속 묶음 뒤 실행', h7("bash -c 'ls; /f/AI/X/HB_Chat/HB_Chat.bat'"), 2, '사용자용')
expect('H7 powershell -Command 글 속 묶음 뒤 Sync -Force', h7(f'powershell -Command "Get-Date; & \'{SYNC}\' -Apply -Force"'), 2, '-Force')
expect('H7 cmd /c 글 속 & 뒤 실행', h7('cmd /c "dir & F:\\AI\\X\\HB_Chat\\HB_Chat.bat"', tool='PowerShell'), 2, '사용자용')
expect('H7 감싸개 너무 깊음', h7('nohup env nohup env nohup env dotnet --info'), 2, '너무 깊게')
expect('H7 Start-Process -WorkingDirectory 데스크톱', h7(f"Start-Process dotnet -ArgumentList 'run' -WorkingDirectory '{DESK}'", tool='PowerShell'), 2, '데스크톱')
expect('H7 env -C 데스크톱', h7('env -C /f/AI/X/HB_Chat/src/HB.Chat.Desktop dotnet run'), 2, '데스크톱')
expect('H7 bash -c 글 속 cd 뒤 dotnet run', h7("bash -c 'cd /f/AI/X/HB_Chat/src/HB.Chat.Desktop && dotnet run'"), 2, '데스크톱')
expect('H7 -p 드라이브 경로', h7(f'dotnet run -p {DESK}', tool='PowerShell'), 2, '데스크톱')
expect('H7 -p 이름=값 속성은 프로젝트 아님', h7('dotnet run -p Configuration=Release', cwd='C:\\Users\\x\\AppData\\Local\\Temp\\idx'), 0)
expect('H7 powershell -enco 줄임', h7('powershell -enco ZABpAHIA'), 2, 'EncodedCommand')
expect('H7 powershell -ex Bypass는 EncodedCommand 아님', h7('powershell -ex Bypass -File build.ps1'), 0)

# ── commit_notice (PostToolUse) ──
repo9 = tempfile.mkdtemp(prefix='hb-notice-')
try:
    def g9(*args: str) -> None:
        subprocess.run(['git', '-C', repo9, *args], check=True, capture_output=True)

    def notice9(cmd: str, cwd: str = repo9, tool: str = 'Bash') -> tuple[int, str]:
        payload = {'tool_name': tool, 'tool_input': {'command': cmd}, 'cwd': cwd}
        r = subprocess.run([sys.executable, '-I', os.path.join(HERE, 'commit_notice.py')], input=json.dumps(payload, ensure_ascii=False).encode('utf-8'),
                           capture_output=True)
        return r.returncode, r.stdout.decode('utf-8', 'replace').strip()

    def commit9(rel: str, msg: str = 'x') -> None:
        p = os.path.join(repo9, rel)
        os.makedirs(os.path.dirname(p), exist_ok=True)
        open(p, 'a', encoding='utf-8').write('x\n')
        g9('add', '-A')
        g9('-c', 'user.name=t', '-c', 'user.email=t@t', 'commit', '-q', '-m', msg)

    g9('init', '-q')
    commit9('Orch/tools/Sync-ProjectFiles.ps1')
    commit9('Orch/tools/sync-targets.local.txt')
    expect('알림 project-files 밖 커밋은 조용', (0, notice9('git commit -m x')[1] or 'empty'), 0, 'empty')
    commit9('Orch/project-files/.claude/scripts/한글.py')
    expect('알림 project-files 커밋 (따옴표 경로)', notice9('git add -A && git commit -q -F - <<\'EOF\'\nmsg\nEOF'), 0, "Sync-ProjectFiles.ps1' -All")
    out9 = notice9('git commit -m x')[1]
    expect('알림 파일 이름은 싣지 않고 개수만 · UTF-8', (0, 'ok' if '한글.py' not in out9 and '파일 1개' in out9 else out9), 0, 'ok')
    expect('알림 git -C', notice9(f'git -C "{repo9}" commit -m x', cwd=tempfile.gettempdir()), 0, '[커밋 알림]')
    expect('알림 PowerShell', notice9('git commit -m x', tool='PowerShell'), 0, '[커밋 알림]')
    expect('알림 commit 없는 명령', (0, notice9('git log -1')[1] or 'empty'), 0, 'empty')
    commit9('Other/project-files/a.md')  # 같은 폴더에 tools/Sync-ProjectFiles.ps1이 없음
    expect('알림 Sync 없는 project-files는 조용', (0, notice9('git commit -m x')[1] or 'empty'), 0, 'empty')
    commit9('NoList/tools/Sync-ProjectFiles.ps1')
    commit9('NoList/project-files/a.md')  # 대상 목록(sync-targets.local.txt)이 없는 받은 저장소 모양
    expect('알림 대상 목록 없는 Sync는 조용', (0, notice9('git commit -m x')[1] or 'empty'), 0, 'empty')
    future_env = dict(os.environ, GIT_COMMITTER_DATE='2099-01-01T00:00:00')
    open(os.path.join(repo9, 'Orch', 'project-files', 'f.md'), 'w').write('x')
    subprocess.run(['git', '-C', repo9, 'add', '-A'], check=True, capture_output=True)
    subprocess.run(['git', '-C', repo9, '-c', 'user.name=t', '-c', 'user.email=t@t', 'commit', '-q', '-m', 'future'], check=True, capture_output=True, env=future_env)
    expect('알림 미래 시각 HEAD는 조용', (0, notice9('git commit -m x')[1] or 'empty'), 0, 'empty')
    commit9("Q'x/tools/Sync-ProjectFiles.ps1")
    commit9("Q'x/tools/sync-targets.local.txt")
    commit9("Q'x/project-files/a.md")
    out9 = notice9('git commit -m x')[1]
    expect('알림 경로에 작은따옴표면 명령을 적지 않음', (0, 'ok' if '셸 제어 글자' in out9 and '-File' not in out9 else out9), 0, 'ok')
    old_env = dict(os.environ, GIT_COMMITTER_DATE='2020-01-01T00:00:00')
    p = os.path.join(repo9, 'Orch', 'project-files', 'c.md')
    open(p, 'w').write('x')
    subprocess.run(['git', '-C', repo9, 'add', '-A'], check=True, capture_output=True)
    subprocess.run(['git', '-C', repo9, '-c', 'user.name=t', '-c', 'user.email=t@t', 'commit', '-q', '-m', 'old'], check=True, capture_output=True, env=old_env)
    expect('알림 오래된 HEAD는 조용 (커밋 실패 등)', (0, notice9('git commit -m x')[1] or 'empty'), 0, 'empty')
    expect('알림 저장소 아님은 조용', (0, notice9('git commit -m x', cwd=tempfile.gettempdir())[1] or 'empty'), 0, 'empty')
    expect('알림 깨진 입력도 exit 0', (subprocess.run([sys.executable, '-I', os.path.join(HERE, 'commit_notice.py')], input=b'{', capture_output=True).returncode, ''), 0)
finally:
    shutil.rmtree(repo9, ignore_errors=True)

# ── H8 plan_ref_guard ──
ROOT8 = 'F:\\AI\\Proj' if os.name == 'nt' else '/proj'
env8 = dict(os.environ, CLAUDE_PROJECT_DIR=ROOT8)


def h8(tool: str, ti: dict, cwd: str = ROOT8) -> tuple[int, str]:
    return run('plan_ref_guard.py', {'tool_name': tool, 'tool_input': ti, 'cwd': cwd}, env8)


def at(*parts: str) -> str:
    return os.path.join(ROOT8, *parts)


REF8 = 'docs/plans/2026-11-07_훅-추가.md'
expect('H8 코드 주석에 계획서 경로', h8('Write', {'file_path': at('src', 'a.py'), 'content': f'# 이유는 {REF8} 참고\n'}), 2, '[계획서 참조 차단]')
expect('H8 역슬래시 작업 로그 경로', h8('Edit', {'file_path': at('README.md'), 'old_string': 'a', 'new_string': 'Orchestration\\docs\\worklog\\2026-11-05_x.md'}), 2)
expect('H8 MultiEdit', h8('MultiEdit', {'file_path': at('.claude', 'commands', 'x.md'), 'edits': [{'old_string': 'a', 'new_string': 'b'}, {'old_string': 'c', 'new_string': REF8}]}), 2)
expect('H8 NotebookEdit', h8('NotebookEdit', {'notebook_path': at('n.ipynb'), 'new_source': REF8}), 2)
expect('H8 상대 경로 파일', h8('Write', {'file_path': 'src/b.cs', 'content': f'// {REF8}'}), 2)
expect('H8 규칙 글 docs/plans/…', h8('Edit', {'file_path': at('CLAUDE.md'), 'old_string': 'a', 'new_string': '("계획서 …", `docs/plans/…`)'}), 0)
expect('H8 규칙 글 YYYY-MM-DD 꼴', h8('Write', {'file_path': at('.claude', 'commands', 'record-rules.md'), 'content': '`docs/plans/YYYY-MM-DD_제목.md`'}), 0)
expect('H8 docs 안 계획서 · 색인', h8('Write', {'file_path': at('Orchestration', 'X', 'docs', 'worklog.md'), 'content': f'[계획서]({REF8})'}), 0)
expect('H8 review_tmp 안', h8('Write', {'file_path': at('review_tmp', 'p.py'), 'content': REF8}), 0)
expect('H8 프로젝트 밖 (메모리)', h8('Write', {'file_path': 'C:\\Users\\x\\.claude\\memory\\m.md' if os.name == 'nt' else '/home/x/m.md', 'content': REF8}), 0)
expect('H8 이미 있던 경로를 고침', h8('Edit', {'file_path': at('src', 'a.py'), 'old_string': f'# {REF8} 참고', 'new_string': f'# {REF8} 참고함'}), 0)
expect('H8 경로 없는 글', h8('Write', {'file_path': at('src', 'a.py'), 'content': 'print(1)'}), 0)
expect('H8 다른 도구는 무시', h8('Read', {'file_path': at('src', 'a.py')}), 0)
expect('H8 깨진 입력은 막음', (lambda r: (r.returncode, r.stderr.decode('utf-8', 'replace')))(subprocess.run([sys.executable, '-I', os.path.join(HERE, 'plan_ref_guard.py')], input=b'{', capture_output=True, env=env8)), 2)

print('ALL', ok)
sys.exit(0 if ok else 1)
