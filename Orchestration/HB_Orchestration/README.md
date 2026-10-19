# HB_Orchestration

Claude Code(구독)를 지휘자로, 외부 모델(GPT · Gemini · 로컬)을 **모드** 단위로 부려 쓰는 개인용 오케스트레이션.

```
Claude Code ──MCP──▶ HB.Orchestration.Mcp
  list_modes          │
  save_result         │  (기록의 응답 원문 → 파일)
  run_mode(mode, …)   ├─ modes.json   "무엇을 어떤 순서로"  (실행마다 다시 읽음 → 수정 즉시 반영)
                      │    debate   : Rebuttal@gpt → LogicCheck@gemini
                      │    deliberate: Rebuttal@gpt ⇄ Proponent@gpt_mini (최대 N회차) → Judge@gpt
                      │    research : Scout@gpt_mini → Feasibility@gpt
                      │    parallel : Worker@gpt | Worker@local
                      ├─ appsettings  "누가"  (제공사·모델·출력 상한·예산)
                      └─ 단계마다 기록 ──▶ calls.jsonl ◀── Dashboard (.cshtml)
```

## 구조

```
HB_Orchestration/
├─ HB_Orchestration.slnx          ← Rider는 이걸로 열 것
├─ HB_Orchestration.bat                   ← publish / 대시보드 실행 메뉴
├─ HB_Orchestration.config                ← 위 배치의 경로 설정 (메모장으로 편집)
├─ Directory.Build.props          ← 공통 설정
├─ src/
│  ├─ HB.Orchestration.Shared/    ← 호출 기록 모델 + JSONL 저장
│  ├─ HB.Orchestration.Agents/    ← 제공사·에이전트·모드 실행기 (호스트와 무관한 핵심)
│  ├─ HB.Orchestration.Mcp/       ← MCP 서버, appsettings.json, modes.json
│  └─ HB.Orchestration.Dashboard/ ← 호출 기록 + 모드 편집 UI
├─ project-files/                 ← Unity 프로젝트 쪽 규칙서·명령의 원본 + 설정 템플릿 (*.example.json). 폴더째 붙여넣지 않는다 (5절)
└─ tools/Sync-ProjectFiles.ps1    ← project-files → Unity 프로젝트 비교·복사
```

## 1. 패키지 & 빌드

```powershell
cd src/HB.Orchestration.Agents
dotnet add package Microsoft.Agents.AI
dotnet add package Microsoft.Extensions.AI
dotnet add package Microsoft.Extensions.AI.OpenAI      # 안 잡히면 --prerelease
dotnet add package Microsoft.Extensions.Logging.Abstractions
cd ../HB.Orchestration.Mcp
dotnet add package ModelContextProtocol                 # 안 잡히면 --prerelease
dotnet add package Microsoft.Extensions.Hosting
cd ../HB.Orchestration.Dashboard
dotnet add package Markdig
cd ../..

dotnet build HB_Orchestration.slnx
```

평소 publish와 대시보드 실행은 **`HB_Orchestration.bat`** 으로 한다 (아래 8절).

- publish 출력은 **솔루션 폴더 밖**으로. `dotnet run`으로 MCP 등록하지 말 것.
- publish는 `appsettings.json` / `modes.json`을 **복사하지 않는다** (publish 폴더에서 고친 설정을 덮어쓰지 않도록). 처음 publish할 때만 배치가 기본 파일을 넣어준다. 원본에 새로 생긴 키는 publish 끝에 `[알림]`으로 보여 준다 (8절).
- .NET 10 SDK가 없으면 `Directory.Build.props`의 `net10.0` → `net9.0`.

## 2. 에이전트 — `appsettings.json` ("누가")

에이전트 하나 = 제공사 + 모델 + 출력 상한. 이름은 자유 (영문·숫자·`_`·`-`). 등급별로 나눠두면 모드에서 고르기 쉽다.

| 예시 이름 | 제공사 | 용도 |
|---|---|---|
| `gpt_high_sol` | openai (상) | 판정·검증처럼 정확도가 중요한 단계 |
| `gpt_main_tera` | openai (중) | 일반 리뷰·반박 |
| `gpt_mini_luna` | openai (하) | 정리·탐색처럼 분량이 많은 단계 |
| `gemini_argon` / `gemini_flash` | gemini (OpenAI 호환 엔드포인트) | 다른 관점의 검증 |
| `local` | Ollama / LM Studio | 무료 초안·병렬 작업 |

- `MaxOutputTokens`: 응답 상한. **추론 모델은 생각 토큰도 포함**되므로 낮게 잡으면 답변 없이 끝난다 (서버가 `[빈 응답]` 실패로 기록). 폭주 방지용으로 넉넉하게 (1만 이상 권장).
- 키가 없거나 설정이 틀린 에이전트는 그것만 건너뛰고 서버는 정상 시작. `list_modes`에 `(사용불가)`로 표시.
- **저장하면 바로 반영된다.** 서버가 호출 때마다 파일 수정 시각을 보고, 바뀌었으면 설정·에이전트를 다시 읽는다 (재시작 불필요). JSON이 깨졌으면 이전 설정으로 계속 동작하고 `list_modes`·`server_info`에 경고가 뜬다.
- 대시보드 **모드** 화면에서 추가·수정·삭제할 수 있고, **이름을 바꾸면 그 이름을 쓰는 modes.json 단계도 같이 바뀐다** (7절).
- appsettings.json도 주석 없는 순수 JSON으로 둔다. 필드 설명은 아래 표.

### appsettings.json 필드 (`Orchestration` 섹션)
| 키 | 의미 |
|---|---|
| `ModesPath` | modes.json 위치를 직접 지정 (비우면 3절의 찾는 순서) |
| `GuidelinesPath` | 리뷰 지침 파일 (비우면 `REVIEW_GUIDELINES_PATH` 환경 변수 → Unity 프로젝트 루트의 `REVIEW_GUIDELINES.md`) |
| `Providers.<이름>.Type` | `openai-compatible` (현재 유일) |
| `Providers.<이름>.Endpoint` | API 주소. OpenAI는 비움, Gemini `https://generativelanguage.googleapis.com/v1beta/openai/`, Ollama `http://localhost:11434/v1`, LM Studio `http://localhost:1234/v1` |
| `Providers.<이름>.ApiKeyEnv` | API 키를 담은 **환경 변수 이름**. 로컬처럼 키가 필요 없으면 생략 |
| `Providers.<이름>.TimeoutSeconds` | 요청 하나의 네트워크 타임아웃(초). 에이전트에 값이 없을 때 적용 (생략 = SDK 기본 100초) |
| `Providers.<이름>.MaxAttempts` | 한 단계의 최대 시도 횟수 (첫 시도 포함). 에이전트에 값이 없을 때 적용 (생략 = 2) |
| `Providers.<이름>.PromptCache` | 프롬프트 캐시 방식 (OpenAI Chat Completions 전용). `ProviderDefault`(생략, 손대지 않음 — OpenAI는 입력 전부를 캐시에 쓰고 쓰기분을 입력의 1.25배로 청구) · `Off`(쓰기 안 함, 읽기 할인도 없음) · `Context`(첨부 자료가 약 3,000자 이상이면 그 끝에만 저장 지점, 짧으면 `Off`). 캐시는 30분 보관 — 같은 첨부로 30분 안에 다시 물으면 첨부 부분이 0.05배(sol)로 읽히고, 30분이 지나 다시 쓰면 그 1회는 1.25배. 다른 제공사에는 넣지 말 것 (모르는 필드 오류 가능) |
| `Agents[].Name` | 에이전트 이름 (영문·숫자·`_`·`-`, 64자 이내). modes.json의 `Agent`가 이 이름을 가리킨다 |
| `Agents[].Provider` | 위 `Providers`의 이름 |
| `Agents[].Model` | 제공사의 모델 ID |
| `Agents[].MaxOutputTokens` | 응답 상한 (생략 = 제한 없음) |
| `Agents[].TimeoutSeconds` | 요청 하나의 네트워크 타임아웃(초). **에이전트 → 제공사 → SDK 기본 100초** 순. 긴 응답을 쓰는 모델(sol 등)만 길게 잡을 때 사용. 대시보드 에이전트 표의 "타임아웃(초)" 칸 |
| `Agents[].MaxAttempts` | 한 단계의 최대 시도 횟수 (첫 시도 포함, 1~5). **에이전트 → 제공사 → 2**(= 최대 1회 재시도) 순. 아래 "재시도" 참고 |
| `Agents[].Enabled` | `false`면 끔 (생략 = 켬) |
| `Budget.FreeProviders` | 대시보드 오늘 사용량(토큰·비용)에서 뺄 제공사. 하루 토큰·비용 상한(`DailyTokenLimit` · `DailyCostLimitUsd`)은 2026-10-20에 없앴다 — 예전 키가 남아 있어도 무시 |
| `Budget.MaxInputChars` | `run_mode` 입력 최대 글자 수 (input + `context` 첨부 합계, 기본 350,000). Debate는 토론자가 매 턴 요청 전체를 받으므로 비용이 턴 수만큼 곱해짐 |
| `Context.MaxRanges` | `context` 범위 개수 상한 (기본 30) |
| `Context.MaxFileBytes` | 읽기 전에 확인하는 파일 크기 상한 (기본 2,000,000바이트) |
| `Context.DenyPatterns` | 외부로 보내지 않을 파일 glob **추가분**. 내장 목록(`.env`, `*.pem`, `*.key`, `*.keystore`, `*.jks`, `*.p12`, `*.pfx`, `*.mobileprovision`, `credentials*`, `secrets*`, `.git/**` 등)은 항상 적용 |
| `Context.WriteDenyPatterns` | save_result가 쓰지 않을 파일 glob **추가분** (쓰기에만 적용). 내장 쓰기 금지 목록(실행·설정 파일, 아래 save_result 절)과 `DenyPatterns`는 항상 적용 |
| `Context.ExtraRoots` | 프로젝트 루트 밖에서 `context`로 **읽을** 수 있는 추가 루트 (`이름 → 절대 경로`, 기본 비어 있음). 아래 "추가 루트" 참고 |
| `Images.MaxCount` · `Images.MaxBytes` · `Images.MaxDimension` | `run_mode` `images` 상한: 장수(기본 20), 장당 바이트(기본 2,000,000), 긴 변 px(기본 1024). 서버는 이미지를 줄이지 않고 넘으면 거절한다 (축소는 `image_prep.py shrink`). 이미지는 `MaxInputChars`에 들어가지 않고, 토큰은 실제 사용량으로 일일 한도·비용에 잡힌다 |
| `Providers.<이름>.ImageDetail` | 이미지 부분에 실을 `detail` 값 (OpenAI: `auto`·`low`·`high`·`original`). 비우면 보내지 않음 (= 제공사 기본값). Gemini OpenAI 호환 엔드포인트는 이 필드를 문서화하지 않아 제공사 단위로 둔다 |
| `Agents[].Vision` | 이미지 입력을 받는 모델인지 (기본 `true`). `false`인 에이전트가 낀 모드에 `images`가 오면 실행 전에 거절 |
| `Agents[].StructuredOutput` · `Providers.<이름>.StructuredOutput` | 구조화 출력(역할 `Output`) 요청 방식. **에이전트 → 제공사 → `Prompt`** 순. `Prompt` = 시스템 프롬프트에만 JSON 형식을 적음. `Schema` = 거기에 `response_format: json_schema`(strict)까지 보내 강제. 모델이 미지원이면 400 → 그 단계 실패 (자동으로 `Prompt`로 다시 보내지 않음, 이 값을 `Prompt`로 바꿀 것). 기본 설정은 `openai`만 `Schema` (아래 "구조화 출력") |
| `Budget.DefaultMaxRunsPerSession` | `MaxRunsPerSession`이 없는 모드의 세션당 실행 한도 |
| `Budget.FreeProviders` | 비용 집계에서 뺄 제공사 (기본 `local`) |
| `ResponseChecks.ExpectedLanguage` | 기대하는 응답 언어. 지금은 `ko`만 지원. **키가 없으면 `ko`(검사 켬), `""`이면 끔** (아래 "응답 검사기") |
| `ResponseChecks.ForeignScriptRatio` | 코드 블록을 뺀 본문에서 일본어 가나 비율이 이 값 이상이면 경고 (기본 0.02 = 2%) |
| `Debug.DumpToolResults` | `true`면 `run_mode`가 반환하는 문자열을 그대로 `%LOCALAPPDATA%/HB_Orchestration/dumps/`에 남긴다 (조사용, 기본 `false`). 결과가 Claude에게 가는 동안 바뀌는지 가를 때 쓰고, 끝나면 끈다 |
| `Records.BlobFolder` | 지침·설정 원문 저장 폴더. 비우면 `calls.jsonl` 옆 `blobs/`, 상대 경로면 `calls.jsonl` 폴더 기준 (아래 "호출 기록의 지침·설정 해시") |
| `Records.MaxReasonChars` · `Records.MaxOwnReviewChars` | `run_mode`의 `reason` · `ownReview` 글자 수 상한 (기본 500 · 30,000, 0 = 무제한). 넘으면 잘라 쓰지 않고 거절 (실행 횟수 차감 없음) |
| `Records.StoreSettingsText` | 서버 설정(`appsettings.json`) 원문도 저장할지 (기본 `false` = 해시와 키 패턴 검사 결과만). `true`여도 키 패턴이 보이면 저장하지 않음 |
| `WorkOrders.Folder` | `/work-order`가 지시서를 남길 폴더 (프로젝트 루트 기준 상대 경로). 값이 있으면 `list_modes` 끝에 `지시서 폴더: …` 줄이 붙고, 비우면 줄이 없고 명령은 `docs/work-orders/`를 쓴다. 절대 경로·`..`·`@이름`은 `[주의]`로만 표시. **서버는 이 폴더에 쓰지 않는다** (지휘자가 직접 씀) |
| `Pricing.KrwPerUsd` | 1달러당 원. 비우면 대시보드는 달러만 표시 |
| `Pricing.Models.<모델 ID>` | `InputPerMillion`, `CachedInputPerMillion`(비우면 입력과 같음), `CacheWritePerMillion`(캐시 쓰기, 비우면 입력과 같음 — OpenAI GPT-5.6 이후는 입력의 1.25배, 쓰기 토큰은 호출 기록 `CacheWriteTokens`), `OutputPerMillion` — USD / 1M 토큰. 웹검색 단계를 쓰는 모델에는 `WebSearchPerThousand`(1,000회당 USD)도 넣는다. 키는 에이전트 이름이 아니라 **모델 ID**(기록에 남는 값). 지금 안 쓰는 예전 모델도 남겨 두면 예전 기록의 비용이 계속 계산됨. 서버는 쓰지 않고 대시보드만 읽음 |

### 재시도

SDK 자동 재시도(기본 3회, 기록에 안 남음)는 끄고 서버가 직접 재시도한다.

| 오류 | 재시도 | 기록의 `ErrorKind` |
|---|---|---|
| 네트워크 타임아웃, 408 | O | `Timeout` |
| 429 요청 과다 | O (`Retry-After`·`retry-after-ms`를 따름, **30초를 넘으면 기다리지 않고 실패**) | `RateLimit` |
| 5xx | O | `Server` |
| 연결 실패 (응답 없음) | O | `Network` |
| 그 외 4xx (400 · 401 · 402 크레딧 부족 · 404 …) | X — 다시 보내도 같다 | `Client` |
| 사용자 취소 | X — 기록 없이 중단 | — |
| 빈 응답 | X — 원인이 출력 상한이라 같다 | — |

- 서버가 대기 시간을 알려주지 않으면 2초 → 5초를 기다린 뒤 다시 보낸다.
- **시도마다 calls.jsonl에 한 줄**씩 남는다 (`Attempt` 1부터, 같은 `RunId`·`Step`). 대시보드 목록에 `시도 n/N` 배지가 붙는다.
- `run_mode` 결과에는 `(재시도 1회 후 성공 · 1회차 Timeout)`처럼 한 줄로 알린다.
- **한 단계의 최대 대기 ≈ 타임아웃 × 시도 횟수** (+ 시도 사이 대기). 예: 타임아웃 300초, MaxAttempts 2 → 최대 약 10분.

### 응답 검사기

| 검사 | 기준 | 처리 |
|---|---|---|
| 빈 응답 | 내용 없음 | **실패** `[빈 응답]` (종료 이유가 길이 제한이면 MaxOutputTokens 안내) |
| 출력 상한에서 잘림 | 응답의 종료 이유가 길이 제한(`length`) | 경고 |
| 언어 이탈 | 코드 블록(```` ``` ```` · `~~~`)과 인라인 코드를 뺀 본문에서 가나 비율 ≥ `ForeignScriptRatio`, 가나 5자 이상 | 경고 |

- 경고는 실패가 아니다. 응답은 그대로 전달하고, `run_mode` 결과의 해당 단계 아래에 `[경고] 응답 언어 이탈 감지 (가나 비율 10.7%, 308자)`처럼 한 줄씩 붙인다.
- 경고는 기록(`Warnings`)에도 남고, 대시보드 목록과 상세 화면에 `경고 n` 배지로 보인다.
- 검사기 자체가 오류를 내면 경고 없이 넘어가고 서버 로그만 남긴다.
- `ExpectedLanguage`는 "비우면 기존 동작" 원칙의 **예외**로, 키가 없어도 검사가 켜진다. 경고는 결과를 바꾸지 않고, 사람이 대시보드를 보기 전에는 모르는 이탈(3장)을 기본으로 잡기 위해서다. 끄려면 `""`.
- 한자(중국어) 이탈은 잡지 않는다.

### 호출 기록의 지침·설정 해시 (`Provenance`)

호출 기록만으로 "이 에이전트가 어떤 지침과 설정으로 실행됐나"를 확정하기 위해, 모든 기록 줄에 `provenance`를 남긴다 (SHA-256 소문자 hex).

| 필드 | 무엇을 해시하나 | 원문 저장 |
|---|---|---|
| `instructionsSha256` · `instructionsBytes` | 에이전트에 넘긴 시스템 프롬프트 문자열 그대로의 UTF-8(BOM 없음) 바이트. 역할 + 공통 규칙 + 지침(+ Debate 토론자는 토론 규칙) | O |
| `guidelines` | 붙였으면 `attached: true` + 지침 파일 경로·원본 바이트 해시(`sha256sum`과 같음). 안 붙였으면 `attached: false` + `reason` (`역할 UseGuidelines=false` / `지침 파일 없음`) | O |
| `modes` | 실행 시점 modes.json 원본 바이트 | O |
| `settings` | 적용 중인 `appsettings.json`을 읽을 때 함께 읽은 원본 바이트 + 키 패턴 검사(`secretScan`: `통과` / `의심 n건`) | **X** (기본, `Records.StoreSettingsText`) |

- 원문은 `blobs/<sha256>.txt`에 **해시별로 한 번만** 저장한다. 같은 지침이면 새 파일이 생기지 않는다. 여러 서버 프로세스가 동시에 써도 내용이 같아 안전하다 (임시 파일 → 덮어쓰지 않는 이동).
- 저장에 실패해도 호출은 그대로 진행한다. 실패는 서버 로그와 기록의 `storeErrors`에 남는다 (없으면 필드 생략). 해시는 저장 실패와 관계없이 남는다.
- 해시 범위는 서버가 에이전트(MAF)에 넘긴 문자열까지다. HTTP 요청 본문(JSON 이스케이프)은 해시하지 않는다.
- `settings`의 해시는 실행 시점에 파일을 다시 읽은 값이 아니라 **지금 적용 중인 설정의 바이트**다. 다시 읽기에 실패해 이전 설정이 적용 중이면 이전 바이트의 해시와 `reloadError`가 남는다. 환경 변수(`Orchestration__…`)로 덮어쓴 값은 잡히지 않는다.
- 지침·modes.json을 읽다가 실패하면(다른 프로그램이 잠금 등) 실행 횟수 차감 없이 `[리뷰 지침 읽기 실패]` / `[모드 설정 오류]`로 거절한다.
- 5장 이전 기록에는 `provenance`가 없다 (대시보드 "기록 없음").

### 재시작이 필요한 경우
| 바꾼 것 | 필요한 동작 |
|---|---|
| modes.json (모드·역할·에이전트 배치) | 없음 — 다음 호출부터 반영 |
| appsettings.json (에이전트·모델·제공사·예산) | 없음 — 다음 호출부터 반영 |
| 세션 실행 횟수 | 없음 — 대시보드 **세션** 화면에서 초기화 |
| 서버 코드 (publish) | 배치가 MCP 서버만 종료 → Claude Code에서 `/mcp`로 재연결 (8절). Claude Code·대화는 그대로 |
| **API 키 환경 변수** | **PC 재시작** (직접). 환경 변수는 프로그램이 시작될 때 물려받는 값이라, Unity Hub·Toolbox 같은 트레이 프로그램까지 새로 떠야 확실하다 |

## 3. 모드 — `modes.json` ("무엇을 어떤 순서로")

**실행할 때마다 다시 읽으므로 저장만 하면 바로 반영**된다. 대시보드의 **모드** 화면에서 흐름을 보고 에이전트를 바꿔 저장할 수도 있다 (7절).

찾는 순서: `ModesPath`(appsettings) → `HB_MODES_PATH` 환경 변수 → **Unity 프로젝트 루트의 `hb-modes.json`** (프로젝트별 모드) → 서버 폴더의 `modes.json`

> modes.json은 **주석 없는 순수 JSON**으로 둔다. 설명은 이 README에 있다. (서버는 주석이 있어도 읽지만, 대시보드에서 저장하면 주석이 사라진다.)

```json
{
  "Roles": {
    "Rebuttal": { "Prompt": "너는 반박자다 ...", "UseGuidelines": true }
  },
  "Modes": {
    "debate": {
      "MaxRunsPerSession": 2,
      "Description": "주장/설계에 반박 후 양쪽 논리 검증",
      "Flow": "Sequential",
      "Return": "All",
      "Steps": [
        { "Role": "Rebuttal",   "Agent": "gpt_main_tera" },
        { "Role": "LogicCheck", "Agent": "gemini_flash", "Sees": "InputAndPrevious" }
      ]
    }
  }
}
```

### 역할 (`Roles.<이름>`)
| 키 | 값 | 의미 |
|---|---|---|
| `Prompt` | 문자열 | 시스템 프롬프트 본문 |
| `PromptFile` | 경로 | 프롬프트가 길면 파일로 분리 (modes.json 기준 상대 경로). `Prompt`와 둘 중 하나 |
| `UseGuidelines` | `true`(기본) / `false` | REVIEW_GUIDELINES.md를 붙일지. 코드와 무관한 역할(탐색·실효성)은 `false`로 입력 토큰 절약 |
| `Output` | `findings` · `perf_findings` / 생략 | 구조화 출력 형식. 지정하면 서버가 JSON 형식 설명을 시스템 프롬프트에 붙이고 응답을 해석한다. 역할 프롬프트에는 출력 형식을 따로 적지 않는다. 모르는 이름이면 `[모드 설정 오류]`. `WebSearch` 단계와 함께 쓸 수 없다 |

내장 역할 `Reviewer`, `Critic`은 코드에 들어 있고, 같은 이름으로 정의하면 덮어쓴다. 내장 `Reviewer`는 `Output: findings`다 (modes.json에서 덮어쓰면 그쪽 `Output`을 따른다). `PerfReviewer`는 modes.json에 `Output: perf_findings`로 둔다.

### 모드 (`Modes.<이름>`)
| 키 | 값 | 의미 |
|---|---|---|
| `Description` | 문자열 | `list_modes`에서 Claude에게 보이는 한 줄 설명. 짧을수록 토큰 절약 |
| `Flow` | `Sequential` / `Concurrent` / `Debate` | 순서대로 넘기기 / 동시에 독립 실행 / 다회차 토론 |
| `Steps` | 배열 | 단계 목록 (아래 표) |
| `Return` | `Last`(기본) / `All` | Claude에게 돌려줄 범위. Concurrent는 항상 All |
| `MaxInputChars` | 숫자 | 이 모드의 입력 길이 상한. 없으면 `Budget.MaxInputChars` |
| `PromptCache` | `Off` / `Context` / 없음 | 이 모드의 프롬프트 캐시 방식. 없으면 제공사 설정(`Providers.<이름>.PromptCache`). 제공사가 정책 없음(`ProviderDefault`)이면 무시. 같은 첨부를 30분 안에 다시 보내지 않는 모드는 `Off`(쓰기 1.25배만 내지 않게). 기본 modes.json은 preflight 단계 5개 · `parallel` · `work_order`가 `Off` |
| `MaxRunsPerSession` | 숫자 | Claude Code 세션 하나에서 실행할 수 있는 횟수 (0 = 무제한). 없으면 `Budget.DefaultMaxRunsPerSession` |
| `RequireOwnReview` | `true` / 없음 | `true`면 `run_mode`의 `ownReview`가 필수 (review 계열). 없으면 필수 아님. `list_modes`에 `[ownReview 필수]` 표시. 기본 modes.json은 `review` · `panel` · `work_order` · `preflight_p1` · `preflight_check` · `preflight_perf` · `perf_sol` · `perf_astra` · `perf_argon` |
| `MaxRounds` (Debate) | 1~5 | 최대 회차. 1회차 = 두 토론자가 한 번씩. 기본 2, 서버 절대 상한 5 |
| `HistoryWindow` (Debate) | 숫자 | 각 턴이 볼 직전 발언 수 (기본 2). 판정자는 항상 전체 |

### 단계 (`Steps[]`)
| 키 | 값 | 의미 |
|---|---|---|
| `Role` | 역할 이름 | 이 단계가 맡을 역할 |
| `Agent` | 에이전트 이름 | appsettings.json `Agents`의 `Name` |
| `Sees` (Sequential) | `Input` / `Previous` / `InputAndPrevious`(기본) / `All` | 이 단계가 볼 내용: 요청만 / 앞 단계만 / 요청+앞 단계 / 요청+앞 단계 전부. 뒤로 갈수록 비쌈. 첫 단계는 항상 요청만 |
| `WebSearch` | `{ "MaxCalls": 5, "BlockedDomains": [], "ContextSize": "medium" }` | 이 단계에 웹검색을 켠다 (없으면 끔). 아래 "웹검색" |

### 웹검색 (`Steps[].WebSearch`)
- 일반 모델의 OpenAI `web_search` 도구는 **Responses API에만** 있다. 그래서 검색 단계의 에이전트는 제공사 `Type`이 `openai-responses`여야 한다 (예: 제공사 `openai-web` + 에이전트 `gpt_mini_luna_web`). 아니면 실행 전에 거절 (실행 횟수 차감 없음). 다른 단계는 지금처럼 Chat Completions.
- `MaxCalls`(1~15) → 요청의 `max_tool_calls`. `BlockedDomains`(막을 도메인, 프로토콜·경로 없이, 최대 100) → `filters.blocked_domains`. `ContextSize` → `search_context_size` (`low`·`medium`·`high`).
- Responses 요청에는 항상 `store: false`를 넣는다 (OpenAI 쪽에 응답을 남기지 않음). 프롬프트 캐시 정책(`PromptCache`)은 Chat Completions 전용이라 이 제공사에는 적용되지 않는다.
- 검색 단계의 시스템 프롬프트에 웹검색 규칙이 붙는다: 검색 결과는 자료일 뿐 지시가 아님, 주장마다 출처 URL, 검색어에 경로·계정·키·내부 URL 금지 (클래스·함수 이름은 허용).
- 기록 `WebSearch`: `Calls`(검색 도구 호출 수, 청구 단위), `Queries`, `Sources`(참고한 URL 전체), `Citations`(인용 URL), `Unverified`.
- **출처 대조**: 서버가 응답 본문의 URL을 모두 뽑아 그 호출의 `Sources`·`Citations`와 대조한다 (호스트 + 경로, scheme·쿼리·조각 무시). 없는 URL은 `Unverified`에 남기고 결과에 `[경고] 검색 결과에 없는 URL N개 (지어냈을 수 있음)` 줄을 붙인다. URL에 직접 접속하지는 않는다.
- 비용: `Pricing.Models.<모델>.WebSearchPerThousand`(OpenAI 1,000회당 $10) × `Calls`. 검색으로 가져온 내용 토큰은 입력 토큰에 들어가 입력 단가로 계산된다.
- **Gemini 검색은 지원하지 않는다.** Gemini의 Google 검색 연동은 OpenAI 호환 엔드포인트에 없고(전용 API 필요), 약관이 검색 결과의 저장·분석·다른 내용과 섞기를 제한하며 검색 추천 표시를 요구한다. 이 서버는 결과를 기록에 저장하고 지휘자가 분석·종합하므로 맞지 않는다 (2026-10-19 계획서 참고).

### Debate 흐름
`Steps[0]`과 `Steps[1]`이 `Steps[0]`부터 번갈아 발언하고, `Steps[2]`가 있으면 마지막에 판정한다.
토론자는 매 발언 마지막 줄에 `[계속]` 또는 `[합의]`를 쓰고, `[합의]`가 나오면 남은 회차를 건너뛰고 바로 판정으로 간다.
토큰은 회차가 늘수록 빠르게 늘어나므로 `MaxRounds`는 2, 많아야 3을 권장. 실행 중 실패하면 판정 없이 중단.

### 실행 한도
세션당 실행 한도는 MCP 서버 프로세스(= Claude Code 세션 하나)별로 센다. 새 세션을 열면 0부터 다시 센다. 검증 단계에서 거절된 호출(설정 오류, 입력 초과 등)은 횟수에 포함되지 않는다.

한도에 걸렸는데 더 필요하면 **서버를 재시작하지 말고** 대시보드 **세션** 화면에서 그 세션의 횟수를 초기화한다. 서버를 다시 띄우려면 Claude Code 세션을 새로 열어야 해서 진행 중이던 대화 맥락까지 잃는다.
- 서버는 세션 상태를 `%LOCALAPPDATA%/HB_Orchestration/sessions/{PID}.json`에 남기고, 대시보드는 초기화 요청을 `{PID}.reset`으로 남긴다. 서버가 다음 `run_mode`·`list_modes` 때 그걸 읽고 적용한다.

### 지휘자의 판단 근거 (`reason` · `ownReview`)

| 인자 | 필수 | 쓰임 |
|---|---|---|
| `reason` | 모든 모드 | 이 모드를 부른 이유 한 줄 |
| `ownReview` | `RequireOwnReview: true` 모드 | 지휘자가 결과를 보기 전에 완성한 자체 검토 원문 |

- **에이전트에게 보내지 않는다.** 프롬프트·시스템 프롬프트 어디에도 들어가지 않고, 호출 기록(`reason` · `ownReview`)과 대시보드에만 남는다 (리뷰의 독립성).
- 같은 실행의 모든 기록 줄(단계·시도)에 같은 값이 원문 그대로 남는다.
- 없거나(공백만 포함) 상한을 넘으면 에이전트를 부르기 전에 `[reason 필요]` · `[ownReview 필요]` · `[… 너무 깁니다]`로 거절한다. 실행 횟수는 차감하지 않는다.
- 도구 스키마에서는 선택 인자다. 필수로 두면 누락이 `[`로 시작하는 안내가 아니라 도구 오류로 나가기 때문에, 필수 여부는 서버가 검사한다.

### 실행할 때만 에이전트 바꾸기
`run_mode`의 `agents` 인자로 modes.json을 고치지 않고 이번 실행만 바꿀 수 있다. Claude Code에서는 "panel을 gpt_high_sol이랑 gemini_argon으로 돌려줘"처럼 말하면 된다.
- `"gpt_main_tera=gpt_high_sol"` — 그 에이전트만 교체
- `"*=gpt_high_sol"` — 모든 단계를 교체 (panel처럼 모델 비교가 목적인 모드에는 쓰지 말 것)
- 교체 대상은 appsettings에 등록·활성화된 에이전트여야 하고, 잘못되면 토큰 없이 거절된다. 결과 끝에 `(에이전트 교체: a→b)`가 붙고 대시보드에는 실제 실행한 에이전트로 기록된다.
- 교체 실행도 세션당 실행 횟수에 포함된다.

### 추가 루트 (`Context.ExtraRoots`) — 프로젝트 밖 코드를 복사 없이 읽기
```json
"Context": { "ExtraRoots": { "orch": "F:/AI/ToolWithCS/Orchestration/HB_Orchestration" } }
```
- `context`에서 `"path": "@orch/src/Foo.cs"`처럼 `@이름/상대경로`로 지정한다. 등록된 이름은 `list_modes` 마지막 줄 `추가 루트(context 읽기 전용): @orch`에 보인다 (경로는 표시하지 않음).
- 루트마다 메인 루트와 같은 검사(루트 밖·절대 경로·링크 경유·`DenyPatterns`)를 한다.
- **읽기 전용**이다. `save_result`는 추가 루트에 쓰지 않는다.
- 에이전트에게는 `### @orch/src/Foo.cs:1-80`처럼 이름만 보낸다. 로컬 절대 경로는 외부로 보내지 않는다.
- 기록(`calls.jsonl`)의 `ContextRoots`에는 그 호출이 실제로 읽은 루트의 이름과 절대 경로가 남는다. 나중에 설정을 비워도 출처를 알 수 있고, 대시보드는 이 값으로 파일 링크를 연다. `@`를 쓰지 않은 호출에는 이 필드가 없다.
- 이름은 영문·숫자·`_`·`-` 2~32자이고 **대소문자를 구분하지 않는다** (.NET 설정 시스템이 키를 대소문자 무시로 다룸). 표시는 설정에 적힌 이름으로 한다.
- 비어 있으면 기존과 똑같이 동작한다. 등록되지 않은 `@이름/…`은 기존처럼 메인 루트 기준 상대 경로다.
- 잘못된 항목(상대 경로, 없는 폴더, 이름 규칙 위반)은 `list_modes`에 `[주의]`로만 표시된다. 그 루트를 참조한 호출만 `[첨부 코드 오류]`로 거절되고, 다른 호출은 영향을 받지 않는다.
- appsettings는 호출마다 다시 읽으므로 넣고 빼는 데 재시작이 필요 없다.

### run_mode 결과 형식
```
runId: dfa393514b
### 1. Cross_P3_A · gpt_main_sol (입력 15,551 / 출력 7,502, 144.0초)
(재시도 1회 후 성공 · 1회차 Timeout)          ← 재시도가 있었을 때만
[경고] 응답 언어 이탈 감지 (가나 비율 10.7%, 308자)  ← 검사기 경고가 있을 때만
<에이전트 응답>
```
- `runId`는 `save_result`에 넘기는 값이다. 대시보드와 calls.jsonl에서도 같은 값으로 찾을 수 있다.
- 구조화 출력 역할이면 `<에이전트 응답>` 자리에 서버가 그린 지적 목록이 온다 (아래 "구조화 출력").
- 토큰은 성공한 시도의 값, 시간은 재시도와 대기를 포함한 단계 전체 시간이다.
- `Return: Last`도 runId와 마지막 단계 제목 줄을 붙인다. 출력에서 빠지는 단계의 재시도·경고는 결과 끝에 `단계 N …`으로 모은다.
- 실행 전에 거절된 경우(`[모드 설정 오류]`, `[입력이 너무 깁니다]` 등)는 아직 runId가 없으므로 기존처럼 `[`로 시작하는 한 줄이다.

### 구조화 출력 (역할 `Output`)
review 계열(`Reviewer` · `PerfReviewer`)의 응답을 JSON으로 받아 지적마다 ID를 매긴다. 계획서 `docs/plans/2026-10-19_리뷰-구조화-출력.md`.

- 형식 (코드 내장 `FindingFormat`, 스키마 버전은 기록의 `Structured.Format`에 `findings@2`처럼 남음)
  - `findings`: `findings[]`(`severity` critical·major·minor·nit, `file`, `line`, `problem`, `detail`, `suggestion`, `confidence` high·medium·low) + `needsInfo[]`
  - `needsInfo[]` (@2부터 객체): `what`(필요한 것) · `path`(첨부에 없던 파일, 모르면 null) · `symbol`(타입·메서드·검색어, 모르면 null) · `why`(판단이 어떻게 달라지는지). 서버가 `단계-N순번` ID를 매겨 `자료 요청 1-N1: …`으로 그린다. 해석은 @1 문자열도 받는다 (예전 기록·골든 세트 다시 채점)
  - `perf_findings`: 위 + 지적마다 `frequency`·`scaling`·`measurement`, severity는 high·medium·low, 최상위 `specDependent[]`·`fixDirection[]`·`measurePriority[]`
- 해석 순서: 종료 사유 → JSON(코드 펜스·앞뒤 문장은 첫 `{`~마지막 `}`로 벗김) → 타입·값 검증 → ID. 항목 하나라도 틀리면 응답 전체를 해석 실패로 본다.

| 상태 (`Structured.State`) | 조건 | 지휘자에게 |
|---|---|---|
| `complete` | 정상 종료, 검증 통과, 자료 부족 없음 | 그린 목록. 0건이면 `문제 없음` |
| `needs_info` | 검증 통과, `needsInfo` 있음 | 그린 목록 + 끝줄 `검토 불완전: 자료 부족 n건` (`문제 없음`을 쓰지 않음) |
| `incomplete` | 출력 상한 잘림·필터 중단인데 JSON은 읽힘 | 그린 목록 + `검토 불완전: … 일부 지적만 있을 수 있음` |
| `failed` | JSON이 아니거나 검증 실패 | **원문 그대로** + `[경고] 구조화 출력 해석 실패 (사유)`. 단계 실패로 치지 않음 |

```
- 1-F1 [major] src/A.cs:42 — 문제 / 제안 / 확신도(high)
  상세: 발생 조건·원인·영향
  빈도: 매 프레임 · 증가: O(N) · 측정: …     ← perf_findings만
자료 요청 1-N1: Calls 수집부 (path src/Reader.cs · symbol RunCalls) — 중복 id 처리 확인
사양 의존: / 수정 방향: / 측정 우선순위:      ← perf_findings만, 있을 때
총 1건(critical 0 / major 1 / minor 0 / nit 0)
```
- 지적 ID `단계-F순번`은 심각도 → 에이전트가 낸 순서로 매긴다. 기록 시점에 한 번 정하고 다시 해석하지 않는다. runId 안에서만 유일하다.
- **겹침 후보**: 서로 다른 단계의 지적 중 `file`·`line`이 둘 다 있고 같은 파일, 그룹 첫 줄에서 5줄 이내면 결과 끝에 `겹침 후보: src/A.cs:40-44 — 1-F1, 2-F1` 한 줄. 최대 10줄 (넘으면 `외 n그룹`). 합치지 않는다 — 같은 문제인지는 지휘자가 판단한다.
- 기록: `Response`는 원문(JSON) 그대로, `Structured`에 해석 결과(상태·지적·ID·요청 방식 `schema`/`prompt`). 언어 이탈 검사는 그린 텍스트에 돌린다.
- `save_result`는 해석된 기록이면 **그린 텍스트**를 저장한다 (원문 JSON은 대시보드 호출 상세의 "응답 원문"에서 본다).
- 대시보드 호출 상세: 형식·요청 방식·상태 한 줄, 그린 목록, 접힌 원문.

### 자료 보충 재질문 (`run_mode`의 `followUp`)
계획서 `docs/plans/2026-10-19_자료요청-왕복.md`. 에이전트가 직접 코드를 읽는 도구는 두지 않는다 (경로 검사를 통과해도 외부 전송 허가는 아니다 — critique bd33879c06). 대신 지휘자가 요청을 보고 로컬에서 찾아 **승인한 범위만** 보낸다.

`run_mode(mode, input, context, reason, followUp: { runId, step, requests: ["1-N1", …] })`
- 그 단계의 에이전트(원래 실행에서 바꿨으면 바꾼 에이전트)에게 `[원래 프롬프트, 원래 응답]`을 앞 대화로 두고, 보충 자료(`context`)·지휘자 메모(`input`)·보충 지시를 이어 보낸다. 앞부분이 원래 호출과 같아 제공사 프롬프트 캐시에 걸린다.
- 결과는 같은 runId의 **새 단계 번호**(예: 2)로 남는다 — 지적·요청 ID(`2-F1`)와 `save_result`가 원래 응답과 섞이지 않는다. 기록에 `FollowUpOf`·`FollowUpRequests`·`FollowUpSkipped`.
- 실행마다 **성공한 보충 1회** (실패·취소된 시도는 다시 할 수 있다, 같은 실행의 동시 호출은 서버 안 예약으로 하나만). **세션 실행 한도는 차감하지 않는다** (사용자 결정 — followUp으로 한도가 다 차지 않게). 입력 상한(원래 프롬프트 + 응답 + 보충)은 본다. `reason` 필수. `agents`·`images`·`ownReview`와 함께 쓰면 거절한다.
- 계획은 그 단계 하나만, 원래 실행의 에이전트로 다시 조립한다. 시스템 프롬프트 해시가 원래 호출과 다르면 결과에 `(주의: 시스템 프롬프트가 원래 실행 때와 다르다)`.
- 결과 끝: 원래 단계와 보충 단계 지적의 `겹침 후보`, `(판정은 N단계 지적 기준 — 원래 단계의 같은 지적은 duplicate로 …)`. 보내지 않은 요청은 결과 끝과 에이전트에게 간 메시지에 표시된다. 지휘자는 그 이유를 사용자에게 알린다 (CLAUDE.md "결과 종합" 6번).
- 판정 화면: followUp으로 보낸 요청은 따로 기록하지 않아도 "보충"으로 센다. 보충 단계가 새로 낸 요청은 다시 보충할 수 없어 "처리 안 함"에 넣지 않고 따로 센다.
- 거절: 이미 보충한 실행, 진행 중, 없는·빈 요청 ID, 모드가 다름, Debate, 골든 평가 실행, 이미지를 받은 단계, 보충할 자료 없음.

### save_result — 응답 원문을 파일로
`save_result(runId, step, path, codeBlock?, overwrite?)`

calls.jsonl의 응답 원문을 그대로 저장한다. Claude가 응답을 다시 출력해 옮겨 적지 않으므로 출력 토큰이 들지 않고, 옮겨 적기 오류가 없으며, 저장 위치가 고정된다.
- 같은 단계에 시도가 여럿이면 성공한 시도를 쓴다.
- `path`는 그 기록의 프로젝트 폴더(`ProjectDir`) 기준 상대 경로다. `context`와 같은 규칙으로 절대 경로·루트 밖·링크 경유·`DenyPatterns`를 거절한다. 없는 하위 폴더는 만든다.
- 추가 루트(`@이름/…`)에는 쓰지 않는다. `@`로 시작하는 경로는 등록 여부와 관계없이 `[저장 불가]`.
- `codeBlock: N`이면 응답 안 N번째 코드 블록(```` ``` ```` · `~~~`)의 내용만 저장한다. 닫히지 않은 블록(응답 끝까지)이면 저장은 하고 결과에 `— 닫히지 않은 블록 (응답이 잘렸을 수 있음)`을 붙인다.
- 이미 있는 파일은 `overwrite: true`일 때만 덮어쓴다.
- **쓰기 전용 금지 목록** (외부 응답이 실행·설정에 영향을 주는 파일로 들어가지 않게, 비밀 파일 목록에 더해 쓰기에만 적용). 폴더 기준 패턴은 **어느 깊이에서든** 걸린다 (Unity 프로젝트가 작업 폴더의 하위 폴더여도):
  - Claude Code·에이전트 설정과 지침: `**/.claude/**`, `.mcp.json`, `CLAUDE.md`, `CLAUDE.local.md`, `REVIEW_GUIDELINES.md`, `hb-modes.json`, `AGENTS.md`, `GEMINI.md`, `**/.cursor/**`
  - 저장소·CI·git 훅: `**/.github/**`, `.gitattributes`, `.gitmodules`, `**/.husky/**`, `**/.githooks/**`, `.pre-commit-config.yaml`
  - IDE: `**/.vscode/**`, `**/.idea/**`
  - 빌드·패키지: `*.csproj`, `*.sln`, `*.slnx`, `*.props`, `*.targets`, `global.json`, `nuget.config`, `package.json`, `**/Packages/manifest.json`, `**/Packages/packages-lock.json`, `**/ProjectSettings/**`
  - 실행 파일·스크립트(어디서든, `docs/`·`review_tmp/` 포함): `*.ps1` `*.psm1` `*.psd1` `*.bat` `*.cmd` `*.sh` `*.py` `*.exe` `*.dll` `*.com` `*.scr` `*.msi` `*.vbs` `*.vbe` `*.js` `*.jse` `*.wsf` `*.wsh` `*.hta` `*.reg` `*.lnk`
  - Unity 컴파일 대상: `**/Assets/**/*.cs`, `**/Packages/**/*.cs`, `*.asmdef`·`*.asmref`(Assets·Packages 아래), `*.rsp`(csc.rsp 컴파일러 인수)
  - 거절 문구 `쓰기 금지 파일입니다 (WriteDenyPatterns '…')`. 응답 코드는 `docs/`·`review_tmp/` 등에 `.md`·`.cs`·`.txt`로 저장한 뒤 합친다. 추가분은 `Context.WriteDenyPatterns`. 호출부가 목록을 넘기지 않으면 내장 목록이 적용된다 (열리는 쪽이 아니라 막는 쪽).
- 쓰기는 같은 폴더의 짧은 임시 파일(`.<guid>.tmp`)에 다 쓴 뒤 이동으로 교체한다. 실패해도 잘린 파일이 남지 않고 `overwrite: true`의 원래 내용은 그대로다. `overwrite: false`이고 대상이 있으면 쓰기 전에 "이미 있습니다"로 거절하고, 확인과 이동 사이에 생긴 경우도 이동 단계에서 같은 문구로 막는다. 쓰기 단계의 실패는 "쓰기 실패".
  - 이동 방식이라 원래 파일의 속성·생성 시각·대체 데이터 스트림은 이어지지 않고, 다른 프로그램이 삭제 공유 없이 열고 있으면 덮어쓰기가 "쓰기 실패"가 된다 (의도된 동작).
- UTF-8(BOM 없음)으로 저장하고 `저장: <경로> (n바이트, SHA-256 …)`을 돌려준다. 실패는 `[저장 불가] …`.
- 이 도구로 MCP 도구는 4개가 된다. 도구 설명이 컨텍스트에 늘 올라가므로 설명은 한 문장으로 짧게 두었다.

### record_verdicts — 지적마다 판정 기록
`record_verdicts(runId, items[])` — item: `findingKey`, `summary?`, `verdict`, `steps?`, `reason?`, `severity?`, `claudeAlsoFound?`, `file?`, `findingIds?`

결과를 종합한 뒤 지휘자가 지적마다 판정을 남긴다. 대시보드 **판정** 화면의 원본이다 (규칙은 CLAUDE.md "결과 종합" 5번).
- `verdict`: `adopted`(채택) · `partial`(부분 채택) · `rejected`(기각) · `deferred`(보류) · `duplicate`(중복). 채택이 아니면 `reason` 필수 (`Records.MaxReasonChars` 이내).
- `steps`: 그 지적을 낸 단계 번호. 그 실행에 없는 번호는 거절. 비우면 **Claude만 지적**(에이전트가 놓친 것)으로 기록하고 `claudeAlsoFound`는 true가 된다.
- `findingIds`(구조화 출력의 지적 ID, 예: `["1-F2","2-F1"]`): 주면 `steps`를 ID에서 채우고(같이 주면 일치해야 함), 비어 있는 `summary`·`file`·`severity`를 **첫 ID**의 지적에서 채운다. severity는 여러 ID 중 가장 높은 것을 high·medium·low로 바꿔 쓴다. 이 실행에 없는 ID는 거절. 판정 줄에 `FindingIds`로 남는다.
- **자료 요청 ID**(`1-N1`)를 `findingIds`로 주면 요청 처리 기록이 된다 (`Kind: request`): `supplied`(보충) · `declined`(보내지 않음, `reason` 필수) · `deferred`. 지적 ID와 한 판정에 섞을 수 없다. 채택률·놓침에는 들어가지 않고 판정 화면 "자료 요청 처리"에 따로 모인다 (처리 안 한 요청 목록 포함).
- 하나라도 틀리면 아무것도 쓰지 않는다 (`[판정 기록 불가] …`에 틀린 항목을 모두 적음). 한 번에 50건까지.
- 저장: `calls.jsonl`과 같은 폴더의 `verdicts.jsonl` (환경 변수 `HB_ORCH_VERDICTS_PATH`로 바꿀 수 있음). 한 줄 = 판정 1회, 한 호출의 줄은 한 번에 쓴다.
- 같은 `runId`·`findingKey`로 다시 쓰면 마지막 판정이 이긴다 (보류 → 결론, 사용자가 뒤집은 판정). 이전 줄은 지우지 않는다.
- 에이전트·역할은 저장하지 않고 `runId` + 단계로 calls.jsonl에서 찾는다. 단계의 대표 호출은 성공한 마지막 시도다.
- 이 도구로 MCP 도구는 5개가 된다.

#### 판정 화면 (`/Verdicts`)
| 칸 | 뜻 |
|---|---|
| 채택률 | (채택 + 부분 채택 × 0.5) / (채택 + 부분 채택 + 기각). 보류·중복은 뺀다. **지휘자가 받아들인 비율**이지 정확도가 아니다 |
| 에이전트만 | 채택·부분 채택 중 ownReview에 없던 것 |
| 놓침 | Claude만 지적해 채택된 것이 있던 실행에서 그 에이전트가 **지적을 내는 단계**였던 수. 지적을 내는 단계 = 구조화 출력을 냈거나 그 실행에서 지적을 하나라도 낸 단계 (판정자·검증 역할 제외). 자유 텍스트로 지적 0건을 낸 리뷰어는 빠진다 |
| 비용 · 채택 1건당 | 판정이 있는 실행의 호출(재시도 포함) 비용 합계와 그것을 (채택 + 부분 채택 × 0.5)로 나눈 값. 기록에서 계산만 한다 |

- 기간은 판정 시각이 아니라 실행 시작 시각 기준 (소급 판정도 그 실행의 기간에 들어간다).
- 판정 없는 실행 목록을 아래에 보여 준다. 호출 상세에는 그 실행의 판정, 호출 목록에는 `판정 n` 배지.

## 4. 토큰 절약 장치

| 장치 | 어디서 | 효과 |
|---|---|---|
| 도구 5개(`list_modes`, `run_mode`, `save_result`, `record_verdicts`, 확인용 `server_info`)만 노출 | MCP | 모드가 늘어도 Claude 컨텍스트의 도구 설명이 그대로 |
| `save_result` | MCP | 응답을 파일로 남길 때 Claude가 다시 출력하지 않음 (출력 토큰 0) |
| 실행 전 검증 | 서버 | 모드·에이전트·역할 오류, 입력 초과, 예산 초과면 **토큰 0으로** 즉시 반환 |
| 단계별 `Sees` | modes.json | 다음 단계에 필요한 것만 전달 (예: research 검증자는 후보 목록만) |
| `Return: Last` | modes.json | 최종 판정만 필요하면 Claude가 읽는 양 감소 |
| 앞 단계 실패 시 중단 | 서버 | 의미 없는 뒤 단계 호출 안 함 |
| 공통 출력 규칙 | 서버 | 서론·반복·원문 재인용 금지 → 출력 토큰 감소 |
| 고정 시스템 프롬프트 앞부분 | 서버 | 역할+규칙+지침이 매번 같아 제공사 프롬프트 캐시 적중 |
| `UseGuidelines: false` | modes.json | 코드와 무관한 역할에 지침을 안 붙임 |
| `MaxOutputTokens` | appsettings | 응답 폭주 방지 |
| `MaxRounds` · `HistoryWindow` · `[합의]` 조기 종료 | modes.json | 다회차 토론의 누적 비용 제한 |
| `MaxRunsPerSession` | modes.json | 리뷰→수정→재리뷰, 추가 조사 반복 같은 루프를 세션 단위로 차단. 기본값은 `Budget.DefaultMaxRunsPerSession` |
| `files`는 에이전트에 안 보냄 | 서버 | 경로 목록은 기록·링크 전용 |
| `context`로 코드 첨부 | 서버 | Claude가 이미 읽은 코드를 input에 다시 쓰지 않음 → **Claude 출력 토큰 절약**. 서버가 `{path, startLine, endLine}`을 디스크에서 읽어 줄 번호와 함께 붙임. 루트 밖·링크(심볼릭·junction)·전송 금지 파일·범위 오류·크기 초과는 호출 전에 거절. 첨부한 범위는 기록의 `Context`, 내용은 `Prompt`에 남음. 8.3 짧은 이름도 긴 이름으로 바꿔 금지 패턴을 검사. 한계: 검사와 읽기 사이에 링크로 바꿔치기하는 경쟁은 막지 않음(단일 사용자 PC 전제) |
| `images`로 이미지 첨부 | 서버 | `image_prep.py shrink`로 줄인 png·jpeg를 서버가 읽어 user 메시지에 `[첨부 자료] → [이미지 N] 이름표 + 이미지 → [요청]` 순서로 붙인다. 입력을 받는 단계에만 붙음. 경로 규칙은 `context`와 같고(`@이름/` 포함), 형식·장수·바이트·긴 변·중복은 호출 전에 거절. 기록에는 `Images`(경로·크기·sha256 앞 12자)만 남고 원문은 남기지 않음. 준비 순서는 CLAUDE.md "이미지 첨부" (collect → 사용자 정리 → shrink) |
| CLAUDE.md의 input 규칙 | Unity 쪽 | diff·최소 자료만, 기본은 호출하지 않기 |

## 5. Unity 프로젝트 쪽
> **`project-files/`가 원본이다.** Unity 프로젝트의 `CLAUDE.md`, `REVIEW_GUIDELINES.md`, `.claude/commands/*.md`, `.claude/scripts/*.py`는 복사본으로 둔다. 규칙을 고칠 때는 원본을 고친 뒤 아래 스크립트로 복사한다 (Unity 쪽에서 직접 고치면 두 벌이 갈라진다). 줄바꿈은 LF (`.gitattributes`로 고정).

**처음 설치 (폴더째 붙여넣지 않는다)**
1. 스크립트 `-Apply`로 규칙서·명령을 복사한다 (아래).
2. `project-files/.mcp.example.json`을 Unity 프로젝트 루트에 **`.mcp.json`으로** 복사하고, `<publish 폴더>`를 실제 publish 폴더로 고친다.
3. `project-files/.claude/settings.example.json`을 `.claude/settings.json`으로 복사한다. 이미 있으면 `allow` 항목과 `hooks`를 합친다.
4. Unity 프로젝트 `.gitignore`에 `.mcp.json`과 `.claude/push_guard.local.txt`를 넣는다 (기기별 절대 경로 · 개인 값).
5. `.claude/push_guard.local.txt`를 만들고 공개되면 안 되는 개인 값(이메일·로컬 사용자 경로 등)을 한 줄에 정규식 하나로 넣는다 (H2 훅, 없으면 내장 패턴만 검사).
6. `python -I .claude/scripts/check_agents.py`(훅 등록) · `python -I .claude/scripts/test_hooks.py`(훅 동작)가 통과하는지 본다. 훅은 새 세션부터 걸린다.

설정 템플릿은 실제 설정과 이름이 다르다(`*.example.json`). 폴더째 붙여넣어도 Unity 쪽 `.mcp.json` · `.claude/settings.json`이 덮이지 않게 하기 위해서다. 예전에 같은 이름이던 때 폴더째 붙여넣었다가 `.mcp.json`이 `<publish 폴더>` 자리표시로 덮여 MCP 연결이 끊긴 적이 있다.

```powershell
# 비교만 (기본). 종료 코드 0 = 모두 같음, 1 = 다른 것이 있음, 2 = 실행 오류
powershell -ExecutionPolicy Bypass -File tools\Sync-ProjectFiles.ps1 -UnityProject F:\MyGame
# 복사 + 해시 확인
powershell -ExecutionPolicy Bypass -File tools\Sync-ProjectFiles.ps1 -UnityProject F:\MyGame -Apply
```

- 동기화 대상: `CLAUDE.md`, `REVIEW_GUIDELINES.md`, `.claude/commands/*.md`, `.claude/scripts/*.py`. 바이트가 같아야 한다.
- 확인만 (만들거나 덮지 않음, 종료 코드와 무관): `.claude/settings.json`은 `settings.example.json`과 해시를 비교한다. `.mcp.json`은 기기마다 경로가 달라 해시는 비교하지 않고, 없거나 `<publish 폴더>`가 남아 있으면 `[할 일]`로 알린다. Unity 쪽에 템플릿 이름 파일이 있으면 `Unity에 템플릿 사본`으로 보여 준다 (지워도 됨).
- **`-Apply`는 Unity 쪽 파일이 원본과 다르면 아무것도 복사하지 않고 멈춘다.** Unity에서 고친 내용이 있으면 원본에 먼저 합친다. 버려도 되면 `-Force`로 덮는다. 덮기 전 파일은 `<Unity>/review_tmp/sync-backup/<시각>/`에 백업된다.
- 줄 맨 앞 `\---`, `\_`, `\[`, `1\.` 같은 이스케이프 잔재가 보이면 경고만 한다 (종료 코드와 무관). 규칙서에 이런 이스케이프가 섞여 지침 불러오기가 깨진 적이 있다 (`@REVIEW\_GUIDELINES.md`, 원인 미확인).
- `CLAUDE.md` — 언제 어떤 모드를 쓰고, input을 어떻게 줄이고, 결과를 어떻게 종합할지
- `REVIEW_GUIDELINES.md` — 공통 리뷰 기준 (서버가 자동으로 찾아 주입)
- `.claude/commands/cross-review.md` — `/cross-review`, `/cross-review panel`
- `.claude/commands/self-review.md` — `/self-review`: 지휘자 + self-reviewer만 (외부 에이전트 없음). CLAUDE.md 리뷰 조건에 걸리면 지휘자가 `/self-review` · `/cross-review` · 리뷰 없음 중에서 묻는다. 판정은 `record_verdicts`의 `selfReview`(runId 비움)로 남기면 서버가 self-review 실행(Tool·Mode `self_review`, 에이전트 `self-reviewer`, 제공사 `claude-subagent`, 토큰은 하루 상한에 넣지 않고 UsageRaw에)을 만든다 → 판정 화면에 self-reviewer 채택률·놓침
- `.claude/commands/mode.md` — `/mode debate ...`, `/mode deliberate ...`, `/mode research ...`, `/mode parallel ...`
- `.claude/commands/unity-test.md` — `/unity-test`, `/unity-test PlayMode MyGame.Combat`
- `.claude/commands/work-order.md` — `/work-order <요청> [첨부]`: 오케스트레이션에 맡길 작업 지시서를 만든다. 조사 → 초안 → `work_order` 모드(luna | flash 동시 검토) → `그외 이상사항` 정리 → `WorkOrders.Folder`(없으면 `docs/work-orders/`)에 저장 + 대화에 출력. 코드는 고치지 않는다. 모드별 지침은 CLAUDE.md가 아니라 이 명령 파일에 둔다 (CLAUDE.md는 세션마다 컨텍스트에 올라감)
- `.claude/commands/preflight.md` — `/preflight <요청> [첨부]`: 큰 작업의 실행 전 점검. `preflight_p1`(A 반박 · B 위험 · 성능 동시 검토) → `preflight_p2`(상호 판정) → 사용자 확정 → `preflight_p3`(최종 수정안) → 지휘자 종합 → `preflight_check`(전체, astra) · `preflight_perf`(성능, sol). 단계마다 멈추고, P5에는 앞 단계 논의를 붙이지 않는다. 산출물은 `docs/preflight/<날짜>_<주제>/`. 첨부 xlsx·csv는 `xlsx_dump.py`로 Markdown을 만들어 `context`로 붙인다. 성능만 볼 때는 `/mode perf_sol …`(기본) · `perf_astra`(중요한 핫패스) · `perf_argon`(Gemini)
- `.claude/scripts/xlsx_dump.py` — `/work-order`의 첨부 읽기 도우미. 기획서 xlsx·csv를 Markdown 표로 뽑고(병합 셀·메모·텍스트 상자 글 포함) 이미지는 `review_tmp/work-order/<파일>/`에 붙은 셀 이름으로 저장. 필요: Python 3.9+, `openpyxl` (`python -m pip install openpyxl`). `.xls`는 지원하지 않음
- `.claude/scripts/cleanup_guard.py` · `push_guard.py` · `run_mode_guard.py` — 규칙 강제 훅 H1~H4 (CLAUDE.md "훅이 막는 규칙"), 시험은 `test_hooks.py`
- `.claude/settings.example.json` → Unity의 `.claude/settings.json` — Unity CLI 조회·테스트 명령은 매번 승인 없이 실행 + PreToolUse 훅 등록 (기존 파일이 있으면 `allow` 항목과 `hooks`를 합치기)
- `.mcp.example.json` → Unity의 `.mcp.json` — dll 경로를 publish 폴더로 (JSON 안에서는 경로를 `/`로 쓸 것. `\`는 JSON에서 특수 문자)

API 키는 시스템 환경 변수로만. 각 제공사 대시보드에서도 월 한도 설정.

## 6. Unity CLI 연동 (지휘자가 직접 테스트)

이 서버와는 독립적이다. Claude Code가 터미널에서 `unity` CLI를 직접 쓰는 구조라 서버 코드 변경이 없다.

```powershell
# 설치 (베타 채널)
$env:UNITY_CLI_CHANNEL='beta'; irm https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.ps1 | iex
unity --version

# (선택) Claude Code에 Unity CLI 사용법 문서 설치
unity skill install --help
```

- **테스트**: `unity test`는 배치 모드로 별도 에디터를 띄운다. **같은 프로젝트가 에디터에 열려 있으면 실행할 수 없으니** 테스트 전에 에디터를 닫는다. 종료 코드 `0` 통과 / `8` 실패 / 그 외 실행 오류.
- **라이브 에디터 조작**(씬·오브젝트 편집, Play 모드 등)은 프로젝트에 Pipeline 패키지가 필요하다: `unity pipeline install` (Unity 6.0+).
- **Unity MCP 서버**(`unity mcp configure claude-code`)도 있지만, 에디터 명령이 전부 도구로 올라와 Claude 컨텍스트를 차지한다. 토큰을 아끼려면 기본은 CLI(Bash)로 쓰고, 에디터 조작을 자주 할 때만 MCP를 추가하는 걸 권장.
- `.claude/settings.json`은 `unity status/list/test/logs`만 자동 허용한다. `unity command`(임의 C# 실행이 가능한 `eval` 포함)는 일부러 매번 승인을 받게 뒀다.

## 7. 대시보드

`HB_Orchestration.bat` → 2번 (또는 `dotnet run --project src/HB.Orchestration.Dashboard`) → http://localhost:5280

**호출 기록** (`/`)
- 기간 · 모드 · 제공사 필터, 호출 수 · 토큰 · 평균 응답 · 예상 비용 · 실패, 일별 토큰
- **오늘 비용** · **오늘 토큰** 칸: 오늘 0시부터, `FreeProviders` 제외 (필터와 무관. 비용은 MCP appsettings 단가만 사용, 단가 없는 모델은 "단가 없는 모델 N건(비용 미집계)"으로 표시). 상한은 없다. 기간 필터의 "최근 24시간"은 어제 호출도 들어가므로 이 값과 다르다
- 캐시: `캐시 0`은 미적중, `캐시 정보 없음`은 제공사가 값을 주지 않은 기록(Gemini는 미적중일 때 값이 없음, 예전 기록)
- 상세: 요청/응답, **같은 실행의 다른 단계와 실행 전체 토큰 합계**
- **첨부 코드 · 관련 파일**: 이름을 누르면 대시보드 안 보기(`/Attachment`, 줄 번호 + 보낸 줄 범위 강조). 옆 **Rider**는 IDE로 연다
  - 링크 형식: `Dashboard:FileLinkTemplate` 기본 `jetbrains://rider/navigate/reference?project={project}&path={path}:{line}` (JetBrains 데몬이 Rider로 넘김, 처음 한 번 브라우저가 허용을 묻는다)
  - Rider는 **열린 솔루션 폴더 기준 상대 경로만** 연다 (2026-10-19 Rider 2026.2.3 확인: 절대 경로·`/`까지 인코딩·솔루션 폴더 밖 파일은 안 열림). 그래서 폴더 → 프로젝트 이름을 Rider 최근 솔루션 목록(`%APPDATA%/JetBrains/Rider*/options/recentSolutions.xml`)에서 읽고, 파일을 담은 가장 깊은 솔루션 폴더 기준으로 링크를 만든다. 어느 솔루션에도 속하지 않으면 "Rider 없음"
  - 목록에 없거나 이름이 다르면 `Dashboard:RiderProjects`(`"폴더": "프로젝트 이름"`)로 덧붙인다. 목록 파일 위치는 `Dashboard:RiderRecentSolutionsPath`
  - 예전 기본값 `http://localhost:63342/api/file?...`은 Rider 2026.2에 그 기능이 없어 404다. `{project}`가 없는 템플릿은 예전처럼 절대 경로를 넣는다
  - 대시보드 보기는 **그 기록에 적힌 항목만** 연다 (순번 밖은 404). 경로 규칙은 서버와 같다 (루트 밖·링크·전송 금지 패턴 거절). 첨부 코드는 파일이 그 뒤 바뀌었을 수 있으니 실제 보낸 내용은 요청(Prompt)에서 본다
- **웹검색** 패널: 검색어, 응답에 인용된 URL, 참고한 URL 전체(접힘), 검색 결과에 없는 URL(빨간색, 지어냈을 수 있음). 목록에는 `· 검색 N`(출처 의심이 있으면 빨간색). 모드 화면의 단계 칸에 검색 설정과, 검색할 수 없는 에이전트면 경고
- **첨부 이미지**: 보낸 순서대로 썸네일(누르면 원본). 기록에는 원문이 없어 지금 파일을 보여 주고, 기록의 sha256 앞 12자와 비교해 `보낸 이미지와 같음` / `기록 이후 파일이 바뀜` / `파일 없음`을 표시한다. 목록에는 `· 이미지 N`
- 상세 위쪽에 **지휘자 판단과 에이전트 응답을 나란히**: `reason` · `ownReview`(결과를 보기 전 자체 검토) · 응답. 5장 이전 기록은 "기록 없음", ownReview가 필수가 아닌 모드에서 넣지 않았으면 "넣지 않음"
- **에이전트가 받은 지침·설정** 표: 시스템 프롬프트 · 리뷰 지침 · modes.json · 서버 설정의 경로 · SHA-256 · 크기. 원문이 `blobs/`에 있으면 "원문" 링크 (그 기록이 가리키는 해시만 열림, 링크 파일은 열지 않음, 서버 설정은 기본 저장 안 함). 단계 목록에 단계별 시스템 프롬프트 해시 앞 8자
- **Claude 세션** 칸: 이 호출을 부른 세션으로 이동 (확정 / 추정 표시, 아래 "Claude 세션")
- 비용: **달러와 원화를 함께** 표시 (`$0.0416 (₩57)`). 캐시된 입력 토큰은 캐시 단가로 계산
  - 계산: `(입력 - 캐시) × 입력 단가 + 캐시 × 캐시 단가 + 출력 × 출력 단가` (입력 토큰에는 캐시 토큰이 포함됨)
  - 단가·환율은 MCP `appsettings.json`의 `Orchestration:Pricing`에 USD로 저장하고, **모드 화면의 "모델 단가" 표에서 편집**한다
  - 캐시 토큰 수는 이 기능을 넣은 뒤의 호출부터 기록된다 (그 전 기록은 전부 일반 입력 단가로 계산)

**모드** (`/Modes`)
- MCP publish 폴더의 `modes.json`을 읽어 모드마다 흐름을 그림으로 보여준다 (순차 → / 동시 묶음 / 토론 ⇄ → 판정).
- 단계별 에이전트(드롭다운), 세션당 실행 한도, 최대 회차, 반환 범위를 바꿔 **저장**하면 다음 `run_mode`부터 바로 반영된다 (재시작 불필요).
- **모델 단가** 표: 설정에 단가가 있는 모델, 켜진 에이전트가 쓰는 모델, 호출 기록에 나온 모델을 모두 한 줄씩 보여주고(쓰는 에이전트·기록 횟수 표시, 단가 없는 모델 강조), **입력 / 캐시 입력 / 출력 단가(USD / 1M 토큰)**와 환율을 편집한다. 에이전트와 상관없이 모델 단위라 예전 모델도 계속 관리할 수 있다. 세 칸을 다 비우면 그 모델의 단가를 지우고, 마지막 빈 줄로 새 모델을 추가한다.
- **이미지 첨부** 패널 (표시 전용): `Images` 상한과 제공사별 `ImageDetail`. 에이전트 표의 **이미지** 열은 `Vision` (표시 전용, 저장해도 기존 값 보존. 바꾸려면 appsettings.json 직접 수정).
- 저장 전 검증: 없는 에이전트·역할, Debate 단계 수, 회차 범위. 꺼진 에이전트는 경고만.
- 저장 방식: `modes-backup/`에 백업(최근 10개) → 임시 파일에 쓰고 한 번에 교체. 화면을 연 뒤 파일이 다른 곳에서 바뀌었으면 저장을 거부하니 **다시 불러오기** 후 수정.

**모드 화면 — 에이전트 편집** (appsettings.json)
- 표에서 이름 · 제공사 · 모델 ID · 출력 상한 · 사용 여부를 바꾸고 **에이전트 저장**. 마지막 빈 줄에 입력하면 새 에이전트가 추가된다. 저장 즉시 반영 (재시작 불필요).
- **이름 변경 동기화**: 이름을 바꾸면 그 이름을 쓰던 modes.json의 모든 단계도 새 이름으로 바뀐다.
- 모드가 쓰고 있는 에이전트는 삭제할 수 없다 (삭제 칸이 잠김). 먼저 모드의 에이전트를 바꾼다.
- 백업: `settings-backup/` (최근 10개). appsettings.json의 다른 섹션(Logging, Providers, Budget)과 표에 없는 필드는 그대로 둔다.

**모드 화면 — 없는 에이전트 일괄 교체**
- appsettings.json을 메모장으로 직접 고쳐서 modes.json이 없는 이름을 가리키게 되면, 화면 위에 경고와 함께 `없는 이름 → 바꿀 에이전트` 선택이 뜬다. **일괄 교체**를 누르면 그 이름을 쓰는 모든 단계가 바뀐다.

**세션** (`/Sessions`)
- 실행 중인 Claude Code 세션(= MCP 서버)마다 프로젝트, PID, 시작 시각, 서버 빌드 시각, 모드별 사용 횟수 / 한도를 보여준다.
- 모드별 **초기화** 또는 **전체 초기화** → 그 세션의 다음 호출 때 적용 (서버 재시작 불필요). 적용 전에는 "초기화 대기 중"으로 표시.
- 서버 빌드가 publish 폴더의 dll보다 오래됐으면 **이전 빌드** 표시 → 그 세션에서 `/mcp`로 재연결하면 새 서버가 뜬다.
- 강제 종료된 서버가 남긴 기록은 화면을 열 때 자동 정리.
- 어느 폴더를 볼지는 대시보드 `appsettings.json`의 `Dashboard:McpConfigDir` (배치로 실행하면 `HB_Orchestration.config`의 `PUBLISH_DIR`가 우선). 편집할 파일을 직접 지정하려면 `Dashboard:ModesPath`.
- Unity 프로젝트에 `hb-modes.json`이 있으면 그 프로젝트에서는 그쪽이 우선이다. 이 화면은 publish 폴더 쪽만 고친다.

**Claude 세션** (`/ClaudeSessions`) — 지휘자가 무엇을 알고 있었나
- Claude Code 세션 기록(`~/.claude/projects/<경로 인코딩>/<sessionId>.jsonl`)을 **읽기만** 해서, 세션마다 시작 시각(로컬) · 버전 · 프로젝트 · 불러온 지침 파일 · 부른 `run_mode`를 보여준다. **대화 본문은 보여주지 않는다.**
- 지침 파일: `instructions` 항목의 `files[]` — 경로 · 종류 · 크기 · SHA-256 (기록된 content의 UTF-8 바이트 기준). 지금 디스크 파일과 대조해 `같음` / `끝 줄바꿈만 다름`(디스크 = content + LF 정확히 1개) / `다름` / `파일 없음`. 세션 기록이 끝 줄바꿈을 빼고 저장하는 것은 파일 3개로 확인한 관찰이다.
- `run_mode` 연결: 결과 첫 줄 `runId:`가 있으면 **확정**. 머리말이 없는 이전 서버의 결과는 같은 모드 · 같은 프로젝트 · 호출 시각 −2초 ~ +60초에 시작한 실행이 하나뿐일 때만 **추정**. 후보가 여럿이면 연결하지 않고, 실행 전에 거절된 호출(`[`로 시작하는 결과)은 추정하지 않는다. 다른 세션이 확정한 실행은 추정 후보에서 뺀다.
- 결과가 백그라운드(120초 초과 → `<task-notification>`)나 큰 출력(`<persisted-output>` 미리보기)으로 들어와도 찾는다. 다른 도구 결과(예: Bash로 calls.jsonl을 읽은 출력)에 든 runId는 쓰지 않는다.
- 세션 기록 형식은 비공식이다. JSON이 아니거나 모양이 다른 줄은 "알 수 없는 줄 n", 알아볼 줄이 하나도 없는 파일은 "알 수 없는 형식"으로 표시한다. 줄마다 엄격한 UTF-8로 읽고, 잘못된 바이트가 있는 줄은 알 수 없는 줄로 센다.
- 설정: `Dashboard:ClaudeProjectsDir` (비우면 `CLAUDE_CONFIG_DIR/projects` → `%USERPROFILE%\.claude\projects`), `Dashboard:ClaudeSessionLimit` (목록에 보일 최근 세션 수, 기본 50. 연결은 전체 세션으로 계산).
- `/Sessions`(살아 있는 MCP 서버)와는 다른 화면이다.

**청구 대조** (`/Billing`) — OpenAI 실제 청구와 기록 기준 계산 비교
- 기간: 7일 · 3일 · 1일(UTC 날짜별 금액·토큰) · 시간별 24시간(토큰과 기록 기준 계산, 오늘 누적 청구는 한 줄로). OpenAI 비용은 UTC 자정 기준 하루 버킷이고, 하루가 끝나기 전에도 몇 분 지연으로 누적값이 갱신된다.
- 청구값 가져오기:
  - 조직 Admin 키(`sk-admin-…`, Read Only로 충분)가 환경 변수 `Dashboard:Billing:AdminKeyEnv`(기본 `OPENAI_ADMIN_KEY`)에 있으면 "새로 가져오기" 버튼으로 Costs API(항목별)·Usage API(모델별 토큰, 캐시 쓰기 포함)를 부른다. 버튼을 누를 때만 부르고 자동 새로고침과 묶지 않는다.
  - 키가 없으면 OpenAI 대시보드에서 받은 비용 파일(CSV·JSON)을 올린다 (메모리에서만 읽고 저장하지 않음). 이때는 날짜별 금액 대조만 된다.
- 키는 프로세스 환경 변수 → Windows 사용자 환경 변수 순으로 매번 읽는다 (새로 넣어도 재시작 불필요). 화면·로그·설정 파일에 값을 쓰지 않는다.
- 계산은 OpenAI 조직으로 청구되는 제공사(openai · openai-web 등) 호출만, 서버와 같은 `ModelPrice.CostOf` (웹검색은 search 동작 수, 추정). 캐시 쓰기를 기록하기 전(2026-10-18 이전) 호출이 있는 날은 "쓰기 가정"(캐시에 안 걸린 입력 전부 쓰기) 값을 함께 보여준다. 기록 밖 호출(curl·같은 조직의 다른 도구)은 계산에 없다.

## 8. HB_Orchestration.bat (publish · 대시보드 실행)

솔루션 폴더의 `HB_Orchestration.bat`을 더블클릭하면 메뉴가 뜬다.

| 메뉴 | 동작 |
|---|---|
| 1 | MCP 서버 publish → `PUBLISH_DIR`. 실행 중인 MCP 서버가 있으면 **서버만 종료할지 묻고** 진행. 설정 파일은 백업 후 그대로 유지하고, 원본에만 있는 경로를 알린다 (아래) |
| 2 | 대시보드 빌드 후 별도 창에서 실행 + 브라우저 열기. 이미 떠 있으면 브라우저만 연다 |
| 3 | 1 + 2 |
| 4 | `HB_Orchestration.config`를 메모장으로 열기. 메모장을 닫으면 바로 다시 읽는다 |

명령줄로도 쓸 수 있다: `HB_Orchestration.bat publish` / `dashboard` / `all`

**설정 (`HB_Orchestration.config`)** — `KEY=VALUE`, `#`은 주석, 상대 경로는 배치 폴더 기준. 배치는 **자기와 같은 이름의 `.config`** 를 읽으므로 이름을 바꿀 땐 둘을 같이 바꾼다.

| 키 | 기본값 | 의미 |
|---|---|---|
| `SOLUTION_DIR` | `.` | `HB_Orchestration.slnx`가 있는 폴더 |
| `PUBLISH_DIR` | `..\Publish_Output` | MCP publish 폴더 (`.mcp.json`이 가리키는 곳). 대시보드 모드 화면도 여기를 편집 |
| `CONFIGURATION` | `Release` | 빌드 구성 |
| `DASHBOARD_PORT` | `5280` | 대시보드 포트 |

**publish와 실행 중인 Claude Code**
- 실행 중인 MCP 서버는 dll을 잡고 있어 그대로는 publish가 실패한다. 배치가 서버 프로세스를 찾아 **서버만 종료할지(Y/N)** 묻는다. Claude Code와 대화는 그대로 남는다.
- 서버를 종료한 순간 그 세션에서 진행 중이던 `run_mode`가 있었다면 그 호출은 실패한다. 에이전트 호출 중이 아닐 때 publish할 것.
- **재연결은 수동이다** (테스트로 확인함). publish가 끝나면 각 Claude Code 세션에서 `/mcp` → `hb-orchestration` 재연결.
  - 확인하고 싶으면 "server_info 실행해줘" → 새 빌드 시각이 나오면 새 서버.
  - 대시보드 **세션** 화면에서도 세션별 서버 빌드 시각으로 확인할 수 있다.
- 실행 중인 서버가 없으면 그냥 publish하고, 다음에 Claude Code를 열 때 새 서버가 뜬다.

**publish와 설정 파일 (`appsettings.json` · `modes.json`)**
- publish 전에 사용 중 설정을 `%LOCALAPPDATA%\HB_Orchestration\config-backup\<yyyyMMdd-HHmmss-난수>\`에 백업하고, publish 뒤 그 백업으로 되돌린다. 백업은 최근 20개를 남긴다 (이 이름 모양의 폴더만 정리).
- 백업에 실패하면 publish하지 않고 멈춘다. 되돌리기에 실패하면 `[ERROR]`와 백업 경로를 보여 주고 멈춘다 (손으로 복사).
- publish가 끝나면 **원본(`src/HB.Orchestration.Mcp/`)에만 있는 경로**를 `[알림]`으로 보여 준다. 파일은 바꾸지 않는다 — 필요한 항목을 사용 중 설정에 직접(또는 대시보드로) 옮긴다.
  ```
  [알림] modes.json: 원본에만 있음 Modes.work_order
  [알림] appsettings.json: 원본에만 있음 Agents[Name=gpt_new].MaxOutputTokens
  appsettings.json: 원본에만 있는 경로 없음
  ```
  - 원본에만 있는 키는 그 경로 하나만 보인다 (안쪽은 펼치지 않음). 같은 경로에서 자료형이 다르면 `자료형 다름`. 사용 중에만 있는 키, 같은 자료형의 값 차이는 알리지 않는다.
  - 키는 대소문자 무시. Name을 가진 객체 배열(`Agents`)은 Name으로 짝짓는다. **그 밖의 배열(`Steps`, `FreeProviders` 등)은 안쪽을 비교하지 않는다.**
  - 2-way 비교라 사용 중 설정에서 일부러 지운 모드도 publish 때마다 다시 알린다.
  - publish 폴더 기준이다. `ModesPath` · `HB_MODES_PATH` · 프로젝트의 `hb-modes.json`을 쓰면 실제로 쓰이는 파일은 다를 수 있다.
  - 직접 실행: `dotnet <publish 폴더>\HB.Orchestration.Mcp.dll --config-drift <원본 폴더> <publish 폴더>` (종료 코드 0 비교함 / 1 비교 못 한 파일 있음 / 2 사용법 오류). 인자 없이 실행하면 평소의 MCP 서버다.

**기타**
- 대시보드 창을 닫으면 대시보드가 꺼진다.

## 9. 골든 세트 (`--golden`) — 프롬프트·모드·모델 변경 회귀 평가

정답이 알려진 사례 묶음을 같은 입력으로 다시 돌려, **찾아야 할 지적을 몇 개 찾았는지**와 **과거에 기각된 지적을 다시 내는지**를 숫자로 비교한다. 역할 프롬프트·modes.json·모델을 바꾼 뒤에 쓴다. MCP 서버를 띄우지 않는 일회성 실행이다.

```
dotnet <publish 폴더>\HB.Orchestration.Mcp.dll --golden run [사례|all] [--mode 모드] [--agents from=to,...] [--dir 폴더] [--label 이름] [--no-guidelines] [--yes]
dotnet <publish 폴더>\HB.Orchestration.Mcp.dll --golden score <사례> --from <파일> [--dir 폴더] [--label 이름]
```
- `run`은 `--yes`가 없으면 사례·에이전트·입력 글자 수·입력 비용 추정·붙는 지침 파일만 보이고 **외부 호출 없이** 멈춘다.
- 지침은 run_mode와 같은 순서로 찾는다 (`Orchestration:GuidelinesPath` → `REVIEW_GUIDELINES_PATH` → **현재 폴더**의 `REVIEW_GUIDELINES.md`). 지침을 쓰는 역할인데 못 찾으면 계획을 보인 뒤 거절한다 (외부 호출 없음) — 프로젝트 루트에서 실행하거나 경로를 지정한다. `--no-guidelines`는 파일이 있어도 지침을 빼고 돈다 (지침 있음·없음 비교용, 기록 사유 `골든 --no-guidelines`). 사례를 돌기 직전마다 지침을 다시 읽어 실행 전 확인과 다르면(파일이 바뀜·사라짐) 그 사례부터 멈춘다. 결과 줄에 `guidelinesAttached` · `guidelinesSha256` · `guidelinesReason`.
- run_mode와 같은 시스템 프롬프트(역할 + 출력 형식 + 공통 규칙 + 지침)·에이전트로 보낸다. 세션 실행 한도·reason·ownReview·context 첨부는 쓰지 않는다 (첨부는 사례의 `input.md`에 이미 들어 있음). 하루 비용 상한은 없으니 실행 전 계획의 예상 비용을 보고 `--yes`로 돌린다. 단계가 하나이고 구조화 출력(`Output`)이 있는 모드만 채점된다.
- 호출 기록에는 `Tool = golden`, `Mode = 원래 모드`로 남는다 (대시보드·비용 집계에 보임). 세션 상태 파일은 남기지 않는다.
- `score`는 self-reviewer 결과(마크다운 `- C1 [major] 파일:줄 — 문제`)나 findings JSON(@1·@2)을 채점만 한다 (외부 호출 없음).
- 폴더: `--dir` 또는 appsettings `Orchestration:Golden:Folder`. 사례 하나라도 형식이 틀리면 아무것도 돌리지 않는다. 종료 코드 0 성공 / 1 일부 사례 실패 / 2 사용법·사례 형식 오류 / 3 크레딧·한도 소진(Quota)으로 남은 사례를 멈춤.
- **Quota**: 429 중 `insufficient_quota`·`credit_balance_exhausted`(메시지나 응답 본문)는 `ErrorKind.Quota`로 분류해 재시도하지 않는다 (`[실패] Quota — 크레딧/한도 소진, 충전 전까지 재실행 금지`). 골든은 Quota가 나오면 남은 사례를 돌리지 않고, 다단계(crash) 사례는 단계 제공사 중 하나라도 **가장 최근 판정 가능한 호출 기록(7일, 성공 또는 Quota — Timeout·Network 등은 건너뜀)이 Quota**면 앞 단계도 호출하지 않는다. 충전 뒤 **그 제공사로 가는** 작은 호출 1회(그 제공사 에이전트의 리뷰 사례, 또는 `--agents`로 연결)로 성공 기록을 남기면 풀린다. 판정은 OpenAI 문구 기준이다 — Gemini 한도 소진(RESOURCE_EXHAUSTED)은 지금도 RateLimit으로 재시도된다.

**사례 폴더** (`<골든 폴더>/<사례 id>/`, `results/`와 `_`로 시작하는 폴더는 사례가 아님)

| 파일 | 내용 |
|---|---|
| `case.json` | `id`(폴더 이름과 같게), `kind`(`record` 판정 기록 · `seeded` 고친 결함을 되돌린 코드 · `crash` 원인이 알려진 크래시, 아래), `mode`(기본 review), `source`, `description` |
| `input.md` | 에이전트에게 보낼 프롬프트 원문 (그대로 보냄) |
| `expected.json` | 기대 지적 `[{ id, summary, files, lines: [시작, 끝], keywords, severity }]` |
| `traps.json` | (선택) 과거에 기각된 지적, 같은 형식 + `why`(기각 이유) |

**채점 규칙** (`GoldenScorer`, LLM 판정자 없음)
- 짝 후보: (같은 **파일 이름** + 줄이 범위 ±5) 또는 (**키워드**가 문제·상세에 있음, 대소문자 무시, 단 지적이 다른 파일을 가리키면 제외). 둘 다면 강도 3, 키워드만 2, 줄만 1.
- **찾음은 강도 2 이상만** 센다. 줄만 맞은 짝(강도 1)은 같은 줄 범위의 다른 결함과 구분하지 못해 "줄만 맞음 — 확인 필요"로만 보인다 (맞는 지적이면 정답지 키워드를 보강).
- 기대 지적 하나에 짝 하나, 지적 하나도 한 번만 쓴다. 강도 2 이상 짝의 수가 최대가 되게 정하고(빈 지적 먼저, 맞바꾸면 강도 합이 커지면 바꿈), 그다음 남은 지적을 함정과 대조하고(강도 2 이상), 마지막으로 남은 기대 지적에 줄만 맞은 빈 지적을 붙인다 (함정에 걸릴 지적을 줄만 맞은 짝이 가져가지 않게).
- 함정 걸림은 감점하지 않고 **"검토 필요"**로 따로 보인다 (다른 관점에서 타당할 수 있음). 짝 없는 지적은 새 지적 후보로 기록만 한다.
- 결과: 화면 표 + `<골든 폴더>/results/<날짜>_<라벨>.jsonl` (사례별 점수·짝 근거·runId·모델·토큰·입력 해시·provenance). 자동 짝은 틀릴 수 있으니 근거를 보고 확인한다.

**crash 사례** (`kind: crash`, `mode: crash` — 계획서 `2026-10-19_골든-crash사례`)
- 모드 전체(Sequential, 분류 → 웹검색)를 run_mode와 같은 프롬프트 조립으로 돌린다. `input.md`는 run_mode의 `input`이다 (서버가 `## 요청`으로 감쌈, 첨부 없음). `score` 명령은 안 된다 (검색 기록이 필요).
- 모드는 단계 2개 이상 + 마지막 단계에 WebSearch가 있어야 한다 (1단계 = 분류, 마지막 = 검색으로 채점). 아니면 실행 전 거절 — case.json에 `mode: crash`를 꼭 적는다 (빠뜨리면 기본값 review라 거절됨). 실행 전 확인과 단계 수 · 에이전트 · 지침이 달라지면 그 사례부터 멈춘다.
- `expected.json` 항목: `{ id, step: triage|search, summary, keywords, urls?, severity }` (files·lines 없음, urls에 빈 조각 금지). `traps.json`은 `step`을 비우면 두 단계 모두.
- 채점 (`GoldenCrashScorer`): triage = 분류(1단계) 본문 키워드. search = 검색 단계가 **실제로 본 URL**(참고 · 인용 · 직접 연 페이지)에 `urls` 조각이 있으면 찾음 — 근거에 "인용·열람한 URL"인지 "검색 결과에만 있던 URL"인지 남긴다. 조각 뒤가 영숫자면 안 맞는다 (`issues/8932` ≠ `issues/89321`). `urls`가 있는데 본문 키워드만 맞거나 조각이 검색 결과에 없는 URL에만 있으면 "확인 필요"(찾음에 안 셈, 놓침에 듦), `urls`가 없는 항목은 키워드로 찾음. 함정은 키워드가 확신 표기(높음 · 확인됨 · 유력 · high · confirmed)와 **같은 줄**에 있을 때만 걸린다. 키워드는 부정 문맥에서도 맞으니 근거를 본다.
- 검색 단계 호출 기록(WebSearch)을 못 읽으면 점수 대신 채점 실패로 남긴다 ("검색 0회"와 구분).
- 결과 줄에 검색 수 · 본 URL 수 · 검색 결과에 없는 URL 목록. 합계는 리뷰와 따로 `[crash 합계] 분류 찾음 · 검색 찾음`.

## 빌드 에러가 나면
- `AsIChatClient` / `AsBuilder` / `ConfigureOptions` 없음 → `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI`를 `--prerelease`로 다시 추가
- `response.Usage` 없음 → `AgentRunner.cs`의 `InputTokens` / `OutputTokens` 두 줄 삭제
- `WithTools` / `McpServerTool` 관련 → `ModelContextProtocol`을 `--prerelease`로 다시 추가
- 에러 메시지와 `dotnet list package` 결과를 같이 보면 대부분 바로 원인이 나온다
