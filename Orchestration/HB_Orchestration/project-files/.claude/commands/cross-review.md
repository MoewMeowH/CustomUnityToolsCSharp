---
description: Claude와 외부 에이전트가 독립적으로 리뷰한 뒤 결과를 종합한다
argument-hint: [main|high] [파일/커밋 범위, 비우면 현재 미커밋 변경]
---

인자: $ARGUMENTS
- 첫 단어가 `high`면 `review_high`(sol · DeepSeek 동시), `main`이거나 없으면 `review_main`. (예전 인자 `panel`은 `high`로 읽는다.)
- 나머지가 대상. 비어 있으면 `git diff HEAD`의 미커밋 변경.

1. diff가 있으면 대상 diff와 변경 의도를 파악한다. 의도가 불분명하면 커밋 메시지·주변 코드로 추론하고, 그래도 모르면 한 줄로 질문한다. / diff가 없으면(기존 코드 점검) 변경 의도 대신 "이 코드의 역할과 점검 목적"을 쓰고, 파일 전체가 아니라 핵심 클래스·메서드 위주로 추려서 넘긴다.
2. **자체 검토 (시범: self-reviewer 서브에이전트, 계획서 `Orchestration/HB_Orchestration/docs/plans/2026-10-19_서브에이전트-도입-검토.md`)** — 순서를 바꾸지 않는다.
   - 2-1. **지휘자 검토를 먼저 저장**: REVIEW_GUIDELINES.md 기준으로 너의 리뷰를 완성해 작업 로그에 다 쓴다. self-reviewer 결과를 본 뒤에 고치지 않는다 ("지휘자 추가"가 self-reviewer에 끌려가지 않도록).
   - 2-2. **입력 복사**: `subagent_in/<날짜>_<주제>/`를 만들고 대상 파일(판단에 필요한 주변 코드 포함)과 `REVIEW_GUIDELINES.md`를 명령으로 복사한다. 원본과 사본의 SHA-256이 같은지 확인한다. self-reviewer는 이 폴더 밖을 읽지 못한다 (훅).
   - 2-3. **self-reviewer 호출**: 위임 메시지에는 그 폴더 경로, 대상 파일·줄 범위, 변경 의도, 제약(사용자 결정 원문 포함)만 쓴다. 너의 의견·의심 지점·결론은 넣지 않는다 (`run_mode` input 규칙과 같다).
   - 2-4. 결과가 형식(`## 지적` · `## 자료 부족` · `## 읽은 파일`, `## 비고`는 선택)을 어겼거나, 중단됐거나, 비어 있거나, `## 읽은 파일`에 그 폴더 밖 경로가 있으면 **여기서 멈추고** 사용자에게 알린다. 다시 부르거나 네가 대신 쓸지는 사용자가 정한다.
   - 2-5. **보안 영역이면 security-reviewer도** (계획서 `Orchestration/HB_Orchestration/docs/plans/2026-10-23_서브에이전트-종류-추가.md`): 변경이 훅 · 경로 · 비밀 값 · 외부 전송 · 권한 검사 · 게임 클라이언트 신뢰(재화 · 세이브 · 패킷)에 걸리면 사용자에게 "security-reviewer도 함께 돌릴지" 한 줄로 묻는다 (자동으로 돌리지 않는다). 함께 돌리면 같은 `subagent_in/` 폴더로 self-reviewer와 **각자 따로** 호출하고 2-4 검사를 둘 다 한다.
3. `run_mode`를 호출한다. input: 변경 의도 + diff(필요한 최소). 리뷰는 input에 넣지 않는다.
   - `ownReview`: **서브에이전트 결과 원문만** 넣는다. 둘이면 self-reviewer 결과 뒤에 security-reviewer 결과를 그대로 이어 붙인다 (사이에 구분 줄 · 제목을 넣지 않는다 — H4가 '## 읽은 파일' 절의 '- ' 아닌 줄을 막는다).
   - `reason`: 맨 앞에 `[ownReview:self-reviewer]`(둘이면 `[ownReview:self-reviewer+security-reviewer]`)를 붙이고 부른 이유 한 줄.
   - 종합·판정에서 "Claude만 지적"은 두 서브에이전트가 찾고 외부가 놓친 것이다. 어느 서브에이전트의 지적인지 작업 로그에 적는다.
4. CLAUDE.md의 "결과 종합" 형식으로 최종 리뷰를 출력한다.
   - "Claude만 지적"은 self-reviewer가 찾고 외부가 놓친 것이다. 너만 찾은 것은 **"지휘자 추가"**로 따로 묶는다.
   - `record_verdicts`의 `claudeAlsoFound`는 **self-reviewer 결과에 있었는지**로 정한다. 지휘자 추가 항목은 `steps` 없이 남기고 `reason` 맨 앞에 `[지휘자 추가]`를 붙인다.
   - 작업 로그에 남긴다: self-reviewer 지적 수 · 오탐(이미 정한 것을 문제로 지적) 수 · 대상 코드를 다시 전부 읽었는지 (시범 성공 기준).
   - 끝나면 `subagent_in/<날짜>_<주제>/`를 지운다.
5. `record_verdicts`로 지적마다 판정을 남긴다 (CLAUDE.md "결과 종합" 5번).
6. 바로 고칠 수 있는 항목을 묻고, 승인하면 수정한다.
