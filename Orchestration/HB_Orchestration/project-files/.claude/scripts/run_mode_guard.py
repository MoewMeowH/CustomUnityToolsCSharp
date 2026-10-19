"""PreToolUse 훅 H3·H4 (mcp__hb-orchestration__run_mode) — 계획서 2026-10-19_훅-규칙-강제.

- H3: mode가 review · panel인데 프로젝트 안 docs/plans/에 오늘 날짜 이름이거나 오늘 고친 계획서(.md)가 없으면 막는다.
  CLAUDE.md "외부 에이전트 리뷰(review 모드)를 부르는 작업은 계획서. 없던 작업이면 리뷰 전에 만들고 '사후 작성' 표시".
  docs/plans/는 프로젝트 루트에서 깊이 4까지만 찾는다 (느려지지 않게).
- H4: reason이 [ownReview:self-reviewer]로 시작하면 ownReview는 self-reviewer 결과 원문이어야 한다
  (/cross-review 3단계). '## 지적'·'## 읽은 파일' 절이 있고, 읽은 파일 줄이 모두 subagent_in/ 안이어야 통과.
  2026-10-19: 첫 호출이 거절된 뒤 재호출 때 원문 대신 요약을 넣은 적이 있다.
- 실패하면 막는다: 통과 판정일 때만 exit 0.

입력: stdin JSON {"tool_name": "mcp__hb-orchestration__run_mode", "tool_input": {"mode", "reason", "ownReview", ...}}.
"""
import datetime
import json
import os
import re
import sys

PLAN_MODES = {'review', 'panel'}
SELF_REVIEW_TAG = '[ownReview:self-reviewer]'
MAX_DEPTH = 4


def deny(reason: str) -> None:
    print(f'[run_mode 차단] {reason}', file=sys.stderr)
    sys.exit(2)


def plan_dirs(root: str):
    for cur, dirs, _ in os.walk(root):
        depth = os.path.relpath(cur, root).count(os.sep) + (0 if cur == root else 1)
        # 무거운 폴더는 들어가지 않는다
        dirs[:] = [d for d in dirs if not d.startswith('.') and d not in ('node_modules', 'Library', 'bin', 'obj', 'Temp', 'Logs')]
        if os.path.basename(cur) == 'plans' and os.path.basename(os.path.dirname(cur)) == 'docs':
            yield cur
        if depth >= MAX_DEPTH:
            dirs[:] = []


def has_today_plan(root: str, today: datetime.date) -> bool:
    prefix = today.isoformat() + '_'
    for d in plan_dirs(root):
        for name in os.listdir(d):
            if not name.endswith('.md'):
                continue
            if name.startswith(prefix):
                return True
            mtime = datetime.date.fromtimestamp(os.path.getmtime(os.path.join(d, name)))
            if mtime == today:
                return True
    return False


def inside_subagent_in(item: str) -> bool:
    """'경로:줄 범위 (설명)' 항목의 경로가 subagent_in/ 폴더 안인지. 부분 문자열이 아니라 경로 구성 요소로 본다
    (not_subagent_in/ · subagent_in/../docs 거절, /cross-review 10bfc8de41 1-F10)."""
    path = item.split(' (', 1)[0].strip().strip('`')
    path = re.sub(r':\d+(?:-\d+)?(?:,\s*\d+(?:-\d+)?)*$', '', path)  # 끝의 :줄 범위
    parts = [p for p in path.replace('\\', '/').split('/') if p not in ('', '.')]
    if 'subagent_in' not in parts:
        return False
    after = parts[parts.index('subagent_in') + 1:]
    depth = 0
    for p in after:
        depth += -1 if p == '..' else 1
        if depth < 0:
            return False
    return bool(after)


SEARCH_LINE = re.compile(r'^(grep|glob)\s*:', re.IGNORECASE)


def read_item_ok(item: str) -> bool:
    """읽은 파일 항목 하나. 'Grep: …'·'Glob: …' 검색 기록 줄은 경로 모양 토큰(/·\\ 포함)이 있으면 모두 subagent_in/ 안이어야 하고,
    없으면 통과 (검색 기록은 폴더 밖 접근의 증거가 아님 — 읽기 차단 훅이 Grep·Glob path도 막는다. 계획서 2026-10-19_H4-검색기록줄)."""
    if not SEARCH_LINE.match(item):
        return inside_subagent_in(item)
    paths = [t.strip('`\'",;()') for t in re.split(r'\s+', SEARCH_LINE.sub('', item)) if '/' in t or '\\' in t]
    return all(inside_subagent_in(p) for p in paths)


def self_review_problem(own: str) -> str | None:
    lines = own.replace('\r\n', '\n').split('\n')
    heads = [i for i, l in enumerate(lines) if l.startswith('## ')]
    if not any(lines[i].strip() == '## 지적' for i in heads):
        return "'## 지적' 절이 없다"
    read = [i for i in heads if lines[i].strip() == '## 읽은 파일']
    if not read:
        return "'## 읽은 파일' 절이 없다 (요약이 아니라 결과 원문을 넣어야 한다)"
    start = read[0] + 1
    end = next((i for i in heads if i > read[0]), len(lines))
    items = [l.strip()[2:].strip() for l in lines[start:end] if l.strip().startswith('- ')]
    if not items:
        return "'## 읽은 파일'에 항목이 없다"
    outside = [p for p in items if not read_item_ok(p)]
    if outside:
        return f"읽은 파일 중 subagent_in/ 밖이 있다: {outside[0][:120]} (서브에이전트 결과를 그대로 쓰지 말고 사용자에게 알린다)"
    return None


def check(data: dict, today: datetime.date | None = None) -> None:
    if data.get('tool_name') != 'mcp__hb-orchestration__run_mode':
        return
    ti = data.get('tool_input') or {}
    # 키 이름은 대소문자·밑줄을 무시하고 찾는다 (MCP 클라이언트가 이름을 바꿔 넘겨도 검사가 빠지지 않게).
    by_key = {k.replace('_', '').lower(): v for k, v in ti.items()}
    mode = (by_key.get('mode') or '').strip().lower()
    reason = (by_key.get('reason') or '').strip()
    own = by_key.get('ownreview') or ''

    if reason.startswith(SELF_REVIEW_TAG) and (problem := self_review_problem(own)) is not None:
        keys = '' if own else f" (받은 입력 키: {', '.join(sorted(ti))})"
        deny(f'ownReview가 self-reviewer 결과 원문이 아니다: {problem}{keys}. /cross-review 3단계: ownReview에는 self-reviewer 결과 원문만 넣는다.')

    if mode in PLAN_MODES:
        root = os.environ.get('CLAUDE_PROJECT_DIR') or data.get('cwd') or os.getcwd()
        if not has_today_plan(root, today or datetime.date.today()):
            deny(f"'{mode}' 전에 계획서가 필요하다: 프로젝트 안 docs/plans/에 오늘 날짜 이름이거나 오늘 고친 계획서가 없다. "
                 "CLAUDE.md '계획서와 작업 로그': 계획서 없이 진행하던 작업이면 리뷰 전에 만들고 맨 위에 '사후 작성'이라고 표시한다.")


def main() -> None:
    try:
        sys.stderr.reconfigure(encoding='utf-8')
        # stdin은 바이트로 읽어 UTF-8로 푼다: Windows에서 파이프 stdin의 기본 인코딩은 cp949라
        # json.load(sys.stdin)이면 한글이 깨져 '## 지적'을 못 찾았다 (2026-10-19 실사용 오탐).
        check(json.loads(sys.stdin.buffer.read().decode('utf-8-sig')))
    except SystemExit:
        raise
    except Exception as ex:  # 실패하면 막는다
        deny(f'검사 중 오류 ({type(ex).__name__}: {ex}).')
    sys.exit(0)


if __name__ == '__main__':
    main()
