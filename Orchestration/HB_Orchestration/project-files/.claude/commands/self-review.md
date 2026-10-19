---
description: Claude 지휘자와 self-reviewer 서브에이전트가 독립적으로 리뷰한 뒤 결과를 종합한다 (외부 에이전트 없음)
argument-hint: [파일/커밋 범위, 비우면 현재 미커밋 변경]
---

인자: $ARGUMENTS
- 대상. 비어 있으면 `git diff HEAD`의 미커밋 변경.
- 외부 에이전트(`run_mode`)는 부르지 않는다. 외부 시선까지 필요하면 `/cross-review` (계획서 `Orchestration/HB_Orchestration/docs/plans/2026-10-20_리뷰조건-self-reviewer.md`).

1. diff가 있으면 대상 diff와 변경 의도를 파악한다. 의도가 불분명하면 커밋 메시지·주변 코드로 추론하고, 그래도 모르면 한 줄로 질문한다. / diff가 없으면(기존 코드 점검) 변경 의도 대신 "이 코드의 역할과 점검 목적"을 쓴다.
2. **자체 검토** — `/cross-review` 2단계와 같다. 순서를 바꾸지 않는다.
   - 2-1. **지휘자 검토를 먼저 저장**: REVIEW_GUIDELINES.md 기준으로 너의 리뷰를 완성해 작업 로그에 다 쓴다. self-reviewer 결과를 본 뒤에 고치지 않는다.
   - 2-2. **입력 복사**: `subagent_in/<날짜>_<주제>/`를 만들고 대상 파일(판단에 필요한 주변 코드 포함)과 `REVIEW_GUIDELINES.md`를 명령으로 복사한다. 원본과 사본의 SHA-256이 같은지 확인한다. self-reviewer는 이 폴더 밖을 읽지 못한다 (훅).
   - 2-3. **self-reviewer 호출**: 위임 메시지에는 그 폴더 경로, 대상 파일·줄 범위, 변경 의도, 제약(사용자 결정 원문 포함)만 쓴다. 너의 의견·의심 지점·결론은 넣지 않는다.
   - 2-4. 결과가 형식(`## 지적` · `## 자료 부족` · `## 읽은 파일`)을 어겼거나, 중단됐거나, 비어 있거나, `## 읽은 파일`에 그 폴더 밖 경로가 있으면 **여기서 멈추고** 사용자에게 알린다. 다시 부르거나 네가 대신 쓸지는 사용자가 정한다.
3. 결과를 종합한다.
   - **공통**: self-reviewer와 지휘자가 같이 찾은 것 — 우선 처리.
   - **self-reviewer만**: 코드를 다시 확인하고 타당/부당 판정과 이유. `## 자료 부족`은 로컬에서 확인해 답하거나, 확인 못 한 것을 사용자에게 알린다.
   - **지휘자 추가**: 너만 찾은 것.
   - 작업 로그에 남긴다: 판정과 이유 · self-reviewer 지적 수 · 오탐(이미 정한 것을 문제로 지적) 수 · 대상 코드를 다시 전부 읽었는지.
   - 끝나면 `subagent_in/<날짜>_<주제>/`를 지운다.
4. `record_verdicts`로 판정을 남긴다 (대시보드 판정 화면 — 에이전트 `self-reviewer`의 채택률·놓침).
   - `runId`는 빼고 `selfReview`를 준다: `review` = self-reviewer 결과 **원문 그대로**, `intent` = 위임 메시지의 변경 의도·제약, `files`, `reason`, 작업 완료 알림의 `subagentTokens`·`toolUses`·`durationMs`. 서버가 self-review 실행 1건을 만들고 결과 첫 줄에 runId를 준다.
   - 판정할 지적이 하나도 없으면(self-reviewer·지휘자 모두 0) 기록하지 않는다. 같은 원문을 다시 보내면 서버가 앞 runId를 알려 주며 거절한다 (응답을 못 받아 다시 불러도 두 번 세지지 않음).
   - self-reviewer 지적: `steps: [1]`, `claudeAlsoFound`는 지휘자 검토에도 있었는지. 지휘자 추가: `steps` 없이, `reason` 맨 앞에 `[지휘자 추가]`.
   - `verdict`: adopted · partial · rejected · deferred · duplicate, 채택이 아니면 `reason` 필수. 하나라도 틀리면 실행 기록도 남지 않으니 고쳐 다시 부른다.
   - 보류·사용자 판정 뒤집기는 받은 runId로 같은 `findingKey`를 다시 쓴다 (`selfReview` 없이).
5. 바로 고칠 수 있는 항목을 묻고, 승인하면 수정한다.
