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
├─ Directory.Build.props          ← 공통 설정
├─ src/
│  ├─ HB.Orchestration.Shared/    ← 호출 기록 모델 + JSONL 저장
│  ├─ HB.Orchestration.Agents/    ← 제공사·에이전트·모드 실행기 (호스트와 무관한 핵심)
│  ├─ HB.Orchestration.Mcp/       ← MCP 서버, appsettings.json, modes.json
│  └─ HB.Orchestration.Dashboard/ ← 호출 기록 UI
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
dotnet publish src/HB.Orchestration.Mcp -c Release -o <publish 폴더>
```

- publish 출력은 **솔루션 폴더 밖**으로. `dotnet run`으로 MCP 등록하지 말 것.
- .NET 10 SDK가 없으면 `Directory.Build.props`의 `net10.0` → `net9.0`.

## 2. 에이전트 — `appsettings.json` ("누가")

| 에이전트 | 제공사 | 용도 |
|---|---|---|
| `gpt` | openai (상위 모델) | 검증·판정처럼 정확도가 중요한 단계 |
| `gpt_mini` | openai (저가 모델) | 정리·탐색처럼 분량이 많은 단계 |
| `gemini` | gemini (OpenAI 호환 엔드포인트) | 다른 관점의 검증, 기본 꺼짐 |
| `local` | Ollama / LM Studio | 무료 초안·병렬 작업, 기본 꺼짐 |

- `MaxOutputTokens`: 응답 상한. 추론 모델은 생각 토큰도 포함되니 너무 낮게 잡지 말 것.
- 키가 없거나 설정이 틀린 에이전트는 그것만 건너뛰고 서버는 정상 시작. `list_modes`에 `(사용불가)`로 표시.
- 바꾸면 Claude Code 재시작 필요.

## 3. 모드 — `modes.json` ("무엇을 어떤 순서로")

**실행할 때마다 다시 읽으므로 저장만 하면 바로 반영**. 찾는 순서:
`ModesPath`(appsettings) → `HB_MODES_PATH` 환경 변수 → **Unity 프로젝트 루트의 `hb-modes.json`** → 서버 폴더의 `modes.json`

> publish를 다시 하면 서버 폴더의 `modes.json`이 덮어써질 수 있다. 직접 고친 모드는 `HB_MODES_PATH`로 솔루션 밖 파일을 가리키거나, 프로젝트별로 `hb-modes.json`에 두는 걸 권장.

```jsonc
"Roles": {
  "Rebuttal": { "Prompt": "너는 반박자다 ...", "UseGuidelines": true }
},
"Modes": {
  "debate": {
    "Description": "주장/설계에 반박 후 양쪽 논리 검증",
    "Flow": "Sequential",            // Sequential | Concurrent
    "Return": "All",                 // Last(기본) | All
    "Steps": [
      { "Role": "Rebuttal",   "Agent": "gpt" },
      { "Role": "LogicCheck", "Agent": "gemini", "Sees": "InputAndPrevious" }
    ]
  }
}
```

| 키 | 값 | 의미 |
|---|---|---|
| `Flow` | `Sequential` / `Concurrent` / `Debate` | 순서대로 넘기기 / 동시에 독립 실행 / 다회차 토론 |
| `Sees` (단계별) | `Input` / `Previous` / `InputAndPrevious` / `All` | 이 단계가 볼 내용. 뒤로 갈수록 비쌈 |
| `Return` | `Last` / `All` | Claude에게 돌려줄 범위 (Concurrent는 항상 All) |
| `MaxInputChars` | 숫자 | 이 모드의 입력 길이 상한 |
| `MaxRunsPerSession` | 숫자 | Claude Code 세션 하나에서 이 모드를 실행할 수 있는 횟수 (0 = 무제한) |
| `MaxRounds` (Debate) | 1~5 | 최대 회차. 1회차 = 두 토론자가 한 번씩. 서버 절대 상한 5 |
| `HistoryWindow` (Debate) | 숫자 | 각 턴이 볼 직전 발언 수 (기본 2). 판정자는 항상 전체 |

### Debate 흐름
`Steps[0]`과 `Steps[1]`이 `Steps[0]`부터 번갈아 발언하고, `Steps[2]`가 있으면 마지막에 판정한다.
토론자는 매 발언 마지막 줄에 `[계속]` 또는 `[합의]`를 쓰고, `[합의]`가 나오면 남은 회차를 건너뛰고 바로 판정으로 간다.
토큰은 회차가 늘수록 빠르게 늘어나므로 `MaxRounds`는 2, 많아야 3을 권장. 실행 중 실패하면 판정 없이 중단.
| `UseGuidelines` (역할별) | `true` / `false` | REVIEW_GUIDELINES.md를 붙일지 |

세션당 실행 한도는 MCP 서버 프로세스 메모리에서 센다. Claude Code 세션마다 서버가 새로 뜨므로 새 세션을 열면 0부터 다시 센다. 검증 단계에서 거절된 호출(설정 오류, 입력 초과 등)은 횟수에 포함되지 않는다.

기본 모드: `review`, `panel`, `critique`, `debate`, `research`, `parallel`. 내장 역할 `Reviewer`, `Critic`은 같은 이름으로 정의하면 덮어쓴다. 역할 프롬프트가 길어지면 `PromptFile`로 분리 가능 (modes.json 기준 상대 경로).

## 4. 토큰 절약 장치

| 장치 | 어디서 | 효과 |
|---|---|---|
| 도구 2개(`list_modes`, `run_mode`)만 노출 | MCP | 모드가 늘어도 Claude 컨텍스트의 도구 설명이 그대로 |
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
| CLAUDE.md의 input 규칙 | Unity 쪽 | diff·최소 자료만, 기본은 호출하지 않기 |

## 5. Unity 프로젝트 쪽
`project-files/` 내용을 Unity 프로젝트 루트로 복사:
- `CLAUDE.md` — 언제 어떤 모드를 쓰고, input을 어떻게 줄이고, 결과를 어떻게 종합할지
- `REVIEW_GUIDELINES.md` — 공통 리뷰 기준 (서버가 자동으로 찾아 주입)
- `.claude/commands/cross-review.md` — `/cross-review`, `/cross-review panel`
- `.claude/commands/mode.md` — `/mode debate ...`, `/mode deliberate ...`, `/mode research ...`, `/mode parallel ...`
- `.claude/commands/unity-test.md` — `/unity-test`, `/unity-test PlayMode MyGame.Combat`
- `.claude/settings.json` — Unity CLI 조회·테스트 명령은 매번 승인 없이 실행 (기존 파일이 있으면 `allow` 항목만 합치기)
- `.mcp.json` — `<publish 폴더>`를 위 publish `-o` 경로로 바꾸면 됨

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

```powershell
dotnet run --project src/HB.Orchestration.Dashboard    # → http://localhost:5280
```

- 기간 · 모드 · 제공사 필터, 호출 수 · 토큰 · 평균 응답 · 예상 비용 · 실패, 일별 토큰
- 상세: 요청/응답, **같은 실행의 다른 단계와 실행 전체 토큰 합계**, 관련 파일 → Rider로 열기
- 비용: `appsettings.json`의 `Prices`에 모델별 1M 토큰 단가

## 빌드 에러가 나면
- `AsIChatClient` / `AsBuilder` / `ConfigureOptions` 없음 → `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI`를 `--prerelease`로 다시 추가
- `response.Usage` 없음 → `AgentRunner.cs`의 `InputTokens` / `OutputTokens` 두 줄 삭제
- `WithTools` / `McpServerTool` 관련 → `ModelContextProtocol`을 `--prerelease`로 다시 추가
- 에러 메시지와 `dotnet list package` 결과를 같이 보면 대부분 바로 원인이 나온다
