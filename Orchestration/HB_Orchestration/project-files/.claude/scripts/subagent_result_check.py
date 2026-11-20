"""SubagentStop 훅 H10: 검토형 서브에이전트 결과가 정의 파일의 출력 형식을 지켰는지 본다. 막지 않는다 (경고).

- 백그라운드 서브에이전트는 PostToolUse(Agent)에 결과가 없다 — SubagentStop의 last_assistant_message가 최종 답이다 (실측 2026-11-20 claude 2.1.329).
  HB_Chat(Agent SDK)은 보고를 SubagentHandback 도구 호출로 넘겨 last_assistant_message가 비므로, 그때는 agent_transcript_path의 마지막 보고 호출 message를 본다.
  입력의 session_id는 부모 세션 id다 (실측 — Stop 훅과 같은 키).
- 대상 · 검사:
  - self-reviewer · perf-reviewer · security-reviewer · direction-reviewer: '## 지적' · '## 읽은 파일' 절, 읽은 파일 줄은 '- '로 시작하고
    subagent_in/ 안 (run_mode_guard H4와 같은 검사).
  - evidence-collector: '## 읽은 파일' · '## 요약' 절, 읽은 파일 줄은 subagent_in/ 안.
  - 그 밖의 서브에이전트는 보지 않는다.
- 위반이면 hook_warnings.jsonl에 남기고, 세션 상태 파일(hook_state/<session>.json)의 pending에 넣는다 → 턴 끝 Stop 훅(stop_check, H11)이 지휘자에게 알린다
  (서브에이전트를 막아 다시 쓰게 하지 않는다 — 내용까지 바뀔 수 있다는 사용자 결정 "턴 끝에 지휘자에게 알림").
- 실패해도 막지 않는다: 늘 exit 0.

입력: stdin JSON {"hook_event_name": "SubagentStop", "session_id", "agent_type", "agent_id", "last_assistant_message", "agent_transcript_path", ...}.
"""
import json
import os
import re
import sys

sys.dont_write_bytecode = True  # 훅 폴더에 __pycache__를 남기지 않는다 (저장소에 섞이지 않게)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # -I는 스크립트 폴더를 경로에 넣지 않는다
from hook_common import add_pending, log_warning, read_input  # noqa: E402
from run_mode_guard import READ_HEAD, ANY_HEAD, read_item_ok, self_review_problem  # noqa: E402

REVIEWERS = {'self-reviewer', 'perf-reviewer', 'security-reviewer', 'direction-reviewer'}
SUMMARY_HEAD = re.compile(r'(?m)^[ \t]{0,3}#{2,}[ \t]+요약')


def evidence_problem(text: str) -> str | None:
    lines = text.replace('\r\n', '\n').split('\n')
    read = [i for i, l in enumerate(lines) if READ_HEAD.match(l)]
    if not read:
        return "'## 읽은 파일' 절이 없다"
    if not SUMMARY_HEAD.search(text):
        return "'## 요약' 절이 없다"
    heads = [i for i, l in enumerate(lines) if ANY_HEAD.match(l)]
    for head in read:
        end = next((i for i in heads if i > head), len(lines))
        for l in (l.strip() for l in lines[head + 1:end]):
            if l and not (l.startswith('- ') and read_item_ok(l[2:].strip())):
                return f"읽은 파일 줄이 '- 경로'가 아니거나 subagent_in/ 밖이다: {l[:120]}"
    return None


def problem_of(agent: str, text: str) -> str | None:
    if agent in REVIEWERS:
        return self_review_problem(text)
    if agent == 'evidence-collector':
        return evidence_problem(text)
    return None


def handback(transcript: str) -> str:
    """서브에이전트 기록에서 마지막 보고 도구 호출(이름이 Handback으로 끝나는 도구의 message)의 글. 없으면 빈 글."""
    found = ''
    try:
        with open(transcript, encoding='utf-8', errors='replace') as f:
            for line in f:
                if 'Handback' not in line:
                    continue
                try:
                    content = json.loads(line).get('message', {}).get('content')
                except (ValueError, AttributeError):
                    continue
                for b in content if isinstance(content, list) else []:
                    if isinstance(b, dict) and b.get('type') == 'tool_use' and str(b.get('name') or '').endswith('Handback'):
                        message = (b.get('input') or {}).get('message')
                        if isinstance(message, str):
                            found = message
    except OSError:
        pass
    return found


def check(data: dict) -> None:
    agent = (data.get('agent_type') or '').strip().lower()
    if agent not in REVIEWERS and agent != 'evidence-collector':
        return
    # HB_Chat(Agent SDK)에서는 서브에이전트가 보고를 도구 호출(SubagentHandback)로 넘겨 last_assistant_message가 빈다 (실측 2026-11-20)
    text = data.get('last_assistant_message') or handback(data.get('agent_transcript_path') or '')
    problem = problem_of(agent, text) if text.strip() else '최종 답이 비어 있다'
    if problem is None:
        return
    detail = (f"서브에이전트 {agent}({data.get('agent_id') or '?'}) 결과가 형식을 어겼다: {problem}. "
              '결과를 그대로 쓰지 말고 사용자에게 알린다 (/self-review "서브에이전트 공통 규칙" 결과 형식 검사).')
    log_warning('H10', 'subagent-format', detail)
    add_pending(data.get('session_id') or '', detail)


def main() -> None:
    try:
        check(read_input())
    except Exception:  # 경고 훅은 막지 않는다
        pass
    sys.exit(0)


if __name__ == '__main__':
    main()
