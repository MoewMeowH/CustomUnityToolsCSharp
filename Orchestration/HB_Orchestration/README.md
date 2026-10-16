# HB_Orchestration

Claude Code(구독)를 지휘자로, 외부 모델(GPT · Gemini · 로컬)을 **모드** 단위로 부려 쓰는 개인용 오케스트레이션.

```
Claude Code ──MCP──▶ HB.Orchestration.Mcp
  list_modes          │
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
└─ project-files/                 ← Unity 프로젝트 루트에 복사할 파일
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
- publish는 `appsettings.json` / `modes.json`을 **복사하지 않는다** (publish 폴더에서 고친 설정을 덮어쓰지 않도록). 처음 publish할 때만 배치가 기본 파일을 넣어준다.
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
| `Agents[].Name` | 에이전트 이름 (영문·숫자·`_`·`-`, 64자 이내). modes.json의 `Agent`가 이 이름을 가리킨다 |
| `Agents[].Provider` | 위 `Providers`의 이름 |
| `Agents[].Model` | 제공사의 모델 ID |
| `Agents[].MaxOutputTokens` | 응답 상한 (생략 = 제한 없음) |
| `Agents[].TimeoutSeconds` | 요청 하나의 네트워크 타임아웃(초). **에이전트 → 제공사 → SDK 기본 100초** 순. 긴 응답을 쓰는 모델(sol 등)만 길게 잡을 때 사용. 대시보드 에이전트 표의 "타임아웃(초)" 칸 |
| `Agents[].MaxAttempts` | 한 단계의 최대 시도 횟수 (첫 시도 포함, 1~5). **에이전트 → 제공사 → 2**(= 최대 1회 재시도) 순. 아래 "재시도" 참고 |
| `Agents[].Enabled` | `false`면 끔 (생략 = 켬) |
| `Budget.DailyTokenLimit` | 하루 입력+출력 토큰 상한 (0 = 끔). `FreeProviders`는 집계 제외 |
| `Budget.MaxInputChars` | `run_mode` 입력 최대 글자 수 (input + `context` 첨부 합계, 기본 100,000). Debate는 토론자가 매 턴 요청 전체를 받으므로 비용이 턴 수만큼 곱해짐 |
| `Context.MaxRanges` | `context` 범위 개수 상한 (기본 30) |
| `Context.MaxFileBytes` | 읽기 전에 확인하는 파일 크기 상한 (기본 2,000,000바이트) |
| `Context.DenyPatterns` | 외부로 보내지 않을 파일 glob **추가분**. 내장 목록(`.env`, `*.pem`, `*.key`, `*.keystore`, `*.jks`, `*.p12`, `*.pfx`, `*.mobileprovision`, `credentials*`, `secrets*`, `.git/**` 등)은 항상 적용 |
| `Budget.DefaultMaxRunsPerSession` | `MaxRunsPerSession`이 없는 모드의 세션당 실행 한도 |
| `Budget.FreeProviders` | 비용 집계에서 뺄 제공사 (기본 `local`) |
| `Pricing.KrwPerUsd` | 1달러당 원. 비우면 대시보드는 달러만 표시 |
| `Pricing.Models.<모델 ID>` | `InputPerMillion`, `CachedInputPerMillion`(비우면 입력과 같음), `OutputPerMillion` — USD / 1M 토큰. 키는 에이전트 이름이 아니라 **모델 ID**(기록에 남는 값). 지금 안 쓰는 예전 모델도 남겨 두면 예전 기록의 비용이 계속 계산됨. 서버는 쓰지 않고 대시보드만 읽음 |

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

내장 역할 `Reviewer`, `Critic`은 코드에 들어 있고, 같은 이름으로 정의하면 덮어쓴다.

### 모드 (`Modes.<이름>`)
| 키 | 값 | 의미 |
|---|---|---|
| `Description` | 문자열 | `list_modes`에서 Claude에게 보이는 한 줄 설명. 짧을수록 토큰 절약 |
| `Flow` | `Sequential` / `Concurrent` / `Debate` | 순서대로 넘기기 / 동시에 독립 실행 / 다회차 토론 |
| `Steps` | 배열 | 단계 목록 (아래 표) |
| `Return` | `Last`(기본) / `All` | Claude에게 돌려줄 범위. Concurrent는 항상 All |
| `MaxInputChars` | 숫자 | 이 모드의 입력 길이 상한. 없으면 `Budget.MaxInputChars` |
| `MaxRunsPerSession` | 숫자 | Claude Code 세션 하나에서 실행할 수 있는 횟수 (0 = 무제한). 없으면 `Budget.DefaultMaxRunsPerSession` |
| `MaxRounds` (Debate) | 1~5 | 최대 회차. 1회차 = 두 토론자가 한 번씩. 기본 2, 서버 절대 상한 5 |
| `HistoryWindow` (Debate) | 숫자 | 각 턴이 볼 직전 발언 수 (기본 2). 판정자는 항상 전체 |

### 단계 (`Steps[]`)
| 키 | 값 | 의미 |
|---|---|---|
| `Role` | 역할 이름 | 이 단계가 맡을 역할 |
| `Agent` | 에이전트 이름 | appsettings.json `Agents`의 `Name` |
| `Sees` (Sequential) | `Input` / `Previous` / `InputAndPrevious`(기본) / `All` | 이 단계가 볼 내용: 요청만 / 앞 단계만 / 요청+앞 단계 / 요청+앞 단계 전부. 뒤로 갈수록 비쌈. 첫 단계는 항상 요청만 |

### Debate 흐름
`Steps[0]`과 `Steps[1]`이 `Steps[0]`부터 번갈아 발언하고, `Steps[2]`가 있으면 마지막에 판정한다.
토론자는 매 발언 마지막 줄에 `[계속]` 또는 `[합의]`를 쓰고, `[합의]`가 나오면 남은 회차를 건너뛰고 바로 판정으로 간다.
토큰은 회차가 늘수록 빠르게 늘어나므로 `MaxRounds`는 2, 많아야 3을 권장. 실행 중 실패하면 판정 없이 중단.

### 실행 한도
세션당 실행 한도는 MCP 서버 프로세스(= Claude Code 세션 하나)별로 센다. 새 세션을 열면 0부터 다시 센다. 검증 단계에서 거절된 호출(설정 오류, 입력 초과 등)은 횟수에 포함되지 않는다.

한도에 걸렸는데 더 필요하면 **서버를 재시작하지 말고** 대시보드 **세션** 화면에서 그 세션의 횟수를 초기화한다. 서버를 다시 띄우려면 Claude Code 세션을 새로 열어야 해서 진행 중이던 대화 맥락까지 잃는다.
- 서버는 세션 상태를 `%LOCALAPPDATA%/HB_Orchestration/sessions/{PID}.json`에 남기고, 대시보드는 초기화 요청을 `{PID}.reset`으로 남긴다. 서버가 다음 `run_mode`·`list_modes` 때 그걸 읽고 적용한다.

### 실행할 때만 에이전트 바꾸기
`run_mode`의 `agents` 인자로 modes.json을 고치지 않고 이번 실행만 바꿀 수 있다. Claude Code에서는 "panel을 gpt_high_sol이랑 gemini_argon으로 돌려줘"처럼 말하면 된다.
- `"gpt_main_tera=gpt_high_sol"` — 그 에이전트만 교체
- `"*=gpt_high_sol"` — 모든 단계를 교체 (panel처럼 모델 비교가 목적인 모드에는 쓰지 말 것)
- 교체 대상은 appsettings에 등록·활성화된 에이전트여야 하고, 잘못되면 토큰 없이 거절된다. 결과 끝에 `(에이전트 교체: a→b)`가 붙고 대시보드에는 실제 실행한 에이전트로 기록된다.
- 교체 실행도 세션당 실행 횟수에 포함된다.

## 4. 토큰 절약 장치

| 장치 | 어디서 | 효과 |
|---|---|---|
| 도구 3개(`list_modes`, `run_mode`, 확인용 `server_info`)만 노출 | MCP | 모드가 늘어도 Claude 컨텍스트의 도구 설명이 그대로 |
| 실행 전 검증 | 서버 | 모드·에이전트·역할 오류, 입력 초과, 예산 초과면 **토큰 0으로** 즉시 반환 |
| 단계별 `Sees` | modes.json | 다음 단계에 필요한 것만 전달 (예: research 검증자는 후보 목록만) |
| `Return: Last` | modes.json | 최종 판정만 필요하면 Claude가 읽는 양 감소 |
| 앞 단계 실패 시 중단 | 서버 | 의미 없는 뒤 단계 호출 안 함 |
| 공통 출력 규칙 | 서버 | 서론·반복·원문 재인용 금지 → 출력 토큰 감소 |
| 고정 시스템 프롬프트 앞부분 | 서버 | 역할+규칙+지침이 매번 같아 제공사 프롬프트 캐시 적중 |
| `UseGuidelines: false` | modes.json | 코드와 무관한 역할에 지침을 안 붙임 |
| `MaxOutputTokens` | appsettings | 응답 폭주 방지 |
| `Budget.DailyTokenLimit` | appsettings | 하루 상한 도달 시 실행 거부 (로컬 제외) |
| `MaxRounds` · `HistoryWindow` · `[합의]` 조기 종료 | modes.json | 다회차 토론의 누적 비용 제한 |
| `MaxRunsPerSession` | modes.json | 리뷰→수정→재리뷰, 추가 조사 반복 같은 루프를 세션 단위로 차단. 기본값은 `Budget.DefaultMaxRunsPerSession` |
| `files`는 에이전트에 안 보냄 | 서버 | 경로 목록은 기록·링크 전용 |
| `context`로 코드 첨부 | 서버 | Claude가 이미 읽은 코드를 input에 다시 쓰지 않음 → **Claude 출력 토큰 절약**. 서버가 `{path, startLine, endLine}`을 디스크에서 읽어 줄 번호와 함께 붙임. 루트 밖·링크(심볼릭·junction)·전송 금지 파일·범위 오류·크기 초과는 호출 전에 거절. 첨부한 범위는 기록의 `Context`, 내용은 `Prompt`에 남음. 8.3 짧은 이름도 긴 이름으로 바꿔 금지 패턴을 검사. 한계: 검사와 읽기 사이에 링크로 바꿔치기하는 경쟁은 막지 않음(단일 사용자 PC 전제) |
| CLAUDE.md의 input 규칙 | Unity 쪽 | diff·최소 자료만, 기본은 호출하지 않기 |

## 5. Unity 프로젝트 쪽
`project-files/` 내용을 Unity 프로젝트 루트로 복사:
- `CLAUDE.md` — 언제 어떤 모드를 쓰고, input을 어떻게 줄이고, 결과를 어떻게 종합할지
- `REVIEW_GUIDELINES.md` — 공통 리뷰 기준 (서버가 자동으로 찾아 주입)
- `.claude/commands/cross-review.md` — `/cross-review`, `/cross-review panel`
- `.claude/commands/mode.md` — `/mode debate ...`, `/mode deliberate ...`, `/mode research ...`, `/mode parallel ...`
- `.claude/commands/unity-test.md` — `/unity-test`, `/unity-test PlayMode MyGame.Combat`
- `.claude/settings.json` — Unity CLI 조회·테스트 명령은 매번 승인 없이 실행 (기존 파일이 있으면 `allow` 항목만 합치기)
- `.mcp.json` — dll 경로를 publish 폴더로 (JSON 안에서는 경로를 `/`로 쓸 것. `\`는 JSON에서 특수 문자)

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
- 상세: 요청/응답, **같은 실행의 다른 단계와 실행 전체 토큰 합계**, 관련 파일 → Rider로 열기
- 비용: **달러와 원화를 함께** 표시 (`$0.0416 (₩57)`). 캐시된 입력 토큰은 캐시 단가로 계산
  - 계산: `(입력 - 캐시) × 입력 단가 + 캐시 × 캐시 단가 + 출력 × 출력 단가` (입력 토큰에는 캐시 토큰이 포함됨)
  - 단가·환율은 MCP `appsettings.json`의 `Orchestration:Pricing`에 USD로 저장하고, **모드 화면의 "모델 단가" 표에서 편집**한다
  - 캐시 토큰 수는 이 기능을 넣은 뒤의 호출부터 기록된다 (그 전 기록은 전부 일반 입력 단가로 계산)

**모드** (`/Modes`)
- MCP publish 폴더의 `modes.json`을 읽어 모드마다 흐름을 그림으로 보여준다 (순차 → / 동시 묶음 / 토론 ⇄ → 판정).
- 단계별 에이전트(드롭다운), 세션당 실행 한도, 최대 회차, 반환 범위를 바꿔 **저장**하면 다음 `run_mode`부터 바로 반영된다 (재시작 불필요).
- **모델 단가** 표: 설정에 단가가 있는 모델, 켜진 에이전트가 쓰는 모델, 호출 기록에 나온 모델을 모두 한 줄씩 보여주고(쓰는 에이전트·기록 횟수 표시, 단가 없는 모델 강조), **입력 / 캐시 입력 / 출력 단가(USD / 1M 토큰)**와 환율을 편집한다. 에이전트와 상관없이 모델 단위라 예전 모델도 계속 관리할 수 있다. 세 칸을 다 비우면 그 모델의 단가를 지우고, 마지막 빈 줄로 새 모델을 추가한다.
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

## 8. HB_Orchestration.bat (publish · 대시보드 실행)

솔루션 폴더의 `HB_Orchestration.bat`을 더블클릭하면 메뉴가 뜬다.

| 메뉴 | 동작 |
|---|---|
| 1 | MCP 서버 publish → `PUBLISH_DIR`. 실행 중인 MCP 서버가 있으면 **서버만 종료할지 묻고** 진행. 설정 파일은 백업 후 그대로 유지 |
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

**기타**
- 대시보드 창을 닫으면 대시보드가 꺼진다.

## 빌드 에러가 나면
- `AsIChatClient` / `AsBuilder` / `ConfigureOptions` 없음 → `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI`를 `--prerelease`로 다시 추가
- `response.Usage` 없음 → `AgentRunner.cs`의 `InputTokens` / `OutputTokens` 두 줄 삭제
- `WithTools` / `McpServerTool` 관련 → `ModelContextProtocol`을 `--prerelease`로 다시 추가
- 에러 메시지와 `dotnet list package` 결과를 같이 보면 대부분 바로 원인이 나온다
