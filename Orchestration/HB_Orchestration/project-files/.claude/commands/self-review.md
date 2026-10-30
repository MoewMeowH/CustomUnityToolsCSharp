---
description: Claude 지휘자와 검토형 리뷰어(기본 API 리뷰 sub_* — 코드 · perf · security · direction, 서브에이전트는 sub로 고르거나 크레딧 소진 때)가 독립적으로 리뷰한 뒤 결과를 종합한다 (다른 회사 외부 에이전트 없음)
argument-hint: [sub] [perf|security|direction|trace] [파일/커밋 범위 또는 계획서 · 지시서, 비우면 현재 미커밋 변경]
disable-model-invocation: true
---

인자: $ARGUMENTS
- 관점: 첫 단어(또는 `sub` 다음 단어)가 관점이면 그 리뷰어, 아니면 코드 리뷰.
  - (없음) → 모드 `sub_self` / 서브에이전트 **self-reviewer** (코드 리뷰)
  - `perf` → `sub_perf` / **perf-reviewer** (실행 비용·할당·동기화·규모 증가만)
  - `security` → `sub_security` / **security-reviewer** (입력 검증·경로·비밀 값·외부 전송·검사 우회·fail-closed만)
  - `direction` → `sub_direction` / **direction-reviewer** (요청 원문과 계획서의 방향만 — 대상은 계획서, 아래 "direction일 때")
  - `trace <지시서 경로>` → 서브에이전트 **evidence-collector**만 (API 모드 없음 — 요구마다 코드를 검색해야 함). 지시서 대조, 아래 "trace일 때"
- **기본은 API 리뷰**(`sub_*` — claude -p 읽기 전용 에이전트, HB_REVIEWER_API_KEY로 과금, 구독 사용량을 쓰지 않음): 아래 **"API 리뷰"** 절차. 첫 단어가 `sub`이면 서브에이전트(아래 1~5 본 절차).
- 나머지가 대상. 비어 있으면 `git diff HEAD`의 미커밋 변경.
- 아래 "self-reviewer"는 고른 리뷰어(API 리뷰면 `sub_*` 결과)로 읽는다.
- 다른 회사 외부 에이전트는 부르지 않는다. 외부 시선까지 필요하면 `/cross-review`. `sub_*`는 Claude(같은 회사)라 외부 시선이 아니다 — 서브에이전트 자리만 대신한다.

**API 리뷰** (기본): 2-2 ~ 2-4와 4만 바뀐다.
- 2-1은 같다 (지휘자 검토를 먼저 작업 로그에 저장).
- 2-2 대신: `subagent_in/`에 복사하지 않는다 (direction 제외 — 아래). `run_mode("sub_*", input, context, reason)` — input = 위임 메시지의 대상 설명 · 변경 의도 · 제약(사용자 결정 원문) — 검증 결과 · 결론 · 요청/계획서 원문은 넣지 않는다(CLAUDE.md "독립성"). context = diff 파일(변경이면 첫 항목, CLAUDE.md "diff는 파일로") + 대상 파일 (범위 = 검토의 중심). `sub_self` · `sub_perf` · `sub_security`는 서버가 저장소 추적 파일 사본을 작업 폴더에 깔고 에이전트가 Glob · Grep으로 정의 · 호출부 · 설정 · 시험을 직접 찾는다 — 지휘자 지시 · 규칙 · 계획서는 사본에서 빠지므로 그런 파일을 고친 변경이면 그 파일도 context로 넘긴다 (빠지면 결과 끝에 `(사본에서 뺀 변경 파일: …)`이 붙는다). 결과 끝에 `(저장소 사본 없음: …)`이 붙으면 고른 파일만 본 것이니 종합에서 알리고, 그때는 판단에 필요한 파일을 context에 넉넉히 넣어 다시 부를지 정한다. `sub_direction`은 context 파일만 받는다. 읽은 내용은 Anthropic으로 나가므로 비밀이 든 파일은 context에 넣지 않는다. 너의 의견 · 의심 지점은 넣지 않는다. 지침 파일은 서버가 시스템 프롬프트에 붙인다.
- 2-3: 서브에이전트를 부르지 않는다. 결과가 **`[실패] Quota`**(크레딧 소진 — 서버가 최근 1시간 안에 Quota였으면 호출 없이 바로 돌려줌)면 **묻지 않고** 같은 대상 · 같은 위임 내용으로 서브에이전트 본 절차로 다시 한다: 2-1 지휘자 검토는 이미 저장했으니 고치지 않고, 2-2 입력 복사(`subagent_in/` · SHA-256 확인 · `_SUBAGENT_MISTAKES.md`)부터 2-4까지 한 뒤 4는 `selfReview`로 기록한다. 결과 보고 · 작업 로그에 "크레딧 소진으로 서브에이전트 사용"을 적는다. 그 밖의 `[실패]`는 한 줄로 알리고 서브에이전트로 다시 할지 사용자에게 묻는다.
- 2-4: 결과 형식 검사는 같다. `## 읽은 파일`은 작업 폴더 기준 상대 경로다 (절대 경로 · `..`가 있으면 멈춤 — 작업 폴더는 실행 뒤 지워져 대조할 수 없다). `[경고] claude-cli 도구 사용 N건이 거절됨`이 붙으면 종합에서 알린다.
- 4: `record_verdicts`에 `selfReview` 대신 run_mode 결과의 **runId** (지적은 `steps: [1]`). 나머지 규칙 같음.
- 대시보드 첫 화면에 API 리뷰 이번 달 횟수 · 비용과 크레딧 소진 상태가 보인다.

**trace일 때** (지시서 대조 — 사용자가 `/work-order human` 방향서대로 직접 짠 코드): 결함 리뷰와 따로, 요구 사항마다 코드에 있는지만 본다.
- 대상: 인자의 지시서(`구현: 사용자` 계획서)와 그 작업의 변경(지시서 뒤를 비우면 `git diff HEAD`, 커밋 범위를 주면 그 범위 — 새 파일은 `git add -N` 뒤).
  - 지시서 경로가 없거나, 그 파일에 `구현: 사용자` · `## 요구 사항` 절이 없으면 어느 지시서인지 한 줄로 묻고 멈춘다 (요구 목록 없이 대조하지 않는다).
- 2-1 지휘자 검토 대신: 요구 사항(R1…)마다 네가 먼저 찾은 위치 · 상태(아래 수집기 상태 다섯 가지 어휘로)를 그 지시서의 짝 작업 로그(`docs/worklog/<지시서와 같은 이름>.md`, 없으면 만들고 첫 줄에 지시서 링크)에 다 쓴다 (결과를 본 뒤 고치지 않는다).
- 2-2 입력: `subagent_in/<날짜>_<주제>/`에
  - `requirements.md` = 지시서의 `## 요구 사항` 절 **그대로만** — 대조 기준.
  - `spec.md` = 지시서의 `## 기획 요약` 절 그대로 — 요구 문장의 값 · 용어를 해석할 때만 쓰는 자료 (P4의 `request.md`와 같은 자리). 설계 제안 · 주의할 점 · 연결 위치 후보는 넣지 않는다 — "계획대로 짰나"로 쏠리지 않게.
  - diff 파일, 저장소 사본 `repo/`(위 2-2와 같은 스크립트 · `[주의]`면 `target/`), `_SUBAGENT_MISTAKES.md`(evidence-collector 항목이 있으면). SHA-256 확인.
- 2-3: evidence-collector를 부르고, 위임 메시지에는 폴더 경로와 "requirements.md의 R 번호마다 대조 · `repo/`에서 직접 찾기"만 쓴다.
- 2-4: 출력이 표 형식(요구 번호 · 요약 · 근거 위치 · 상태 · 메모) + `## 읽은 파일` + `## 요약`을 어겼거나 읽은 파일이 폴더 밖이면 멈추고 알린다.
- 3 종합: 요구마다 네 조사와 수집기 결과를 맞춰 정리한다. 수집기 상태 → 종합:
  - `정적 확인` → **있음**
  - `실행 확인 필요` → **있음 (실행 확인)** — 필요한 테스트를 할 일로
  - `다름` → **일부** — 무엇이 다른지를 할 일로
  - `못 찾음` → **없음** — 찾아본 곳과 함께 할 일로
  - `자료 부족` → **모름** — 필요한 자료(씬 · 프리팹 · 설정 등)를 할 일로 (빠뜨리지 않는다)
  - 네 조사와 수집기가 다르면 코드를 다시 보고 근거를 나란히 적는다. 기획 누락이 의심되면(요구는 있는데 기획에 근거가 없음 등) 따로 적는다.
- 4 기록: record_verdicts에 남기지 않는다 (결함 지적이 아니라 대조표) — 작업 로그(그 지시서의 짝 로그)에만 표와 종합을 남긴다.
- 끝나면 `subagent_in/<날짜>_<주제>/`를 지운다.

**direction일 때**: 계획서를 쓴 뒤 사용자 승인 **전**에 지휘자가 제안하고, 사용자가 이 명령을 입력했을 때 돈다 (자동으로 부르지 않는다).
- 2-2에서 `request.md`(이 작업에 대한 사용자 메시지 **원문 그대로**, 지휘자 요약 없이)와 계획서를 복사한다. 코드는 넣지 않는다 (필요하면 판단에 쓰인 자료만). API 리뷰(`sub_direction`)도 `request.md`는 `subagent_in/<날짜>_<주제>/`에 만든 뒤 그 파일과 계획서를 context(파일 전체 범위)로 넘긴다 — 요청 원문을 input에 다시 쓰지 않는다.
- 2-1 지휘자 검토와 3 종합은 "요청 누락 · 범위 확장 · 더 단순한 길 · 되돌리기 어려운 결정 · 사용자에게 물을 것" 기준. 채택한 지적은 계획서 본문을 고치지 않고 사용자에게 보고해 `## 결정`으로 반영한다.

1. diff가 있으면 대상 diff와 변경 의도를 파악한다. 의도가 불분명하면 커밋 메시지·주변 코드로 추론하고, 그래도 모르면 한 줄로 질문한다. / diff가 없으면(기존 코드 점검) 변경 의도 대신 "이 코드의 역할과 점검 목적"을 쓴다.
2. **자체 검토** — `/cross-review` 2단계와 같다. 순서를 바꾸지 않는다.
   - 2-1. **지휘자 검토를 먼저 저장**: REVIEW_GUIDELINES.md 기준으로 너의 리뷰를 완성해 작업 로그에 다 쓴다. self-reviewer 결과를 본 뒤에 고치지 않는다.
   - 2-2. **입력 복사**: `subagent_in/<날짜>_<주제>/`를 만들고 diff 파일(변경이면, CLAUDE.md "diff는 파일로")과 `REVIEW_GUIDELINES.md`를 명령으로 복사한다. 원본과 사본의 SHA-256이 같은지 확인한다. self-reviewer는 이 폴더 밖을 읽지 못한다 (훅). 부르는 서브에이전트의 `.claude/mistakes/<이름>.md`에 항목(주석 밖 `- ` 줄)이 있으면 `_SUBAGENT_MISTAKES.md`로 함께 복사한다 (CLAUDE.md "서브에이전트").
     - **저장소 사본** (diff + 직접 찾기): `python -I .claude/scripts/subagent_copy_tracked.py --root <저장소 루트> --out subagent_in/<날짜>_<주제>/repo [--path <대상 프로젝트 폴더>]... [--diff <diff 파일>]`로 대상 저장소의 git 추적 파일 사본을 만든다. 지휘자 지시 · 실수 · 프로젝트 규칙 파일, `.claude` 등, 리뷰 임시 폴더, 계획서 · 작업 로그, 비밀 모양 · 이진 · 큰 파일 · 링크는 스크립트가 뺀다. 유니티 저장소(`Assets/` + `ProjectSettings/`)는 코드 파일만 + diff에 든 파일이다. 프로젝트가 여럿인 저장소는 `--path`로 대상 프로젝트 폴더만 (PROJECT.md 목록). 출력 요약(사본 수 · 뺀 것)을 작업 로그에 적는다.
     - 출력에 `[주의] diff에 든 파일 N개가 사본에 없음`이 있으면 그 파일 중 리뷰 대상을 `subagent_in/<날짜>_<주제>/target/`에 따로 복사한다 (SHA-256 확인). `.claude/` 아래는 `.claude-files/`처럼 폴더 이름을, `CLAUDE.md`는 `CLAUDE.md.txt`로 바꾼다 (H6). 비밀 모양 파일은 넣지 않는다. 위임 메시지에 `target/`의 원래 경로를 적는다.
     - 스크립트가 실패하면(git 없음 · 상한 2만 개 · 64MB 넘음) 대상 파일(판단에 필요한 정의 · 호출부 · 설정 · 시험 포함)을 골라 복사하는 예전 방식으로 한다.
   - 2-3. **self-reviewer 호출**: 위임 메시지에는 그 폴더 경로, 대상 파일·줄 범위, 변경 의도, 제약(사용자 결정 원문 포함)만 쓴다. 너의 의견·의심 지점·결론은 넣지 않는다. 사본이 있으면 "`repo/`는 대상 저장소 추적 파일 사본 — 중심은 diff, 판단에 필요한 정의 · 호출부 · 설정 · 시험은 `repo/` 안에서 Grep · Glob으로 직접 찾는다"를 한 줄 쓴다.
   - 2-4. 결과가 형식(`## 지적` · `## 자료 부족` · `## 읽은 파일`, `## 비고`는 선택)을 어겼거나, 중단됐거나, 비어 있거나, `## 읽은 파일`에 그 폴더 밖 경로가 있으면 **여기서 멈추고** 사용자에게 알린다. 다시 부르거나 네가 대신 쓸지는 사용자가 정한다.
3. 결과를 종합한다.
   - **공통**: self-reviewer와 지휘자가 같이 찾은 것 — 우선 처리.
   - **self-reviewer만**: 코드를 다시 확인하고 타당/부당 판정과 이유. `## 자료 부족`은 로컬에서 확인해 답하거나, 확인 못 한 것을 사용자에게 알린다.
   - **지휘자 추가**: 너만 찾은 것.
   - 작업 로그에 남긴다: 판정과 이유 · self-reviewer 지적 수 · 오탐(이미 정한 것을 문제로 지적) 수 · 대상 코드를 다시 전부 읽었는지.
   - 끝나면 `subagent_in/<날짜>_<주제>/`를 지운다.
4. `record_verdicts`로 판정을 남긴다 (대시보드 판정 화면 — 서브에이전트별 채택률·놓침).
   - `runId`는 빼고 `selfReview`를 준다: `review` = 서브에이전트 결과 **원문 그대로**, `agent` = 고른 서브에이전트 이름(self-reviewer면 생략 가능), `intent` = 위임 메시지의 변경 의도·제약, `files`, `reason`, 작업 완료 알림의 `subagentTokens`·`toolUses`·`durationMs`. 서버가 실행 1건을 만들고 결과 첫 줄에 runId를 준다.
   - 판정할 지적이 하나도 없으면(self-reviewer·지휘자 모두 0) 기록하지 않는다. 같은 원문을 다시 보내면 서버가 앞 runId를 알려 주며 거절한다 (응답을 못 받아 다시 불러도 두 번 세지지 않음).
   - self-reviewer 지적: `steps: [1]`, `claudeAlsoFound`는 지휘자 검토에도 있었는지. 지휘자 추가: `steps` 없이, `reason` 맨 앞에 `[지휘자 추가]`.
   - `verdict`: adopted · partial · rejected · deferred · duplicate, 채택이 아니면 `reason` 필수. 하나라도 틀리면 실행 기록도 남지 않으니 고쳐 다시 부른다.
   - 보류·사용자 판정 뒤집기는 받은 runId로 같은 `findingKey`를 다시 쓴다 (`selfReview` 없이).
5. 바로 고칠 수 있는 항목을 묻고, 승인하면 수정한다.

## 서브에이전트 공통 규칙

서브에이전트(Claude Code `Agent`)를 부르는 모든 경우(이 명령 · `/cross-review` · `/mode` perf_* · research · `/preflight`)에 적용한다.

* 정의: `.claude/agents/`
  * 검토형(Opus, `subagent_in/` 격리 · 같은 출력 형식): `self-reviewer`(코드 리뷰 — `/self-review` · `/cross-review` 2단계), `perf-reviewer`(성능 — `/mode` perf_* ownReview · `/self-review perf`), `security-reviewer`(보안 — `/self-review security` · 보안 영역 `/cross-review`는 사용자에게 묻고 self-reviewer와 함께), `direction-reviewer`(요청 원문 대 계획서 방향 — 계획서를 쓴 뒤 승인 전에 `/self-review direction`을 **제안만**).
  * 검토형은 기본이 API 리뷰(모드 `sub_self` · `sub_perf` · `sub_security` · `sub_direction`, 위 "API 리뷰"). 서브에이전트는 `/self-review sub …`로 고르거나 `[실패] Quota`(크레딧 소진) 때 대신 쓴다.
  * 수집형: `web-searcher`(Sonnet, WebSearch · WebFetch만 — research 2단계 · crash 사전 조사 검색), `evidence-collector`(Opus, `/preflight` P4 근거 수집 · `/self-review trace` 지시서 대조).
  * 모델: 맥락 편향 없이 봐야 하는 자리는 `opus`, 지휘자 컨텍스트 보호·반복 작업(조사·수집·추출)은 `sonnet`.
  * 정의 파일에는 `tools` 허용 목록이 반드시 있어야 한다 (생략하면 MCP 도구까지 상속 → `run_mode`를 부를 수 있음). `python -I .claude/scripts/check_agents.py`로 검사한다. `permissionMode` bypass 계열 금지.
* 입력: 대상 파일을 `subagent_in/<날짜>_<주제>/`에 **명령으로 복사**하고 SHA-256을 확인한 뒤 그 경로만 넘긴다. 리뷰 대상이 변경이면 diff 파일(CLAUDE.md "diff는 파일로")도 함께 복사한다. 리뷰 지침이 필요하면 `REVIEW_GUIDELINES.md`도 복사한다 (`omitClaudeMd`라 CLAUDE.md·지침이 자동으로 들어가지 않음).
  * 검토형(self · perf · security)은 대상 파일을 고르는 대신 저장소 추적 사본 `repo/`를 준다 (`subagent_copy_tracked.py`, 위 2-2 — diff + 직접 찾기). direction은 그대로 요청 원문 · 계획서만.
  * 읽기 차단 훅(`.claude/scripts/subagent_read_guard.py`)이 `subagent_in/` 밖을 막는다. 이전 리뷰·작업 로그·호출 기록을 넣지 않는다.
  * `CLAUDE.md`가 검토 대상이면 `CLAUDE.md.txt`처럼 이름을 바꿔 복사한다 (그대로 두면 지휘자 규칙이 서브에이전트에 자동으로 실린다 — H6이 막음). 링크가 아니라 내용을 복사한다. 부르기 전에 복사를 끝내고, 서브에이전트가 도는 동안에는 `subagent_in/`에 복사하지 않는다 (H6은 부르는 순간만 본다).
  * 그 서브에이전트의 `.claude/mistakes/<이름>.md`에 항목(주석 밖 `- ` 줄)이 있으면 `_SUBAGENT_MISTAKES.md`로 함께 복사한다 (검토 대상인 프로젝트 `MISTAKES.md`와 겹치지 않는 이름). web-searcher는 파일을 못 읽으므로 주석 밖 `- ` 줄만 위임 메시지 끝 `## 자주 하는 실수` 절로 붙인다 (`/record-rules` "실수 기록").
  * 위임 메시지에는 대상·의도·제약만 쓴다. 너의 결론·의심 지점·검증 결과는 넣지 않는다 (CLAUDE.md "input — 독립성").
  * `subagent_in/`은 프로젝트마다 `.gitignore`에 등록한다. 끝나면 지운다.
* 결과가 정해진 형식을 어겼거나, 중단됐거나, `## 읽은 파일`에 폴더 밖 경로가 있으면 그 단계에서 멈추고 사용자에게 알린다. 다시 부르거나 대신 쓸지는 사용자가 정한다.
