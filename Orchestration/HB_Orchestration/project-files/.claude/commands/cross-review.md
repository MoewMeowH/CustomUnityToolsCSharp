---
description: Claude와 외부 에이전트가 독립적으로 리뷰한 뒤 결과를 종합한다
argument-hint: [main|high] [파일/커밋 범위, 비우면 현재 미커밋 변경]
---

인자: $ARGUMENTS
- 첫 단어가 `high`면 `review_high`(sol · DeepSeek 동시), `main`이거나 없으면 `review_main`. (예전 인자 `panel`은 `high`로 읽는다.)
- 나머지가 대상. 비어 있으면 `git diff HEAD`의 미커밋 변경.

1. diff가 있으면 대상 diff와 변경 의도를 파악한다. 의도가 불분명하면 커밋 메시지·주변 코드로 추론하고, 그래도 모르면 한 줄로 질문한다. / diff가 없으면(기존 코드 점검) 변경 의도 대신 "이 코드의 역할과 점검 목적"을 쓰고, 파일 전체가 아니라 핵심 클래스·메서드 위주로 추려서 넘긴다.
2. **자체 검토** — 순서를 바꾸지 않는다.
   - **기본은 API 리뷰**(`/self-review` "API 리뷰"와 같다). 2-1 · 2-4 · 2-5는 두 갈래 공통이고, 2-2 · 2-3만 서브에이전트일 때다.
     - API 갈래: 2-1 지휘자 검토 저장 → 2-5 보안 영역이면 사용자에게 묻기 → 2-2 · 2-3 대신 `run_mode("sub_self")`(함께 돌리기로 했으면 `sub_security`도 따로)를 context로 → 2-4 형식 검사 (`## 읽은 파일`은 작업 폴더 기준 상대 경로, 절대 경로 · `..`가 있으면 멈춤).
     - 아래 3의 ownReview에는 그 결과의 **응답 본문**(`## 지적`부터 끝까지, 서버가 붙인 `runId:` · `### 1.` 머리 줄과 `[경고]` 줄은 뺀다)을 넣고, reason 태그는 `[ownReview:sub_self]`(둘이면 `[ownReview:sub_self+sub_security]`). H4가 상대 경로로 검사한다.
     - `[실패] Quota`면 묻지 않고 서브에이전트 본 절차(2-2 · 2-3)로 하고 "크레딧 소진으로 서브에이전트 사용"을 적는다. 둘을 돌리면 둘 다 서브에이전트로 한다 (태그에 섞으면 H4가 막는다). 사용자가 서브에이전트를 고르면 본 절차.
   - API 리뷰 결과는 그 `sub_*` 실행(runId)으로도 남지만 판정은 외부 실행(review_*)에만 단다 — 같은 지적을 두 번 세지 않게. `sub_*` 실행은 대시보드에 "판정 없는 실행"으로 보인다. **외부 `run_mode`가 실행되지 않았으면**(3단계 끝) `selfReview`를 새로 만들지 말고 그 `sub_*` runId에 `record_verdicts`(그 리뷰어의 지적은 `steps: [1]`, 지휘자 추가는 `steps` 없이)로 단다.
   - 2-1. **지휘자 검토를 먼저 저장**: REVIEW_GUIDELINES.md 기준으로 너의 리뷰를 완성해 작업 로그에 다 쓴다. self-reviewer 결과를 본 뒤에 고치지 않는다 ("지휘자 추가"가 self-reviewer에 끌려가지 않도록).
   - 2-2. **입력 복사**: `subagent_in/<날짜>_<주제>/`를 만들고 diff 파일(변경이면, CLAUDE.md "diff는 파일로") · 대상 파일(판단에 필요한 주변 코드 포함)과 `REVIEW_GUIDELINES.md`를 명령으로 복사한다. 원본과 사본의 SHA-256이 같은지 확인한다. self-reviewer는 이 폴더 밖을 읽지 못한다 (훅). 부르는 서브에이전트의 `.claude/mistakes/<이름>.md`에 항목(주석 밖 `- ` 줄)이 있으면 `_SUBAGENT_MISTAKES.md`로 함께 복사한다 (CLAUDE.md "서브에이전트").
   - 2-3. **self-reviewer 호출**: 위임 메시지에는 그 폴더 경로, 대상 파일·줄 범위, 변경 의도, 제약(사용자 결정 원문 포함)만 쓴다. 너의 의견·의심 지점·결론은 넣지 않는다 (`run_mode` input 규칙과 같다).
   - 2-4. 결과가 형식(`## 지적` · `## 자료 부족` · `## 읽은 파일`, `## 비고`는 선택)을 어겼거나, 중단됐거나, 비어 있거나, `## 읽은 파일`에 그 폴더 밖 경로가 있으면 **여기서 멈추고** 사용자에게 알린다. 다시 부르거나 네가 대신 쓸지는 사용자가 정한다.
   - 2-5. **보안 영역이면 security-reviewer도**: 변경이 훅 · 경로 · 비밀 값 · 외부 전송 · 권한 검사 · 게임 클라이언트 신뢰(재화 · 세이브 · 패킷)에 걸리면 사용자에게 "security-reviewer도 함께 돌릴지" 한 줄로 묻는다 (자동으로 돌리지 않는다). 함께 돌리면 같은 `subagent_in/` 폴더로 self-reviewer와 **각자 따로** 호출하고 2-4 검사를 둘 다 한다.
3. `run_mode`를 호출한다. input: 변경 의도 · 제약(CLAUDE.md "독립성" — 검증 결과 · 결론 · 요청/계획서 원문은 넣지 않는다). context: diff 파일(CLAUDE.md "diff는 파일로")을 첫 항목으로 + 판단에 필요한 파일. 리뷰는 input에 넣지 않는다.
   - `ownReview`: **서브에이전트 결과 원문만** 넣는다. 둘이면 self-reviewer 결과 뒤에 security-reviewer 결과를 그대로 이어 붙인다 (사이에 구분 줄 · 제목을 넣지 않는다 — H4가 '## 읽은 파일' 절의 '- ' 아닌 줄을 막는다).
   - `reason`: 맨 앞에 `[ownReview:self-reviewer]`(둘이면 `[ownReview:self-reviewer+security-reviewer]`)를 붙이고 부른 이유 한 줄.
   - 종합·판정에서 "Claude만 지적"은 두 서브에이전트가 찾고 외부가 놓친 것이다. 어느 서브에이전트의 지적인지 작업 로그에 적는다.
   - API 리뷰였으면 `ownReview`는 2단계의 응답 본문, `reason` 태그는 `[ownReview:sub_self]` · `[ownReview:sub_self+sub_security]`.
   - **`run_mode`가 실행되지 않았으면** (응답이 안전 필터에 막힘 · `[`로 시작하는 실패 · 사용자가 외부 없이 종합을 고름) API 리뷰였으면 2단계 규칙대로 `sub_*` runId에 판정을 단다. 서브에이전트였으면 그 결과가 ownReview로 남지 않는다. 종합 뒤 서브에이전트마다 `record_verdicts`를 `selfReview`(`agent`에 그 이름, `review`에 그 결과 원문)로 **따로** 부르고, `selfReview.reason` 맨 앞에 `[외부 미실행]`을 붙인다. 판정은 그 서브에이전트가 낸 지적만 그 실행에 단다 (`steps` [1]). 지휘자 추가는 self-reviewer 실행에 `steps` 없이. 원문이 막혀 못 넣으면 고쳐 쓰지 말고 멈추고 보고한다 — 대시보드 '서브에이전트 결과'에 원문이 "기록 없음"으로 남아 있다.
4. `/mode` "결과 종합" 형식으로 최종 리뷰를 출력한다.
   - "Claude만 지적"은 self-reviewer가 찾고 외부가 놓친 것이다. 너만 찾은 것은 **"지휘자 추가"**로 따로 묶는다.
   - `record_verdicts`의 `claudeAlsoFound`는 **self-reviewer 결과에 있었는지**로 정한다. 지휘자 추가 항목은 `steps` 없이 남기고 `reason` 맨 앞에 `[지휘자 추가]`를 붙인다.
   - 작업 로그에 남긴다: self-reviewer 지적 수 · 오탐(이미 정한 것을 문제로 지적) 수 · 대상 코드를 다시 전부 읽었는지 (시범 성공 기준).
   - 끝나면 `subagent_in/<날짜>_<주제>/`를 지운다.
5. `record_verdicts`로 지적마다 판정을 남긴다 (`/mode` "결과 종합" 5번).
6. 바로 고칠 수 있는 항목을 묻고, 승인하면 수정한다.
