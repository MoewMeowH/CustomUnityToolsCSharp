"""PreToolUse 훅 H3·H4 (mcp__hb-orchestration__run_mode) — 계획서 2026-10-19_훅-규칙-강제.

- H3: mode가 review_main · review_high인데 프로젝트 안 docs/plans/에 오늘 날짜 이름이거나 오늘 고친 계획서(.md)가 없으면 막는다.
  CLAUDE.md "외부 에이전트 리뷰(review_* 모드)를 부르는 작업은 계획서. 없던 작업이면 리뷰 전에 만들고 '사후 작성' 표시".
  docs/plans/는 프로젝트 루트에서 깊이 4까지만 찾는다 (느려지지 않게).
- H4: reason이 [ownReview:<서브에이전트>]로 시작하면 ownReview는 그 서브에이전트 결과 원문이어야 한다
  (/cross-review 3단계 · /mode perf_* 3단계). 서브에이전트는 self-reviewer · perf-reviewer · security-reviewer, 둘 이상은
  '+'로 잇고(예: [ownReview:self-reviewer+security-reviewer]) 결과도 이어 붙인다 — '## 지적' 절이 서브에이전트 수만큼 있어야 한다.
  모르는 이름 · 겹친 이름이면 막는다 (계획서 2026-10-23_서브에이전트-종류-추가). '## 지적'·'## 읽은 파일' 절이 있고, 읽은 파일 줄이 모두 subagent_in/ 안이어야 통과.
  2026-10-19: 첫 호출이 거절된 뒤 재호출 때 원문 대신 요약을 넣은 적이 있다.
  '## 읽은 파일' 절이 여러 개면 모두 검사한다. '## 비고' 등 다른 절은 경로 검사를 하지 않는다 — 설명 줄은 '## 비고'에 쓰고
  '## 읽은 파일'의 설명 줄은 계속 막는다 (계획서 2026-10-22_self-reviewer-비고-칸, 느슨하게 만들지 않는다는 사용자 결정).
  '## 읽은 파일'에 '- '로 시작하지 않는 줄이 있어도 막는다. 태그가 없어도: review_*에 ownReview가 있거나, ownReview에
  '## 읽은 파일' 절이 있으면 막는다 — 태그를 빼면 검사를 건너뛰던 빈틈 (계획서 2026-10-23_H5-셸-문법-우회).
- 실패하면 막는다: 통과 판정일 때만 exit 0.

입력: stdin JSON {"tool_name": "mcp__hb-orchestration__run_mode", "tool_input": {"mode", "reason", "ownReview", ...}}.
"""
import datetime
import json
import os
import re
import sys

PLAN_MODES = {'review_main', 'review_high'}
# ownReview를 쓸 수 있는 검토형 서브에이전트 (계획서 2026-10-23_서브에이전트-종류-추가). 둘 이상이면 '+'로 잇고, 결과 원문도 이어 붙인다.
REVIEWERS = {'self-reviewer', 'perf-reviewer', 'security-reviewer'}
# 접두어 · 이름 모두 대소문자를 무시한다 — [ownreview:…]로 쓰면 검사를 건너뛰던 빈틈 (/self-review fffbc4440a C5)
OWN_REVIEW_TAG = re.compile(r'^\[ownReview:([^\]]*)\]', re.IGNORECASE)
# '## 읽은 파일 (추가)' · 앞 공백 · '###' · 탭 같은 변형 제목도 읽은 파일 절이다 (/cross-review 8722c947f7 security C9,
# /self-review security C9). 절의 끝은 Markdown 제목 모양 줄(ANY_HEAD)로 정한다.
READ_HEAD = re.compile(r'(?m)^[ \t]{0,3}#{2,}[ \t]+읽은[ \t]*파일')
ANY_HEAD = re.compile(r'^[ \t]{0,3}#{1,6}[ \t]')
FINDINGS_HEAD = re.compile(r'(?m)^[ \t]{0,3}#{1,6}[ \t]+지적')  # '# 지적'도 (/self-review security ddb47044d1 C6)
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
    read = [i for i, l in enumerate(lines) if READ_HEAD.match(l)]
    if not read:
        return "'## 읽은 파일' 절이 없다 (요약이 아니라 결과 원문을 넣어야 한다)"
    any_heads = [i for i, l in enumerate(lines) if ANY_HEAD.match(l)]
    items = []
    for head in read:  # 절이 여러 개여도 모두 검사 (두 번째 절에 숨은 폴더 밖 경로)
        end = next((i for i in any_heads if i > head), len(lines))
        body = [l.strip() for l in lines[head + 1:end] if l.strip()]
        # '- '가 아닌 줄(1. 경로 · * 경로 · 맨 경로)은 검사에서 빠지던 빈틈 (security-reviewer 7282089076 C11) — 경로 줄만 받는다
        if odd := [l for l in body if not l.startswith('- ')]:
            return f"'## 읽은 파일'에 '- '로 시작하지 않는 줄이 있다: {odd[0][:120]} (경로 줄만 쓰고 설명은 '## 비고'에)"
        items += [l[2:].strip() for l in body]
    if not items:
        return "'## 읽은 파일'에 항목이 없다"
    outside = [p for p in items if not read_item_ok(p)]
    if outside:
        return f"읽은 파일 중 subagent_in/ 밖이 있다: {outside[0][:120]} (서브에이전트 결과를 그대로 쓰지 말고 사용자에게 알린다)"
    return None


def blocks_problem(own: str, agents: int) -> str | None:
    """이어 붙인 결과를 '## 지적' 절 제목(줄 맨 앞)마다 서브에이전트 블록으로 나눠, 블록마다 원문 형식을 검사한다
    (둘째 결과를 요약으로 바꿔도 통과하던 빈틈, /self-review fffbc4440a C1). 같은 원문을 두 번 붙인 것은 내용 비교 없이는 못 막는다."""
    lines = own.replace('\r\n', '\n').split('\n')
    starts = [i for i, l in enumerate(lines) if l.startswith('## ') and l.strip() == '## 지적']
    if len(starts) < agents:
        return f"태그의 서브에이전트 {agents}개인데 '## 지적' 절이 {len(starts)}개다 (결과 원문을 모두 이어 붙인다)"
    if agents < 2:
        return None
    for n, start in enumerate(starts):
        end = starts[n + 1] if n + 1 < len(starts) else len(lines)
        if (problem := self_review_problem('\n'.join(lines[start:end]))) is not None:
            return f'{n + 1}번째 결과: {problem}'
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

    if tag := OWN_REVIEW_TAG.match(reason):
        agents = [a.strip().lower() for a in tag.group(1).split('+')]
        if unknown := [a for a in agents if a not in REVIEWERS]:
            deny(f"reason 태그 '{tag.group(0)}'의 서브에이전트 '{unknown[0]}'를 모른다: {', '.join(sorted(REVIEWERS))} 중에서 '+'로 잇는다.")
        if len(set(agents)) != len(agents):
            deny(f"reason 태그 '{tag.group(0)}'에 같은 서브에이전트가 두 번 있다: 서브에이전트마다 한 번씩만 쓴다.")
        problem = self_review_problem(own) or blocks_problem(own, len(agents))
        if problem is not None:
            keys = '' if own else f" (받은 입력 키: {', '.join(sorted(ti))})"
            deny(f'ownReview가 {"+".join(agents)} 결과 원문이 아니다: {problem}{keys}. /cross-review 3단계 · /mode 3단계: ownReview에는 서브에이전트 결과 원문만 넣는다.')
    # 태그가 빠지면 검사를 건너뛰던 빈틈 (계획서 2026-10-23_H5-셸-문법-우회, security-reviewer 7282089076 C10).
    # review_*는 /cross-review로만 부르고 ownReview는 늘 서브에이전트 원문이다. perf_*는 설계안뿐이면 태그 없는 지휘자 의견 (mode.md 2단계).
    elif mode in PLAN_MODES and own.strip():
        deny(f"'{mode}'의 ownReview는 서브에이전트 결과 원문이다 — reason 맨 앞에 [ownReview:self-reviewer](둘이면 "
             "[ownReview:self-reviewer+security-reviewer])를 붙인다 (/cross-review 3단계).")
    elif READ_HEAD.search(own):
        deny("ownReview에 서브에이전트 결과 형식('## 읽은 파일')이 있는데 reason에 [ownReview:<서브에이전트>] 태그가 없다 — "
             '태그를 붙여 훅이 원문을 검사하게 한다 (/cross-review 3단계 · /mode 3단계).')
    # perf_*는 대상이 설계안뿐이면 태그 없는 지휘자 의견('## 지휘자 의견')을 쓴다. 서브에이전트 결과를 요약해 '## 지적'만 남긴 것은 막는다
    # (2026-10-19 사고 유형, 계획서 2026-10-23_따옴표-명령-허용목록과-perf-ownReview)
    elif mode.startswith('perf_') and FINDINGS_HEAD.search(own):
        deny("태그 없는 perf ownReview에 '## 지적' 절이 있다 — perf-reviewer 결과면 원문 그대로 넣고 reason 맨 앞에 [ownReview:perf-reviewer], "
             "설계안에 대한 지휘자 의견이면 '## 지휘자 의견' 제목으로 쓴다 (/mode 2단계).")

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
