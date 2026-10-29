# 골든 세트 (회귀 평가)

[← README](../README.md)

## 골든 세트 (`--golden`) — 프롬프트 · 모드 · 모델 변경 회귀 평가

정답이 알려진 사례 묶음을 같은 입력으로 다시 돌려, 두 가지를 숫자로 비교한다.

- **찾아야 할 지적을 몇 개 찾았는지**
- **과거에 기각된 지적을 다시 내는지**

역할 프롬프트 · modes.json · 모델을 바꾼 뒤에 쓴다. MCP 서버를 띄우지 않는 일회성 실행이다.

```
dotnet <publish 폴더>\HB.Orchestration.Mcp.dll --golden run [사례|all] [--mode 모드] [--agents from=to,...] [--reasoning 에이전트=강도,...] [--dir 폴더] [--label 이름] [--no-guidelines] [--yes]
dotnet <publish 폴더>\HB.Orchestration.Mcp.dll --golden score <사례> --from <파일> [--dir 폴더] [--label 이름]
```

### run

- `--yes`가 없으면 계획만 보이고 **외부 호출 없이** 멈춘다. 계획: 사례 · 에이전트 · 입력 글자 수 · 입력 비용 추정 · 붙는 지침 파일.
- 하루 비용 상한은 없다. 계획의 예상 비용을 보고 `--yes`로 돌린다.
- run_mode와 같은 시스템 프롬프트(역할 + 출력 형식 + 공통 규칙 + 지침) · 에이전트로 보낸다.
- reason · ownReview · context 첨부는 쓰지 않는다. 첨부는 사례의 `input.md`에 이미 들어 있다.
- 단계가 하나이고 구조화 출력(`Output`)이 있는 모드만 채점된다.
- 호출 기록에는 `Tool = golden`, `Mode = 원래 모드`로 남는다 (대시보드 · 비용 집계에 보임). 세션 상태 파일은 남기지 않는다.

### score

self-reviewer 결과(마크다운 `- C1 [major] 파일:줄 — 문제`)나 findings JSON(@1 · @2)을 채점만 한다. 외부 호출은 없다.

### 지침 찾기

run_mode와 같은 순서로 찾는다.

1. `Orchestration:GuidelinesPath`
2. `REVIEW_GUIDELINES_PATH`
3. **현재 폴더**의 `REVIEW_GUIDELINES.md`

- 지침을 쓰는 역할인데 못 찾으면, 계획을 보인 뒤 거절한다 (외부 호출 없음). 프로젝트 루트에서 실행하거나 경로를 지정한다.
- `--no-guidelines`는 파일이 있어도 지침을 빼고 돈다. 지침 있음 · 없음 비교용이고, 기록 사유는 `골든 --no-guidelines`.
- 사례를 돌기 직전마다 지침을 다시 읽는다. 실행 전 확인과 다르면(파일이 바뀜 · 사라짐) 그 사례부터 멈춘다.
- 결과 줄에 `guidelinesAttached` · `guidelinesSha256` · `guidelinesReason`이 남는다.

### 임시 modes · 지침 경로

골든 · 하네스에서 `HB_MODES_PATH` · `REVIEW_GUIDELINES_PATH`로 임시 modes · 지침을 넘길 때는 **절대 경로만** 쓴다.

- 까닭: 상대 경로는 그 프로세스의 현재 폴더 기준이라, 다른 폴더에서 실행하면 엉뚱한 곳을 가리킨다.
- 지정한 경로에 파일이 없으면 서버는 다음 후보로 넘어가지 않고 오류다 (modes.json과 리뷰 지침 모두).
- 골든은 계획 미리보기에서 `[리뷰 지침 경로 없음]`으로 거절한다 (`--no-guidelines`면 막지 않음).

### 하네스 회차 실행 (`run_golden.sh`)

```bash
tools/golden/run_golden.sh <라벨> <모드> [건너뛸 사례 ...]
```

- 리뷰 사례(rec-* · seed-*)만 돈다. 저장소 루트 · Publish_Output dll 기준이다.
- 지정한 `HB_MODES_PATH` · `REVIEW_GUIDELINES_PATH`가 절대 경로가 아니거나 파일이 없으면 시작 전에 종료 2.
- 골든 종료 코드에 따라:
  - 2(실행 전 거절) · 3(Quota): 그 사례에서 멈춘다.
  - 1(사례 실패): 계속하고, 끝에 `[실패 사례 N개]`와 종료 1.
- 시험: `bash tools/golden/test_run_golden.sh` (가짜 dotnet, 외부 호출 없음).

### 폴더 · 종료 코드

- 폴더: `--dir` 또는 appsettings `Orchestration:Golden:Folder`.
- 사례 하나라도 형식이 틀리면 아무것도 돌리지 않는다.

| 종료 코드 | 뜻 |
|---|---|
| 0 | 성공 |
| 1 | 일부 사례 실패 |
| 2 | 사용법 · 사례 형식 오류 |
| 3 | 크레딧 · 한도 소진(Quota)으로 남은 사례를 멈춤 |

### Quota

429 중 `insufficient_quota` · `credit_balance_exhausted`(메시지나 응답 본문)는 `ErrorKind.Quota`로 분류해 재시도하지 않는다.

```
[실패] Quota — 크레딧/한도 소진, 충전 전까지 재실행 금지
```

- 골든은 Quota가 나오면 남은 사례를 돌리지 않는다.
- 다단계(crash) 사례는, 단계 제공사 중 하나라도 **가장 최근 판정 가능한 호출 기록이 Quota**면 앞 단계도 호출하지 않는다.
  - 판정 가능한 기록 = 7일 안의 성공 또는 Quota. Timeout · Network 등은 건너뛴다.
- 풀기: 충전 뒤 **그 제공사로 가는** 작은 호출 1회로 성공 기록을 남긴다 (그 제공사 에이전트의 리뷰 사례, 또는 `--agents`로 연결).
- 판정은 OpenAI 문구 기준이다. Gemini 한도 소진(RESOURCE_EXHAUSTED)은 지금도 RateLimit으로 재시도된다.

## 사례 폴더

`<골든 폴더>/<사례 id>/`. `results/`와 `_`로 시작하는 폴더는 사례가 아니다.

| 파일 | 내용 |
|---|---|
| `case.json` | `id` · `kind` · `mode` · `source` · `description` (아래) |
| `input.md` | 에이전트에게 보낼 프롬프트 원문 (그대로 보냄) |
| `expected.json` | 기대 지적 목록 (아래) |
| `traps.json` | (선택) 과거에 기각된 지적. 같은 형식 + `why`(기각 이유) |

**`case.json`**

| 필드 | 값 |
|---|---|
| `id` | 폴더 이름과 같게 |
| `kind` | `record` 판정 기록 · `seeded` 고친 결함을 되돌린 코드 · `crash` 원인이 알려진 크래시 · `replay` 과거 검토형 리뷰 복원 |
| `mode` | 기본 review_main. 예전 이름은 새 이름으로 읽는다 |
| `source` · `description` | 출처 · 설명 |

**`expected.json`**

```
[{ id, summary, files, lines: [시작, 끝], locations?, required?, keywords, severity, origin? }]
```

| 필드 | 뜻 |
|---|---|
| `locations` | `[{ file, from, to }]` 여러 곳 (원인 · 증상 지점). 하나라도 맞으면 위치 일치. 비우면 files × lines |
| `required` | 그 결함에만 맞는 말 (동의어 묶음, 하나라도 맞으면 됨). 있으면 `keywords`는 보조 |
| `severity` | critical · major · minor · nit |
| `origin` | 정답 출처: record · later-commit · self-review · conductor · common · ds · sol · seeded (모델 비교 집합용) |

## 채점 규칙

`GoldenScorer`, `golden-scorer@2` (결과 줄 `scorerVersion`). LLM 판정자는 없다.

### 짝 후보와 강도

짝 후보가 되는 조건:

- 같은 **파일 이름** + 줄이 위치 중 하나의 범위 ±5, 또는
- **필수 키워드**가 문제 · 상세에 있음 (단 지적이 다른 파일을 가리키면 제외)

| 강도 | 조건 |
|---|---|
| 3 | 위치 + 필수 키워드 둘 다 |
| 2 | 필수 키워드만 |
| 1 | 줄만, 또는 보조 키워드만 |

- `required`가 비면 `keywords`가 필수 역할을 한다 (예전 형식).
- **찾음은 강도 2 이상만** 센다.
  - 줄만 맞은 짝(강도 1)은 같은 줄 범위의 다른 결함과 구분하지 못한다. "줄만 맞음 — 확인 필요"로만 보인다.
  - 맞는 지적이면 정답지 키워드를 보강한다.

### 키워드 검사

- 대소문자 무시 부분 문자열이다.
- 경계: 키워드 첫 글자가 영숫자면 그 앞, 끝 글자가 영숫자면 그 뒤도 영숫자가 아니어야 한다 (`NUL`이 `null`에 걸리지 않게).
- 끝의 `*`는 **항상** 앞부분 일치다 (`ArgumentOutOfRange*` → `ArgumentOutOfRangeException`).
  - 그래서 `char*`처럼 `*`로 끝나는 말 자체는 쓸 수 없다 (`char`의 앞부분 일치가 된다, 이스케이프 없음).
- 한국어는 그대로 부분 문자열이다.
- 형식 오류: 빈 말 · 앞뒤 공백이 있는 키워드 (공백이 경계 검사를 없앤다).
- crash 사례는 예전 검사(왼쪽 경계만) 그대로다. `required` · `locations`는 형식 오류.

### 배정 순서

기대 지적 하나에 짝 하나, 지적 하나도 한 번만 쓴다.

1. 강도 2 이상 짝을 최대 가중 배정(헝가리안)으로 정한다. 사전식으로 비교한다:
   1. 배정 전체의 찾음 수
   2. 강도 합
   3. 필수 키워드가 지적 제목(problem)에 있는 짝 수
   4. 앞 지적 순
   - 자리값을 항목 · 지적 수에서 계산해 합에서도 뒤집히지 않게 한다. long을 넘을 크기면 오류다.
2. 남은 지적을 함정과 대조한다 (강도 2 이상).
3. 마지막으로, 남은 기대 지적에 줄만 맞은 빈 지적을 붙인다. 함정에 걸릴 지적을 줄만 맞은 짝이 가져가지 않게 하기 위해서다.

### 함정 · 짝 없음 · 중복

- 함정 걸림은 감점하지 않고 **"검토 필요"**로 따로 보인다 (다른 관점에서 타당할 수 있음).
- 짝 없는 지적은 새 지적 후보로 기록만 한다.
- 중복(`duplicates`): 함정 대조 뒤 남은 지적이, 이미 찾은 기대 지적과 강도 3(위치 + 필수 키워드)이고 필수 키워드가 제목에 있으면 짝 없음 대신 중복으로 센다.
  - 엄격한 규칙이라 지금 정답지에서는 거의 0이다. 줄이 먼 실제 중복은 놓친다.

### 결과

- 화면 표 + `<골든 폴더>/results/<날짜>_<라벨>.jsonl`.
- 사례별로 남는 것: 점수 · 짝 근거 · runId · 모델 · 토큰 · 입력 해시 · 정답지 해시 `keySha256`(expected + traps) · `scorerVersion` · provenance.
- 자동 짝은 틀릴 수 있으니 근거를 보고 확인한다.

### 정답지 묶음 해시

동결 표시용이다 (`GoldenCaseLoader.SetKeyHash`, 합계 줄에 보임).

- 사례 폴더 이름 순으로, 사례마다 이어 붙인다:
  - `<사례>/expected.json\n` + 원본 바이트
  - traps.json이 있으면 `<사례>/traps.json\n` + 원본 바이트
- 그 SHA-256 앞 16자리다.
- 동결은 묶음 해시 + `scorerVersion`으로 표시한다.
- 예전 동결 해시는 expected.json만 이어 붙인 값이라 함정 변경을 잡지 못했다. 지금은 expected + traps 기준이다.

## replay 사례

`kind: replay`, `mode: sub_self` · `sub_perf` · `sub_security` · `sub_direction`.

과거 검토형 서브에이전트 리뷰를 당시 코드 · 위임 메시지 그대로 복원해 API 리뷰(claude-cli)로 다시 돌린다.

**언제 돌리나**: **역할 프롬프트(Sub*) · 정의 색인 · 모델 · effort를 바꾸면 replay 세트를 1회** 돌려 기준점과 비교한다 (1회 약 $6, 사례당 $0.1~1.2).

### 만들기

```
--golden replay-build [runId|all] [--exclude 글자 ...] [--dir 폴더] [--yes]
```

- 외부 호출은 없다. `--yes`가 없으면 후보와 건너뛸 이유만 보인다.
- 보관된 결과 원문이 selfReview 실행과 같은 것만 잇는다 (cross-review ownReview는 아직 안 함).
- 세션 기록에서 가져오는 것:

  | 사례 파일 | 출처 |
  |---|---|
  | `input.md` | 첫 사용자 메시지 (위임 폴더 경로는 상대로) |
  | `files/` | Read 결과 (위임 폴더 기준, 나눠 읽은 범위가 전체를 덮으면 이어 붙임) |
  | `context.json` | 파일마다 전체 범위 |

- 건너뛰는 것: 일부만 읽은 파일 · 위임 폴더 밖 파일 · 이어 부른 결과 · 같은 원문의 판정 있는 실행이 여럿인 경우.
- 이미 있는 사례 폴더는 건드리지 않는다.
- 사례는 GoldenSet(로컬, push 훅이 막음)에 남는다. 회사 코드는 `--exclude <회사 프로젝트 폴더 이름>`처럼 뺀다 (프로젝트 폴더 · 위임 폴더 · 위임 메시지에 그 글자가 있으면 제외).

### 정답지

| 판정 | 들어가는 곳 |
|---|---|
| 리뷰어 지적 채택 · 부분 | expected (origin self-review) |
| 리뷰어 지적 기각 | traps (why = 기각 이유) |
| 지휘자 추가 채택 | expected (origin conductor) |

- 위치는 지적 머리의 모든 `파일:줄`(locations)이다.
- required는 지적 문장의 코드 이름을 자동 추출한다.
- **사례를 만든 뒤 한국어 개념어 · 구체 이름으로 보강해야** 찾음이 제대로 잡힌다.

### 실행

- `run`이 replay 사례를 `ModeRunner.RunAsync(rootDir = 사례 files/, tool = golden)`로 돌린다.
- 응답 `## 지적`을 self-reviewer 형식으로 읽어 채점한다.
- `--no-guidelines`는 받지 않는다.
- 입력 해시(`inputSha256`)는 input.md + context.json + files/를 함께 해시한다.

## crash 사례

`kind: crash`, `mode: crash`.

모드 전체(Sequential, 분류 → 웹검색)를 run_mode와 같은 프롬프트 조립으로 돌린다.

- `input.md`는 run_mode의 `input`이다 (서버가 `## 요청`으로 감쌈, 첨부 없음).
- `score` 명령은 안 된다 (검색 기록이 필요).
- 모드는 단계 2개 이상 + 마지막 단계에 WebSearch가 있어야 한다 (1단계 = 분류, 마지막 = 검색으로 채점). 아니면 실행 전에 거절한다.
  - case.json에 `mode: crash`를 꼭 적는다. 빠뜨리면 기본값 review라 거절된다.
- 실행 전 확인과 단계 수 · 에이전트 · 지침이 달라지면 그 사례부터 멈춘다.

**`expected.json` 항목**

```
{ id, step: triage|search, summary, keywords, urls?, severity }
```

- files · lines는 없다.
- urls에 빈 조각은 쓸 수 없다.
- `traps.json`은 `step`을 비우면 두 단계 모두에 적용된다.

### 채점 (`GoldenCrashScorer`)

| 단계 | 찾음 조건 |
|---|---|
| triage | 분류(1단계) 본문 키워드 |
| search | 검색 단계가 **실제로 본 URL**(참고 · 인용 · 직접 연 페이지)에 `urls` 조각이 있음 |

- search 근거에는 "인용 · 열람한 URL"인지 "검색 결과에만 있던 URL"인지 남긴다.
- 조각 뒤가 영숫자면 안 맞는다 (`issues/8932` ≠ `issues/89321`).
- "확인 필요"(찾음에 안 셈, 놓침에 듦)가 되는 경우: `urls`가 있는데
  - 본문 키워드만 맞거나
  - 조각이 검색 결과에 없는 URL에만 있을 때.
- `urls`가 없는 항목은 키워드로 찾음을 정한다.
- 함정은 키워드가 확신 표기(높음 · 확인됨 · 유력 · high · confirmed)와 **같은 줄**에 있을 때만 걸린다.
- 키워드는 부정 문맥에서도 맞으니 근거를 본다.
- 검색 단계 호출 기록(WebSearch)을 못 읽으면 점수 대신 채점 실패로 남긴다 ("검색 0회"와 구분).

결과:

- 결과 줄에 검색 수 · 본 URL 수 · 검색 결과에 없는 URL 목록.
- 합계는 리뷰와 따로 `[crash 합계] 분류 찾음 · 검색 찾음`.
