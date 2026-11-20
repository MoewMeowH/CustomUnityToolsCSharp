"""subagent_copy.py 시험. 사용: python -I .claude/scripts/test_subagent_copy.py → 마지막 줄 'ALL True'면 통과 (exit 0)."""
import os
import shutil
import subprocess
import sys
import tempfile

sys.stdout.reconfigure(encoding='utf-8')
HERE = os.path.dirname(os.path.abspath(__file__))
ok = True


def run(*args: str, cwd: str) -> tuple[int, str]:
    env = dict(os.environ, HB_SUBAGENT_MISTAKES_DIR=os.path.join(cwd, 'mistakes'))
    r = subprocess.run([sys.executable, '-I', os.path.join(HERE, 'subagent_copy.py'), *args], capture_output=True, cwd=cwd, env=env)
    return r.returncode, (r.stdout + r.stderr).decode('utf-8', 'replace')


def expect(name: str, cond: bool, detail: str = '') -> None:
    global ok
    ok &= cond
    print(('PASS' if cond else 'FAIL'), name, detail.strip()[:160])


tmp = tempfile.mkdtemp(prefix='hb-subcopy-')
try:
    src = os.path.join(tmp, 'src')
    os.makedirs(os.path.join(src, 'b'))
    for rel, body in (('a.diff', 'diff --git a/x b/x\n'), ('REVIEW_GUIDELINES.md', '# 지침\n'), ('CLAUDE.md', '# 지시\n'), ('b/a.diff', '다른 a\n')):
        with open(os.path.join(src, rel), 'w', encoding='utf-8') as f:
            f.write(body)

    code, out = run('--out', 'subagent_in/r1', 'src/a.diff', 'src/REVIEW_GUIDELINES.md', 'src/CLAUDE.md', cwd=tmp)
    expect('복사 · 해시 출력', code == 0 and 'SHA-256 모두 같음' in out, out)
    expect('CLAUDE.md는 .txt로', os.path.isfile(os.path.join(tmp, 'subagent_in', 'r1', 'CLAUDE.md.txt'))
           and not os.path.exists(os.path.join(tmp, 'subagent_in', 'r1', 'CLAUDE.md')))
    with open(os.path.join(tmp, 'subagent_in', 'r1', 'a.diff'), encoding='utf-8') as f:
        expect('내용 같음', f.read() == 'diff --git a/x b/x\n')

    code, out = run('--out', 'subagent_in/r1', 'src/a.diff', cwd=tmp)
    expect('이미 있는 파일은 덮지 않음', code == 1 and '이미 있다' in out, out)
    code, out = run('--out', 'subagent_in/r2', 'src/a.diff', 'src/b/a.diff', cwd=tmp)
    expect('같은 이름 둘은 실패 · 아무것도 안 만듦', code == 1 and '겹친다' in out and not os.path.exists(os.path.join(tmp, 'subagent_in', 'r2')), out)
    code, out = run('--out', 'review_tmp/r3', 'src/a.diff', cwd=tmp)
    expect('subagent_in 밖은 실패', code == 1 and 'subagent_in' in out, out)
    code, out = run('--out', 'subagent_in/../x', 'src/a.diff', cwd=tmp)
    expect("'..'은 실패", code == 1, out)
    code, out = run('--out', 'subagent_in/r4', 'src/b', cwd=tmp)
    expect('폴더는 실패', code == 1 and 'subagent_copy_tracked' in out, out)
    code, out = run('--out', 'subagent_in/r5', '--agent', '../x', 'src/a.diff', cwd=tmp)
    expect('이상한 서브에이전트 이름은 실패', code == 1, out)

    # --agent: 실수 목록 폴더는 HB_SUBAGENT_MISTAKES_DIR(시험 임시 폴더의 mistakes/)
    mistakes = os.path.join(tmp, 'mistakes')
    os.makedirs(mistakes)
    with open(os.path.join(mistakes, 'zz-test-agent.md'), 'w', encoding='utf-8') as f:
        f.write('# x\n\n<!--\n- 주석 안 줄은 항목이 아니다\n-->\n- 하지 말 것 → 대신 할 것 (2026-11-20, 시험)\n')
    with open(os.path.join(mistakes, 'zz-empty-agent.md'), 'w', encoding='utf-8') as f:
        f.write('# x\n\n<!--\n- 주석 안 줄뿐\n-->\n')
    code, out = run('--out', 'subagent_in/r6', '--agent', 'zz-test-agent', 'src/a.diff', cwd=tmp)
    expect('항목이 있으면 _SUBAGENT_MISTAKES.md', code == 0 and os.path.isfile(os.path.join(tmp, 'subagent_in', 'r6', '_SUBAGENT_MISTAKES.md')), out)
    code, out = run('--out', 'subagent_in/r7', '--agent', 'zz-empty-agent', 'src/a.diff', cwd=tmp)
    expect('주석뿐이면 넣지 않음', code == 0 and not os.path.exists(os.path.join(tmp, 'subagent_in', 'r7', '_SUBAGENT_MISTAKES.md')), out)
    code, out = run('--out', 'subagent_in/r8', '--agent', 'zz-none-agent', 'src/a.diff', cwd=tmp)
    expect('실수 파일이 없으면 넣지 않음', code == 0 and not os.path.exists(os.path.join(tmp, 'subagent_in', 'r8', '_SUBAGENT_MISTAKES.md')), out)

    # 도중 실패면 이번에 만든 사본을 지운다: 둘째 파일 자리에 같은 이름 폴더를 미리 만들어 복사를 실패시킨다
    os.makedirs(os.path.join(tmp, 'subagent_in', 'r9', 'REVIEW_GUIDELINES.md'))
    code, out = run('--out', 'subagent_in/r9', 'src/a.diff', 'src/REVIEW_GUIDELINES.md', cwd=tmp)
    expect('이미 있는 이름(폴더)이면 시작 전에 실패', code == 1 and not os.path.exists(os.path.join(tmp, 'subagent_in', 'r9', 'a.diff')), out)
finally:
    shutil.rmtree(tmp, ignore_errors=True)

print('ALL', ok)
sys.exit(0 if ok else 1)
