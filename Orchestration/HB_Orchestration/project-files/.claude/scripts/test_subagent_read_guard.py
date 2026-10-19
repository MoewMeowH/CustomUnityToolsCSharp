"""subagent_read_guard.py 회귀 시험 (Windows 경로 우회 포함). 프로젝트 루트는 이 스크립트 위치(.claude/scripts/)에서 구한다.
사용: python -I .claude/scripts/test_subagent_read_guard.py  → 마지막 줄 'ALL True'면 통과 (exit 0), 아니면 exit 1.
"""
import json
import os
import subprocess
import sys

sys.stdout.reconfigure(encoding='utf-8')
HERE = os.path.dirname(os.path.abspath(__file__))
GUARD = os.path.join(HERE, 'subagent_read_guard.py')
R = os.path.dirname(os.path.dirname(HERE))          # 프로젝트 루트
env = dict(os.environ, CLAUDE_PROJECT_DIR=R)
S = os.path.join(R, 'subagent_in', 'hooktest')      # 허용 폴더 안 (파일이 없어도 경로 판정만 본다)
DRIVE = os.path.splitdrive(R)[0][:1] or 'C'
REST = os.path.splitdrive(R)[1]                     # 드라이브 뒤 경로 (\AI\...)
BS = '\\'

cases = [
    ('Read', os.path.join(S, 'VerdictLog.cs'), 0),
    ('Read', 'subagent_in/hooktest/VerdictLog.cs', 0),
    ('Read', os.path.join(S, 'console.md'), 0),
    ('Read', os.path.join(S, 'VerdictLog.cs.'), 2),
    ('Read', os.path.join(S, 'VerdictLog.cs '), 2),
    ('Read', os.path.join(S, 'VerdictLog.cs::$DATA'), 2),
    ('Read', os.path.join(R, 'README.md::$DATA'), 2),
    ('Read', os.path.join(R, 'README.md.'), 2),
    ('Read', os.path.join(S, '..', '..', 'README.md.'), 2),
    ('Read', os.path.join(S, 'CON'), 2),
    ('Read', os.path.join(S, 'COM1.txt'), 2),
    ('Read', os.path.join(S, 'conout$.log'), 2),
    ('Read', BS * 2 + '?' + BS + os.path.join(R, 'README.md'), 2),
    ('Read', BS * 2 + '?' + BS + os.path.join(S, 'VerdictLog.cs'), 2),
    ('Read', BS * 2 + '.' + BS + os.path.join(R, 'README.md'), 2),
    ('Read', BS * 2 + 'localhost' + BS + DRIVE + '$' + REST + BS + 'README.md', 2),
    ('Read', BS * 2 + '?' + BS + 'UNC' + BS + 'localhost' + BS + DRIVE + '$' + REST + BS + 'README.md', 2),
    ('Read', os.path.join(R, 'SUBAGENT_IN', '..', 'README.md'), 2),
    ('Read', 'NUL', 2),
    ('Read', os.path.join(R, 'docs', 'worklog.md'), 2),
    ('Read', os.path.join(R, 'CLAUDE.md'), 2),
    ('Grep', S, 0),
    ('Grep', os.path.join(S, 'CON'), 2),
    ('Grep', BS * 2 + '?' + BS + S, 2),
    ('Glob', S, 0),
]
ok = True
for tool, path, want in cases:
    ti = {'file_path': path} if tool == 'Read' else {'path': path, 'pattern': '*.cs'}
    r = subprocess.run([sys.executable, '-I', GUARD], input=json.dumps({'tool_name': tool, 'tool_input': ti}),
                       capture_output=True, text=True, encoding='utf-8', env=env)
    good = r.returncode == want
    ok &= good
    print('OK' if good else 'XX', r.returncode, tool, path, '|', r.stderr.strip()[:45])
print('ALL', ok)
sys.exit(0 if ok else 1)
