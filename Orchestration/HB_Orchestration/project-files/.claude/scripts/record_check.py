"""PostToolUse 훅 H12 · H13 (Write · Edit · MultiEdit): 기록 서식과 서브에이전트 정의를 쓴 직후에 본다. 막지 않는다 (경고).

- H12 기록 서식 (/record-rules):
  - docs/plans/*.md를 **새로 쓸 때(Write)**: '## 요약' · '## 배경' · '## 계획' · '## 버린 대안과 이유' · '## 위험' · '## 테스트' · '## 실제 확인' ·
    '## 사용자에게 물을 것' 절 (제목이 그 낱말로 시작하면 된다). Edit은 보지 않는다 — 옛 계획서에 '## 결정'을 덧붙일 때마다 경고가 나지 않게.
  - docs/worklog/*.md를 새로 쓸 때(Write, _archive_ 제외): 첫 줄에 계획서 링크(../plans/).
  - MISTAKES.md · .claude/mistakes/*.md에 새로 들어가는 '- ' 줄: '- 하지 말 것 → 대신 할 것 (YYYY-MM-DD, 출처)' 꼴.
- H13 서브에이전트 정의: .claude/agents/*.md(project-files 원본 포함)를 쓰면 그 폴더로 check_agents.py를 돌리고, 실패면 출력을 경고로.
- 경고는 hook_warnings.jsonl에 남기고 additionalContext로 알린다. 실패해도 막지 않는다: 늘 exit 0.

입력: stdin JSON {"tool_name": "Write"|"Edit"|"MultiEdit", "tool_input": {...}, "cwd": "..."}.
"""
import os
import re
import subprocess
import sys

sys.dont_write_bytecode = True  # 훅 폴더에 __pycache__를 남기지 않는다 (저장소에 섞이지 않게)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # -I는 스크립트 폴더를 경로에 넣지 않는다
from hook_common import log_warning, notify, read_input  # noqa: E402

PLAN_SECTIONS = ['요약', '배경', '계획', '버린 대안과 이유', '위험', '테스트', '실제 확인', '사용자에게 물을 것']
HEAD = re.compile(r'(?m)^##[ \t]+(.+?)[ \t]*$')
COMMENT = re.compile(r'<!--.*?-->', re.S)
MISTAKE_LINE = re.compile(r'^- \S.* → \S.* \(\d{4}-\d{2}-\d{2}, [^)]+\)\s*$')


def new_texts(tool: str, ti: dict) -> list[str]:
    if tool == 'Write':
        return [ti.get('content') or '']
    if tool == 'Edit':
        return [ti.get('new_string') or '']
    if tool == 'MultiEdit':
        return [e.get('new_string') or '' for e in ti.get('edits') or []]
    return []


def parts_of(path: str) -> list[str]:
    return [p for p in path.replace('\\', '/').split('/') if p]


def problems(tool: str, ti: dict) -> list[tuple[str, str, str]]:
    """(훅, 종류, 글) 목록."""
    path = ti.get('file_path') or ''
    parts = parts_of(path)
    if not parts or not parts[-1].lower().endswith('.md'):
        return []
    name, parent = parts[-1], (parts[-2].lower() if len(parts) > 1 else '')
    grand = parts[-3].lower() if len(parts) > 2 else ''
    out = []
    texts = new_texts(tool, ti)
    if tool == 'Write' and parent == 'plans' and grand == 'docs':
        heads = [m.group(1) for m in HEAD.finditer(texts[0])]
        if miss := [s for s in PLAN_SECTIONS if not any(h.startswith(s) for h in heads)]:
            out.append(('H12', 'plan-sections', f"계획서 {name}에 절이 없다: {', '.join(miss)} — /record-rules 1단계 구성 "
                        '(요약 / 배경 / 계획 / 버린 대안과 이유 / 위험 / 테스트 / 실제 확인 / 사용자에게 물을 것). 해당 없으면 절을 두고 "없음"을 쓴다.'))
    if tool == 'Write' and parent == 'worklog' and grand == 'docs' and not name.startswith('_archive_'):
        first = texts[0].lstrip(chr(0xFEFF)).split('\n', 1)[0]
        if '../plans/' not in first:
            out.append(('H12', 'worklog-link', f'작업 로그 {name}의 첫 줄에 계획서 링크(../plans/…)가 없다 — /record-rules 3단계: 짝 파일 첫 줄에 계획서 링크.'))
    if name == 'MISTAKES.md' or (parent == 'mistakes' and grand == '.claude'):
        # 주석 밖 '- ' 줄만 항목이다 (subagent_copy · subagent_input_guard와 같은 기준). 쓴 뒤 디스크 파일에서 주석을 걷고, 이번에 들어간 줄만 본다
        try:
            with open(path, encoding='utf-8-sig') as f:
                items = {l.strip() for l in COMMENT.sub('', f.read()).split('\n') if l.startswith('- ')}
        except OSError:
            items = set()
        bad = [l.strip() for t in texts for l in t.split('\n') if l.strip() in items and not MISTAKE_LINE.match(l.strip())]
        if bad:
            out.append(('H12', 'mistake-format', f"실수 목록 {name}의 줄이 서식과 다르다: {bad[0][:120]} — "
                        "'- 하지 말 것 → 대신 할 것 (YYYY-MM-DD, 출처)' (/record-rules '실수 기록')."))
    if parent == 'agents' and grand == '.claude':
        folder = os.path.dirname(os.path.abspath(path))
        r = subprocess.run([sys.executable, '-I', os.path.join(os.path.dirname(os.path.abspath(__file__)), 'check_agents.py'), folder],
                           capture_output=True, timeout=30)
        if r.returncode != 0:
            msg = (r.stdout + r.stderr).decode('utf-8', 'replace').strip()
            out.append(('H13', 'agents-check', f'check_agents.py가 {folder}에서 실패했다: {msg[:400]}'))
    return out


def main() -> None:
    try:
        data = read_input()
        found = problems(data.get('tool_name', ''), data.get('tool_input') or {})
        for h, kind, text in found:
            log_warning(h, kind, text)
        if found:
            notify('PostToolUse', ' / '.join(f'[{h} 경고] {t}' for h, _, t in found))
    except Exception:  # 경고 훅은 막지 않는다
        pass
    sys.exit(0)


if __name__ == '__main__':
    main()
