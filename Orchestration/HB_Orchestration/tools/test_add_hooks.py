"""add_hooks.py 시험 (임시 폴더만 씀). 사용: python -I tools/test_add_hooks.py → 마지막 줄 'ALL True'면 통과."""
import json
import os
import shutil
import subprocess
import sys
import tempfile

sys.stdout.reconfigure(encoding='utf-8')
HERE = os.path.dirname(os.path.abspath(__file__))
EXAMPLE = os.path.join(HERE, '..', 'project-files', '.claude', 'settings.example.json')
SCRIPTS = os.path.join(HERE, '..', 'project-files', '.claude', 'scripts')
ok = True


def expect(name: str, cond: bool, detail: str = '') -> None:
    global ok
    ok &= cond
    print(('PASS' if cond else 'FAIL'), name, detail.strip()[:200])


def run(*projects: str) -> str:
    r = subprocess.run([sys.executable, '-I', os.path.join(HERE, 'add_hooks.py'), *projects], capture_output=True)
    return (r.stdout + r.stderr).decode('utf-8', 'replace')


def project(root: str, name: str, hooks: dict | None, with_scripts: bool) -> str:
    p = os.path.join(root, name)
    os.makedirs(os.path.join(p, '.claude', 'scripts'))
    if hooks is not None:
        with open(os.path.join(p, '.claude', 'settings.json'), 'w', encoding='utf-8') as f:
            json.dump({'permissions': {'allow': ['Bash(x)']}, 'hooks': hooks}, f)
    if with_scripts:
        for n in os.listdir(SCRIPTS):
            if n.endswith('.py'):
                shutil.copyfile(os.path.join(SCRIPTS, n), os.path.join(p, '.claude', 'scripts', n))
    return p


def hooks_of(p: str) -> dict:
    with open(os.path.join(p, '.claude', 'settings.json'), encoding='utf-8') as f:
        return json.load(f)


with open(EXAMPLE, encoding='utf-8') as f:
    example = json.load(f)['hooks']
# 옛 등록: 예시에서 PreToolUse Bash 항목의 첫 훅만 남기고, 대상에만 있는 훅 하나를 더한다
old = {'PreToolUse': [{'matcher': 'Bash|PowerShell', 'hooks': [example['PreToolUse'][0]['hooks'][0],
                                                              {'type': 'command', 'command': 'python -I "$CLAUDE_PROJECT_DIR/.claude/scripts/my_own.py"'}]}]}
tmp = tempfile.mkdtemp(prefix='hb-addhooks-')
try:
    a = project(tmp, 'a', old, True)
    out = run(a)
    expect('빠진 훅을 더함', '[더함]' in out and 'text_guard.py' in out and 'stop_check.py' in out, out)
    data = hooks_of(a)
    expect('다른 설정은 그대로', data['permissions'] == {'allow': ['Bash(x)']})
    bash = next(e for e in data['hooks']['PreToolUse'] if e['matcher'] == 'Bash|PowerShell')
    expect('대상에만 있던 훅은 남음', any('my_own.py' in h['command'] for h in bash['hooks']))
    expect('예시의 모든 사건이 생김', set(example) <= set(data['hooks']), str(sorted(data['hooks'])))
    expect('백업을 남김', any(n.startswith('settings.json.bak-') for n in os.listdir(os.path.join(a, '.claude'))))
    out = run(a)
    expect('두 번째는 같음', '[같음]' in out, out)

    b = project(tmp, 'b', old, False)
    out = run(b)
    expect('스크립트가 없으면 건너뜀', '[건너뜀]' in out and '스크립트가 아직 없음' in out, out)
    expect('건너뛴 대상은 그대로', hooks_of(b)['hooks'] == old)

    c = project(tmp, 'c', None, True)
    expect('settings.json이 없으면 건너뜀', '[건너뜀]' in run(c))
    d = project(tmp, 'd', json.loads(json.dumps(example)), True)
    expect('이미 같으면 같음 · 백업 없음', '[같음]' in run(d) and not any(n.startswith('settings.json.bak-') for n in os.listdir(os.path.join(d, '.claude'))))
finally:
    shutil.rmtree(tmp, ignore_errors=True)

print('ALL', ok)
sys.exit(0 if ok else 1)
