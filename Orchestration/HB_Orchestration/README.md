# HB_Orchestration

Claude Code(구독)를 지휘자로, 외부 모델(GPT · Gemini · 로컬)을 **모드** 단위로 부려 쓰는 개인용 오케스트레이션.

```
Claude Code ──MCP──▶ HB.Orchestration.Mcp   (도구 5개)
  run_mode(mode, …)   ├─ modes.json   "무엇을 어떤 순서로"  (실행마다 다시 읽음 → 수정 즉시 반영)
  list_modes          │    review_main   : Reviewer@gpt_main_sol
  save_result         │    review_high   : Reviewer@gpt_main_sol | Reviewer@deepseek_main_flash (동시)
  record_verdicts     │    design_main   : Rebuttal@gpt_main_sol → LogicCheck@gemini_main_flash
  server_info         │    design_high   : Rebuttal@sol ⇄ Proponent@gemini_main_flash (최대 2회차) → Judge@sol
                      │    research_main : Scout@gpt_mini_luna_web → Feasibility@sol   … (전체는 list_modes)
                      ├─ appsettings  "누가"  (제공사·모델·출력 상한·단가)
                      └─ 시도마다 기록 ──▶ %LOCALAPPDATA%\HB_Orchestration\calls.jsonl ◀── Dashboard (.cshtml)
```

> 본문의 `docs/plans/…` · `docs/worklog/…` 링크는 이 PC에만 있는 작업 기록이다 (`docs/`는 git 제외).

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
├─ tests/HB.Orchestration.Tests/  ← xunit (Agents · Dashboard 참조)
├─ project-files/                 ← Unity 프로젝트 쪽 규칙서·명령·서브에이전트·훅의 원본 + 설정 템플릿 (*.example.json). 폴더째 붙여넣지 않는다 (5절)
└─ tools/
   ├─ Sync-ProjectFiles.ps1       ← project-files → Unity 프로젝트 비교·복사
   ├─ Test-SyncProjectFiles.ps1   ← 위 스크립트 시험 (임시 폴더만)
   ├─ sync-targets.example.txt    ← -All 대상 목록 견본 (복사해 sync-targets.local.txt, git 제외)
   └─ golden/                     ← 골든 회차 실행 스크립트 (run_golden.sh · test_run_golden.sh, 9절)
```

## 1. 패키지 & 빌드

패키지는 csproj에 버전을 고정해 두었다 (`dotnet add package` 불필요). 복원 · 빌드 · 테스트:

```powershell
dotnet build HB_Orchestration.slnx
dotnet test tests/HB.Orchestration.Tests
```

| 프로젝트 | 패키지 |
|---|---|
| Agents | `Microsoft.Agents.AI` 1.25.0 · `Microsoft.Extensions.AI` 10.11.0 · `Microsoft.Extensions.AI.OpenAI` 10.11.0 · `Microsoft.Extensions.Logging.Abstractions` 10.0.13 |
| Mcp | `ModelContextProtocol` 2.2.0 · `Microsoft.Extensions.Hosting` 10.0.13 |
| Dashboard | `Markdig` 1.4.0 |

올릴 때는 csproj 버전을 바꾸고 전체 테스트를 돌린다.

평소 publish와 대시보드 실행은 **`HB_Orchestration.bat`** 으로 한다 (아래 8절).

- publish 출력은 **솔루션 폴더 밖**으로. `dotnet run`으로 MCP 등록하지 말 것.
- publish는 `appsettings.json` / `modes.json`을 **복사하지 않는다** (publish 폴더에서 고친 설정을 덮어쓰지 않도록). 처음 publish할 때만 배치가 기본 파일을 넣어준다. 원본에 새로 생긴 키는 publish 끝에 `[알림]`으로 보여 준다 (8절).
- .NET 10 SDK가 없으면 `Directory.Build.props`의 `net10.0` → `net9.0`.

## 2. 에이전트 — `appsettings.json` ("누가")

에이전트 하나 = 제공사 + 모델 + 출력 상한. 이름은 자유 (영문·숫자·`_`·`-`). 등급별로 나눠두면 모드에서 고르기 쉽다.

원본 `src/HB.Orchestration.Mcp/appsettings.json`의 에이전트 (이름 = `{회사}_{등급}_{모델 별칭}`):

| 이름 | 제공사 | 모델 | 쓰는 곳 |
|---|---|---|---|
| `gpt_high_astra` | openai | gpt-6-astra | 교체용 상위 모델 (기본 모드에는 없음, 2026-10-24 이 PC 사용 중 설정은 `Enabled: false`) |
| `gpt_main_sol` | openai | gpt-6.1-sol | 리뷰 · 반박 · 판정 · 성능 · preflight 대부분 |
| `gpt_mini_luna` | openai | gpt-6-luna | 지시서 검토 (work_order_main) |
| `gpt_mini_luna_web` | openai-web | gpt-6-luna | 웹검색 단계 (research_main Scout · crash_main CrashSearch) |
| `gemini_high_argon` | gemini | gemini-4-argon | crash 분류 · preflight B · preflight_check |
| `gemini_main_flash` | gemini | gemini-3.8-flash | 논리 검증 · 토론 상대 · parallel · 지시서 검토 |
| `gemini_mini_lite` | gemini | gemini-3.5-flash-lite | 가벼운 교체용 |
| `deepseek_main_flash` | fireworks | `accounts/fireworks/models/deepseek-v4p1-flash-1017` | review_high · perf_high의 두 번째 시선 (`Vision: false`) |
| `local` | local (Ollama) | `ollama list`의 모델 | 꺼 둠 (`Enabled: false`) |

제공사는 `openai` · `openai-web` · `gemini` · `fireworks` · `local` · `anthropic-cli` 여섯이다 (Type은 `openai-web`만 `openai-responses`, `anthropic-cli`는 `claude-cli`, 나머지는 `openai-compatible`). 실제 쓰는 값은 publish 폴더의 appsettings.json이고, `list_modes` 마지막의 `에이전트:` 줄에 보인다.

- `MaxOutputTokens`: 응답 상한. **추론 모델은 생각 토큰도 포함**되므로 낮게 잡으면 답변 없이 끝난다 (서버가 `[빈 응답]` 실패로 기록). 폭주 방지용으로 넉넉하게 (1만 이상 권장).
- 키가 없거나 설정이 틀린 에이전트는 그것만 건너뛰고 서버는 정상 시작. `list_modes`에 `(사용불가)`로 표시.
- **저장하면 바로 반영된다.** 서버가 호출 때마다 파일 수정 시각을 보고, 바뀌었으면 설정·에이전트를 다시 읽는다 (재시작 불필요). JSON이 깨졌으면 이전 설정으로 계속 동작하고 `list_modes`·`server_info`에 경고가 뜬다.
- 대시보드 **모드** 화면에서 추가·수정·삭제할 수 있고, **이름을 바꾸면 그 이름을 쓰는 modes.json 단계도 같이 바뀐다** (7절).
- appsettings.json도 주석 없는 순수 JSON으로 둔다. 필드 설명은 아래 표.

### appsettings.json 필드 (`Orchestration` 섹션)
| 키 | 의미 |
|---|---|
| `ModesPath` | modes.json 위치를 직접 지정 (비우면 3절의 찾는 순서) |
| `GuidelinesPath` | 리뷰 지침 파일 (비우면 `REVIEW_GUIDELINES_PATH` 환경 변수 → Unity 프로젝트 루트의 `REVIEW_GUIDELINES.md`). 직접 지정한 경로(이 설정 또는 환경 변수)에 파일이 없으면 다음 후보로 넘어가지 않고 run_mode가 `[리뷰 지침 경로 없음]`으로 거절한다 (2026-10-22, modes.json 지정 경로와 같은 규칙) |
| `PromptsPath` | 모델별 덧붙임(외부 에이전트 실수 목록) 폴더. 원본은 `project-files/prompts`, 상대 경로는 서버 폴더 기준. 비우면 modes.json 옆 `prompts/`. 지정했는데 폴더가 없으면 `[모델별 덧붙임 폴더 오류]`으로 거절한다 ("모델별 덧붙임") |
| `Providers.<이름>.Type` | `openai-compatible`(Chat Completions — OpenAI · Gemini · Fireworks · Ollama · LM Studio) · `openai-responses`(OpenAI Responses API, 웹검색 단계용) · `claude-cli`(아래 "claude-cli 제공사") |
| `Providers.<이름>.ExecutablePath` | `claude-cli` 전용: Claude Code 실행 파일. 비우면 PATH의 `claude` |
| `Providers.<이름>.Endpoint` | API 주소. OpenAI는 비움, Gemini `https://generativelanguage.googleapis.com/v1beta/openai/`, Fireworks `https://api.fireworks.ai/inference/v1`, Ollama `http://localhost:11434/v1`, LM Studio `http://localhost:1234/v1` |
| `Providers.<이름>.ApiKeyEnv` | API 키를 담은 **환경 변수 이름** (`OPENAI_API_KEY` · `GEMINI_API_KEY` · `FIREWORKS_API_KEY`). 로컬처럼 키가 필요 없으면 생략. 키 값은 서버 프로세스가 시작될 때 물려받은 값만 보인다 |
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
| `Agents[].MaxTurns` · `Agents[].MaxBudgetUsd` | `claude-cli` 전용: 도구 턴 상한(`--max-turns`, 생략 = 40) · 실행 1회 비용 상한 USD(`--max-budget-usd`, CLI 추정 기준이라 한 응답만큼 넘을 수 있음, 생략 = 없음) |
| `Budget.FreeProviders` | 대시보드 비용 집계 · 오늘 사용량에서 뺄 제공사 (기본 `local`). 설정 값은 기본값 뒤에 **덧붙으므로** `local`은 항상 빠진다 (`[]`로 써도 마찬가지). 하루 토큰·비용 상한(`DailyTokenLimit` · `DailyCostLimitUsd`)은 2026-10-20에 없앴다 — 예전 키가 남아 있어도 무시 |
| `Budget.MaxInputChars` | `run_mode` 입력 최대 글자 수 (input + `context` 첨부 합계, 기본 350,000). Debate는 토론자가 매 턴 요청 전체를 받으므로 비용이 턴 수만큼 곱해짐 |
| `Context.MaxRanges` | `context` 범위 개수 상한 (기본 30) |
| `Context.MaxFileBytes` | 읽기 전에 확인하는 파일 크기 상한 (기본 2,000,000바이트) |
| `Context.DenyPatterns` | 외부로 보내지 않을 파일 glob **추가분**. 내장 목록(`.env`, `.env.*`, `*.pem`, `*.key`, `*.p8`, `*.p12`, `*.pfx`, `*.keystore`, `*.jks`, `*.mobileprovision`, `*.provisionprofile`, `credentials*`, `secrets*`, `.git/**`, `**/.git/**`)은 항상 적용 |
| `Context.WriteDenyPatterns` | save_result가 쓰지 않을 파일 glob **추가분** (쓰기에만 적용). 내장 쓰기 금지 목록(실행·설정 파일, 아래 save_result 절)과 `DenyPatterns`는 항상 적용 |
| `Context.ExtraRoots` | 프로젝트 루트 밖에서 `context`로 **읽을** 수 있는 추가 루트 (`이름 → 절대 경로`, 기본 비어 있음). 아래 "추가 루트" 참고 |
| `Images.MaxCount` · `Images.MaxBytes` · `Images.MaxDimension` | `run_mode` `images` 상한: 장수(기본 20), 장당 바이트(기본 2,000,000), 긴 변 px(기본 1024). 서버는 이미지를 줄이지 않고 넘으면 거절한다 (축소는 `image_prep.py shrink`). 이미지는 `MaxInputChars`에 들어가지 않고, 토큰은 실제 사용량으로 비용에 잡힌다 |
| `Providers.<이름>.ImageDetail` | 이미지 부분에 실을 `detail` 값 (OpenAI: `auto`·`low`·`high`·`original`). 비우면 보내지 않음 (= 제공사 기본값). Gemini OpenAI 호환 엔드포인트는 이 필드를 문서화하지 않아 제공사 단위로 둔다 |
| `Agents[].Vision` | 이미지 입력을 받는 모델인지 (기본 `true`). `false`인 에이전트가 낀 모드에 `images`가 오면 실행 전에 거절 |
| `Agents[].StructuredOutput` · `Providers.<이름>.StructuredOutput` | 구조화 출력(역할 `Output`) 요청 방식. **에이전트 → 제공사 → `Prompt`** 순. `Prompt` = 시스템 프롬프트에만 JSON 형식을 적음. `Schema` = 거기에 `response_format: json_schema`(strict)까지 보내 강제. 모델이 미지원이면 400 → 그 단계 실패 (자동으로 `Prompt`로 다시 보내지 않음, 이 값을 `Prompt`로 바꿀 것). 기본 설정은 제공사 단위로 `openai`만 `Schema`이고, 에이전트 단위로 `gemini_high_argon` · `deepseek_main_flash`도 `Schema` (아래 "구조화 출력") |
| `Agents[].ReasoningEffort` | 추론 강도 `none` · `low` · `medium` · `high` · `xhigh` (대소문자 무시). **에이전트 전용** (같은 제공사라도 모델마다 받는 값이 달라 제공사 기본값은 없음). 비우면 보내지 않음 = 제공사·모델 기본값. 다른 값이면 그 에이전트를 건너뛴다. Chat Completions는 `reasoning_effort`, Responses(openai-web)는 `reasoning.effort`로 나간다. 실행마다 `run_mode`의 `reasoning`(`"에이전트=강도"` · `"*=강도"`, 에이전트는 `agents` 교체 뒤 이름) · 골든 `--reasoning`으로 덮어쓴다. followUp은 원래 호출 값을 그대로 쓴다. 추론 토큰도 `MaxOutputTokens` 안에 들어가므로 강도를 올리면 상한에 닿아 본문 없이 끝날 수 있다 (2026-10-20 sol·argon·deepseek 64,000으로 올림). 호출 기록 `ReasoningEffort`에 실제로 보낸 값이 남고, `list_modes` 에이전트 줄에 설정 값이 보인다. 대시보드 에이전트 편집에서도 고친다. 제공사별 받는 값: OpenAI gpt-6.1-sol은 low~xhigh(none·minimal 400, 기본 medium) · Gemini OpenAI 호환은 low~high(xhigh 매핑 없음, 3.x·4는 끌 수 없음) · Fireworks는 low~xhigh·none (모델별 기본값 문서 없음). **Gemini OpenAI 호환은 강도를 비우면 추론을 거의 하지 않고**(2026-10-21 실측: argon 기본 21초 · total = prompt + completion, high 명시 201초), 추론 토큰을 usage의 `total_tokens`에만 넣는다 → 서버가 Gemini 제공사(엔드포인트 generativelanguage.googleapis.com)에서만 추론 칸이 없고 total > prompt + completion이면 그 차이를 `ReasoningTokens`로 기록하고 `OutputTokens`에 더한다(비용이 출력 단가로 잡힘). **`gemini_high_argon`은 ReasoningEffort가 비어 있어 호환 경로 기본값(추론 거의 없음)으로 동작한다** — 이 에이전트를 쓰는 crash_main · preflight_* 모두 해당 (review_high · perf_high는 2026-10-22부터 DeepSeek, 계획서 `docs/plans/2026-10-22_argon-모드-유지-검토.md`). high를 명시하면 비용 약 6배·시간 약 10배인데 골든 rec-4942 찾음은 1→2/11뿐이라 바꾸지 않았다 (2026-10-21 사용자 결정, 에이전트 이름은 provenance와 연결돼 그대로). appsettings 에이전트의 `Note` 칸에도 같은 설명 (서버가 읽지 않는 메모 칸 — 주석과 달리 대시보드 저장 때 지워지지 않음). 계획서 `docs/plans/2026-10-20_추론강도-설정.md` |
| `ResponseChecks.ExpectedLanguage` | 기대하는 응답 언어. 지금은 `ko`만 지원. **키가 없으면 `ko`(검사 켬), `""`이면 끔** (아래 "응답 검사기") |
| `ResponseChecks.ForeignScriptRatio` | 코드 블록을 뺀 본문에서 일본어 가나 비율, 또는 한자 비율(중국어)이 이 값 이상이면 경고 (기본 0.02 = 2%) |
| `Debug.DumpToolResults` | `true`면 `run_mode`가 반환하는 문자열을 그대로 `%LOCALAPPDATA%/HB_Orchestration/dumps/`에 남긴다 (조사용, 기본 `false`). 결과가 Claude에게 가는 동안 바뀌는지 가를 때 쓰고, 끝나면 끈다 |
| `Records.BlobFolder` | 원문 저장 폴더 (시스템 프롬프트 · 지침 · modes.json · 설정 · 4,096바이트 이상 프롬프트). 비우면 `calls.jsonl` 옆 `blobs/`, 상대 경로면 `calls.jsonl` 폴더 기준 (아래 "호출 기록의 지침·설정 해시") |
| `Records.MaxReasonChars` · `Records.MaxOwnReviewChars` | `run_mode`의 `reason` · `ownReview` 글자 수 상한 (기본 500 · 30,000, 0 = 무제한). 넘으면 잘라 쓰지 않고 거절 (실행 횟수 차감 없음) |
| `Records.StoreSettingsText` | 서버 설정(`appsettings.json`) 원문도 저장할지 (기본 `false` = 해시와 키 패턴 검사 결과만). `true`여도 키 패턴이 보이면 저장하지 않음 |
| `WorkOrders.Folder` | `/work-order`가 지시서를 남길 폴더 (프로젝트 루트 기준 상대 경로). 값이 있으면 `list_modes` 끝에 `지시서 폴더: …` 줄이 붙고, 비우면 줄이 없고 명령은 `docs/work-orders/`를 쓴다. 절대 경로·`..`·`@이름`은 `[주의]`로만 표시. **서버는 이 폴더에 쓰지 않는다** (지휘자가 직접 씀) |
| `Pricing.KrwPerUsd` | 1달러당 원. 비우면 대시보드는 달러만 표시 |
| `Pricing.Models.<모델 ID>` | `InputPerMillion`, `CachedInputPerMillion`(비우면 입력과 같음), `CacheWritePerMillion`(캐시 쓰기, 비우면 입력과 같음 — OpenAI GPT-5.6 이후는 입력의 1.25배, 쓰기 토큰은 호출 기록 `CacheWriteTokens`), `OutputPerMillion` — USD / 1M 토큰. 웹검색 단계를 쓰는 모델에는 `WebSearchPerThousand`(1,000회당 USD)도 넣는다. 키는 에이전트 이름이 아니라 **모델 ID**(기록에 남는 값). 지금 안 쓰는 예전 모델도 남겨 두면 예전 기록의 비용이 계속 계산됨. 위 칸은 **처음 단가**이고, 단가가 바뀌면 칸을 고치지 말고 `Changes[]`에 `{ "From": "yyyy-MM-dd"(UTC 0시부터), 바뀌는 칸만, "Note" }`을 더한다 — 기록 시각 기준으로 그때 단가가 적용된다 (고치면 지난 호출 비용까지 새 단가로 다시 계산됨). 날짜 형식 오류 · 같은 날짜 중복 · 단가 칸 없음 · 음수는 서버가 설정을 읽을 때 경고만 한다. 대시보드 모드 화면은 처음 단가만 고치고 `Changes`는 파일에서 직접 고친다. 단가는 **실효 단가**(할인 후 실제로 내는 값)로 둔다 — 예: `gemini-4-argon`은 $2 / 캐시 $0.10 / $10(11-30까지 소개 가격, 12-01부터 $4/$20). Gemini 콘솔 SKU 금액은 정가($4/$20)로 집계되고 50% 할인이 '절감'에서 상계되므로, 청구 대조는 SKU 금액이 아니라 할인 후 금액이나 크레딧 사용분과 한다 (2026-10-21 크레딧 대조, 모델 항목의 `Note`). 서버는 비용 계산에 쓰지 않고(설정을 읽을 때 `Changes` 문제만 경고) 대시보드 비용 · 골든 실행 전 비용 추정이 쓴다 |

### claude-cli 제공사 (`sub_*` 모드 — 계획서 `docs/plans/2026-10-27_검토형-서브에이전트-API-전환.md`)

검토형 서브에이전트(self-reviewer 등)를 Claude Code 구독 대신 **Anthropic API 키**로 돌린다. 서버가 `claude --bare --restricted -p`를 읽기 전용 에이전트로 띄워, 에이전트가 작업 폴더의 파일을 통째로 직접 읽는다 (지휘자가 고른 범위만 받는 외부 에이전트와 다름). **이 PC의 Claude Code 2.1.311(Windows)은 `--tools`에 Grep · Glob을 줘도 Read만 준다** (2026-10-27 확인) — 검색은 못 하고, 프롬프트의 파일 목록에서 골라 읽는다.
- 실행: `claude --bare --restricted -p --no-session-persistence --output-format json --model <Model> --effort <ReasoningEffort> --max-turns <MaxTurns> [--max-budget-usd <MaxBudgetUsd>] --permission-mode dontAsk --tools Read,Glob,Grep --settings <폴더 밖 읽기 막음> --append-system-prompt-file <역할 + 출력 형식 + 공통 규칙 + 지침>`, 사용자 프롬프트는 stdin, 작업 폴더 = 아래.
  - 확인한 것 (2026-10-27, 실제 실행): `blockReadsOutsideWorkingDirectories`는 상대 · 절대 경로 모두 폴더 밖 읽기를 거절한다. `--bare`만으로는 작업 폴더의 `.claude/settings.json`이 **적용된다** — 그래서 `--restricted`(사용자 · 프로젝트 · 로컬 설정 파일 무시, `--settings`만 적용, 파일 도구를 작업 폴더로 제한)를 함께 쓴다. `--bare -p`는 세션 기록(소스 사본 포함)을 `~/.claude/projects`에 남긴다 — `--no-session-persistence`로 끈다.
  - 실행 파일: `ExecutablePath`(절대 경로만) 또는 PATH의 **절대 경로 항목에서만** `claude.exe`를 찾는다 — 상대 이름을 넘기면 Windows가 서버의 현재 폴더(프로젝트 루트)를 먼저 찾아 거기 놓인 claude.exe가 키를 받을 수 있다. npm 설치본(claude.cmd)은 찾지 않으니 그런 PC는 `ExecutablePath`에 실행 파일을 넣는다.
- **과금**: 자식 프로세스에만 `ANTHROPIC_API_KEY` = `ApiKeyEnv`(기본 `HB_REVIEWER_API_KEY`) 값. `ANTHROPIC_AUTH_TOKEN` · `CLAUDE_CODE_OAUTH_TOKEN` · 클라우드 제공사 플래그 · `ANTHROPIC_BASE_URL`, 키를 담은 원래 변수, 이름이 `_API_KEY` · `_ADMIN_KEY` · `_TOKEN` · `_SECRET`으로 끝나는 변수(다른 제공사 키)는 지운다. 설정 · 실행 기록(record의 ToString)에 키 값은 찍히지 않는다. `--bare`는 구독 로그인(OAuth)을 읽지 않으므로 키가 없으면 구독으로 넘어가지 않고 실패한다. `ApiKeyEnv`에 `ANTHROPIC_API_KEY`를 쓰면 에이전트를 건너뛴다 — 사용자 환경 변수로 두면 Claude Code 자체가 API 과금으로 바뀐다. 키는 각 프로젝트 `.mcp.json`의 `env`에 `"HB_REVIEWER_API_KEY": "${HB_REVIEWER_API_KEY:-}"`로 넘긴다. 환경 변수를 새로 넣었으면 Claude Code(백그라운드에 남은 claude 프로세스 포함)를 모두 끄고 다시 켜야 MCP 서버가 값을 받는다 — /mcp 재연결만으로는 옛 환경 그대로다.
- **작업 폴더**: 서버가 `<데이터 폴더>/review_in/<임의>/`에 `context`가 가리키는 파일을 **파일 전체**로 상대 경로 그대로 복사한다 (경로 규칙 · 추가 루트 `@이름/` · 전송 금지 · 파일 크기 상한은 첨부와 같음, 합계 4,000,000바이트 상한). 프롬프트의 첨부 절에는 본문 대신 **파일 목록(경로 · SHA-256 앞 12자 — 기록에 남아 나중에 대조)**과 "이번 검토의 중심"(경로:줄) 목록만 들어간다. 본문을 보내지 않으므로 모드 입력 상한(MaxInputChars)은 input에만 적용한다. context가 없으면 실행하지 않는다. 한 모드에 다른 제공사 에이전트를 섞을 수 없고, images · followUp은 받지 않는다.
- **결과**: CLI JSON의 `result`가 응답. 토큰은 입력 = 캐시 안 됨 + 캐시 쓰기 + 캐시 읽기, 캐시 읽기 · 쓰기를 따로 기록 (단가 `claude-opus-5-5` 4 / 읽기 0.2 / 쓰기 5 / 출력 20 — 2026-10-27 시험에서 CLI `total_cost_usd`와 일치). usageRaw에 CLI 원문(본문 제외: `total_cost_usd` · `num_turns` · `permission_denials` …). `error_max_turns` · `error_max_budget_usd` · refusal은 재시도 없는 실패 — 이때도 결과 JSON의 토큰 · usageRaw를 실패 기록에 남겨 비용에서 빠지지 않는다. "credit balance" 문구는 상태 코드와 상관없이 Quota, 429 · 5xx는 재시도 규칙대로. CLI가 입력을 읽기 전에 끝나면(인자 오류 · 판 변경) 네트워크 오류가 아니라 CLI 출력으로 실패를 남긴다. 추론 강도 `none`은 거절(Opus 5.5는 thinking을 끌 수 없음). 결과 JSON의 `permission_denials`가 있으면 `[경고] claude-cli 도구 사용 N건이 거절됨`. 결과에 `result` · `usage`가 없으면 CLI 판이 바뀐 것으로 보고 실패한다.
- 시간: 에이전트 · 제공사 `TimeoutSeconds`(없으면 15분)를 넘으면 프로세스 트리를 끝내고 종료를 기다린 뒤(작업 폴더 잠김 방지) Timeout.
- 모드 `sub_self` · `sub_perf` · `sub_security` · `sub_direction` (역할 `SubSelf` · `SubPerf` · `SubSecurity` · `SubDirection` = 각 검토형 서브에이전트 정의 본문, 읽는 것만 작업 폴더 · Read 기준 / 에이전트 `claude_cli_opus` 공용 = claude-opus-5-5 · effort high · MaxTurns 40 · MaxBudgetUsd 2 · 재시도 없음, 제공사도 MaxAttempts 1). **검토형 리뷰의 기본**이다 — `/self-review` · `/cross-review` 2단계 · `perf_*` ownReview (서브에이전트는 `/self-review sub …` 또는 크레딧 소진 때).
- **크레딧 소진 대체**: claude-cli 단계는 그 제공사의 최근 판정 가능한 기록(성공 또는 Quota)이 1시간 안의 Quota면 호출 없이 `[실패] Quota (최근) …`를 돌려준다 (지휘자는 서브에이전트로 대신한다). 1시간 뒤에는 한 번 시도한다 — 계속 막으면 성공 기록이 생기지 않아 크레딧을 채워도 풀리지 않는다 (크레딧이 없을 때 실패 호출은 비용 0, 충전 뒤 최대 1시간이면 풀린다). 대시보드 첫 화면에 `API 리뷰(sub_*) 이번 달 N회 · $X`(claude-cli 제공사 기준, 단가 없는 기록은 건수로)와, 마지막 판정 가능한 기록이 Quota면 `크레딧 소진 — 서브에이전트로 대체 중`(1시간이 지났으면 `다음 호출에서 다시 시도`). 시범 1호(2026-10-27, 이 기능 자체 리뷰): 17턴 · 274초 · $1.26, self-reviewer 서브에이전트와 지적 9건 겹침. 절차는 `/self-review` 명령 파일 "API 리뷰".
- **ownReview로 쓸 때** (`/cross-review` 2단계 · `perf_*`): 응답 본문(`## 지적`부터)만 넣고 reason 태그는 `[ownReview:sub_self]` · `[ownReview:sub_perf]` · `[ownReview:sub_self+sub_security]`. H4 훅이 읽은 파일을 작업 폴더 기준 상대 경로로 검사한다.
- **정의 색인** (계획서 `docs/plans/2026-10-27_작업폴더-정의-색인.md`): 이 PC의 claude -p는 Read만 주므로, 작업 폴더 절의 파일 목록에 줄 수와 `정의: class X 12 · Run() 40 · …`(C# · Python · Markdown · JSON 얕은 키 · PowerShell, 줄 단위 정규식 — 대략의 위치)를 붙인다. 파일당 80개 · 전체 2만 자 · 1MB 이하 UTF-8 파일만 (`DefinitionIndex`).

### 재시도

SDK 자동 재시도(기본 3회, 기록에 안 남음)는 끄고 서버가 직접 재시도한다.

| 오류 | 재시도 | 기록의 `ErrorKind` |
|---|---|---|
| 네트워크 타임아웃, 408 | O | `Timeout` |
| 429 요청 과다 | O (`Retry-After`·`retry-after-ms`를 따름, **30초를 넘으면 기다리지 않고 실패**) | `RateLimit` |
| 5xx | O | `Server` |
| 연결 실패 (응답 없음) | O | `Network` |
| 그 외 4xx (400 · 401 · 402 크레딧 부족 · 404 …) | X — 다시 보내도 같다 | `Client` |
| 사용자 취소 | X — 시도 기록을 남기고 중단 | `Canceled` |
| 프로세스 강제 종료 (작업 중지 · 세션 종료) | X | `Lost` (다음 정리 때, 아래 "끊긴 호출 비용 추정") |
| 빈 응답 | X — 원인이 출력 상한이라 같다 | — |

- 서버가 대기 시간을 알려주지 않으면 2초 → 5초를 기다린 뒤 다시 보낸다.
- **시도마다 calls.jsonl에 한 줄**씩 남는다 (`Attempt` 1부터, 같은 `RunId`·`Step`). 대시보드 목록에 `시도 n/N` 배지가 붙는다.
- `run_mode` 결과에는 `(재시도 1회 후 성공 · 1회차 Timeout)`처럼 한 줄로 알린다.
- **한 단계의 최대 대기 ≈ 타임아웃 × 시도 횟수** (+ 시도 사이 대기). 예: 타임아웃 300초, MaxAttempts 2 → 최대 약 10분.

### 끊긴 호출 비용 추정

요청이 나간 뒤 끊긴 호출(`Timeout` · `Network` · `Canceled` · `Lost`)은 청구되지만 usage를 받지 못해 토큰이 비어 있다. 대시보드가 기록을 읽을 때 추정 사용량을 채운다 (`UsageEstimator`, 기록 파일은 그대로). 계획서 `docs/plans/2026-10-24_끊긴-호출-비용-추정.md`.

- 같은 `RunId` · `Step` · 에이전트에 성공한 시도가 있으면 그 입력을 쓴다 (캐시 적중 0). `Timeout` · `Network`은 서버가 끝까지 생성해 청구했다고 보고 출력도 쓴다 (2026-10-20 GLM). `Canceled` · `Lost`는 연결을 닫아 생성이 멈췄다고 보고 입력만 (2026-10-21 Gemini).
- 성공한 시도가 없으면 입력만 어림한다: (프롬프트 + 지침 UTF-8 바이트) × 0.3 (2026-10-24 실측 중앙값 0.27~0.31). `Timeout` · `Network`은 "출력 미상"으로 표시한다 (실제 청구는 더 클 수 있음). 웹검색 호출은 검색 결과가 입력에 붙어 어림이 하한이 된다.
- HTTP 상태로 거절된 실패(4xx · 5xx · `Quota` · `RateLimit`, 그리고 `Timeout`으로 분류되는 HTTP 408)는 청구되지 않는 것으로 보고 넣지 않는다. `Timeout` · `Network`에는 요청을 보내기 전 실패(연결 거부 · DNS)도 섞이는데 기록으로는 구분할 수 없어 툴팁에 적는다. followUp · 이미지 호출의 바이트 어림은 "하한"으로 표시한다.
- 대시보드: 추정 기록의 토큰 · 비용 앞에 `≈`, 합계 옆에 "추정 $x 포함 (N건, 출력 미상 M건)", 청구 대조의 계산값 아래에 추정분, 호출 상세에 근거.
- **호출 중 표시**: 요청을 보내기 직전에 `calls.jsonl` 옆 `inflight/<기록 Id>.json`(PID · 프로세스 시작 시각 · 기본 기록)을 쓰고, 시도 기록을 남긴 뒤 지운다. 서버 · 골든의 매 단계 시작과 대시보드가 기록을 읽을 때, 남긴 프로세스가 끝난 표시를 `Lost` 기록(`[끊김] 프로세스가 응답 전에 종료됨 …`)으로 옮긴다. 여러 프로세스가 동시에 정리해도 표시 파일 이름을 먼저 바꾼 쪽만 옮긴다 (`.claimed-<PID>-<시작 Ticks>-…`). 차지한 채 죽은 파일은 되돌려 다시 정리하고, 주인이 죽은 쓰다 만 임시 파일(`.tmp-<PID>-<시작 Ticks>`)은 지운다. 기록 저장에 실패하면 표시를 남겨 둔다. 기록 직후 죽어 같은 Id가 둘이 되면 읽을 때 하나만 둔다. 대시보드는 5초에 한 번 정리하고, 목록은 시각순으로 정렬한다 (늦게 붙은 Lost 때문). 서버의 `ReadSince`는 Lost 줄을 멈출 기준으로 쓰지 않는다.
- 스트리밍(끊긴 시점까지 받은 출력을 세고, 끊으면 생성도 멈춤)은 보류 — Timeout이 다시 생기면 검토 (2026-10-20 · 2026-10-24 결정).

### 응답 검사기

| 검사 | 기준 | 처리 |
|---|---|---|
| 빈 응답 | 내용 없음 | **실패** `[빈 응답]` (종료 이유가 길이 제한이면 MaxOutputTokens 안내) |
| 출력 상한에서 잘림 | 응답의 종료 이유가 길이 제한(`length`) | 경고 |
| 언어 이탈 | 코드 블록(```` ``` ```` · `~~~`)과 인라인 코드를 뺀 본문에서 가나 비율 ≥ `ForeignScriptRatio`, 가나 5자 이상. 가나가 기준 밑이면 한자(CJK 통합 · 확장 A · 호환)도 같은 기준으로 본다 (중국어) | 경고 (한 줄: 가나가 넘으면 가나, 아니면 한자) |
| 영어 문장 섞임 | 같은 본문에서 한글이 없고 영어 낱말 5개 이상인 줄이 2줄 이상. 낱말 = 첫 글자만 대문자일 수 있는 소문자 영어 낱말 — CamelCase · 대문자 약어 · 경로 · 밑줄 · 숫자가 든 토큰은 식별자로 보고 세지 않는다. `>` 인용 줄 · `|` 표 줄, 해석에 실패한 JSON 원문은 보지 않는다. 기준은 지난 응답 755건(크래시 역할 61건 포함)으로 정함 — 걸린 것은 sol의 작업 메모(5e81a2ed47) 한 건뿐 (2026-10-27) | 경고 `[경고] 영어 문장 섞임 (n줄, 예: "…")` |
| 출력 형식 절 빠짐 | 역할에 `OutputSections`(N)가 있고 종료 이유가 길이 제한이 아닐 때, 코드 밖에서 1..N 절 제목이 모두 있는지. 제목 꼴(`#` · `**` · `__` 뒤 `N.` · `N)`, 함께 와도 됨)이 한 줄이라도 있으면 그 꼴만 보고 1절과 같은 단계만 센다. 없으면 맨 앞부터 쓴 `N. 제목`(첫 `—` · `:` 앞 제목 부분 40자 이하)을 본다. 상한 전에 모델이 스스로 멈춘 응답(종료 이유 `stop`)을 잡는다. 지난 preflight 응답 36건에서 걸린 것은 5e81a2ed47 한 건뿐 | 경고 `[경고] 출력 형식 절 빠짐 (3., 4. / 전체 7절)` |

- 경고는 실패가 아니다. 응답은 그대로 전달하고, `run_mode` 결과의 해당 단계 아래에 `[경고] 응답 언어 이탈 감지 (가나 비율 10.7%, 308자)` · `[경고] 응답 언어 이탈 감지 (한자 비율 65.5%, 610자)`처럼 한 줄씩 붙인다.
- 경고는 기록(`Warnings`)에도 남고, 대시보드 목록과 상세 화면에 `경고 n` 배지로 보인다.
- 검사기 자체가 오류를 내면 경고 없이 넘어가고 서버 로그만 남긴다.
- `ExpectedLanguage`는 "비우면 기존 동작" 원칙의 **예외**로, 키가 없어도 검사가 켜진다. 경고는 결과를 바꾸지 않고, 사람이 대시보드를 보기 전에는 모르는 이탈(3장)을 기본으로 잡기 위해서다. 끄려면 `""`.

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
- 표 밖의 필드: `append`(모델별 덧붙임 파일 경로 · 해시, 아래 3절) · `blobFolder`(원문을 저장한 폴더).

### 데이터 폴더 (`%LOCALAPPDATA%\HB_Orchestration\`)

서버 · 골든 · 대시보드가 같은 폴더를 쓴다. `HB_ORCH_LOG_PATH`(calls.jsonl 경로)를 주면 그 파일의 폴더가 기준이 되고, 아래 하위 폴더도 함께 옮겨진다 — 단 `config-backup/`은 배치가 `%LOCALAPPDATA%\HB_Orchestration\`에 고정해 둔다.

| 경로 | 내용 |
|---|---|
| `calls.jsonl` | 호출 기록. 한 줄 = 시도 1회 (UTF-8, camelCase). 여러 서버 프로세스가 같이 덧붙인다 |
| `blobs/<sha256>.txt` | 시스템 프롬프트 · 지침 · modes.json · (선택) 설정 원문, 그리고 **4,096바이트 이상인 프롬프트**. 큰 프롬프트는 기록에 앞 1,000자 + `promptSha256` · `promptBytes`만 남는다 — 원문은 대시보드 호출 상세가 blob에서 읽는다. `Records.BlobFolder`로 위치를 바꿀 수 있다 |
| `inflight/<기록 Id>.json` | 호출 중 표시 (위 "끊긴 호출 비용 추정") |
| `review_in/<임의 이름>/` | `claude-cli` 단계의 작업 폴더 (context 파일 전체 복사). 실행이 끝나면 지우고, 남은 것은 하루 뒤 다음 실행이 지운다 |
| `verdicts.jsonl` | `record_verdicts` 판정. 같은 키는 마지막 줄이 이긴다. `HB_ORCH_VERDICTS_PATH`로 따로 둘 수 있다 |
| `sessions/<pid>.json` | 서버 프로세스별 모드 실행 횟수 |
| `subagents/<agentId>_<호출 id>.json` | 검토형 서브에이전트(self · perf · security · direction-reviewer)가 지휘자에게 넘긴 결과 원문의 보관본 (7절 "서브에이전트 결과"). 한 번 쓰면 바꾸지 않는다. `_state/scanned.json` = 보관을 마친 검토형 기록 (경로 → 크기:수정 시각, 지워진 기록은 빠진다) |
| `dumps/` | `Debug.DumpToolResults`가 켜졌을 때만 |
| `config-backup/<시각>/` | 배치 publish 전 설정 백업 (최근 20개, 8절) |

예전 기록의 큰 프롬프트를 blobs로 옮기려면 서버 없이 한 번 돌린다 (원래 파일은 `calls.jsonl.bak-<시각>-<PID>`로 남김. 종료 코드 0 = 성공 / 1 = blob 저장 실패가 있었거나, 압축 중 파일 앞부분이 바뀌어 아무것도 바꾸지 않음 — 이때도 백업은 남는다. 파일이 없으면 예외로 끝난다):

```powershell
dotnet <publish 폴더>\HB.Orchestration.Mcp.dll --compact-log [calls.jsonl 경로]
```

### 재시작이 필요한 경우
| 바꾼 것 | 필요한 동작 |
|---|---|
| modes.json (모드·역할·에이전트 배치) | 없음 — 다음 호출부터 반영 |
| appsettings.json (에이전트·모델·제공사·단가) | 없음 — 다음 호출부터 반영 |
| 서버 코드 (publish) | 배치가 MCP 서버만 종료 → Claude Code에서 `/mcp`로 재연결 (8절). Claude Code·대화는 그대로 |
| **API 키 환경 변수** | **PC 재시작** (직접). 환경 변수는 프로그램이 시작될 때 물려받는 값이라, Unity Hub·Toolbox 같은 트레이 프로그램까지 새로 떠야 확실하다 |

## 3. 모드 — `modes.json` ("무엇을 어떤 순서로")

**실행할 때마다 다시 읽으므로 저장만 하면 바로 반영**된다. 대시보드의 **모드** 화면에서 흐름을 보고 에이전트를 바꿔 저장할 수도 있다 (7절).

찾는 순서: `ModesPath`(appsettings) → `HB_MODES_PATH` 환경 변수 → **Unity 프로젝트 루트의 `hb-modes.json`** (프로젝트별 모드) → 서버 폴더의 `modes.json`

`ModesPath`나 `HB_MODES_PATH`로 **지정했는데 그 파일이 없으면 오류**다 (run_mode 실패, server_info에 이유). 오타가 있을 때 다른 modes.json으로 조용히 넘어가지 않게 하기 위함이다. 다음 후보로 넘어가는 건 지정이 없을 때만이다.

상대 경로의 기준: appsettings `ModesPath`는 **서버 폴더**(appsettings.json이 있는 곳) 기준이라 서버와 대시보드가 같은 파일을 가리킨다. `HB_MODES_PATH`는 프로세스 환경 값이라 **그 프로세스의 현재 폴더** 기준이다 — 넘길 때는 절대 경로를 쓴다. 호출 기록(provenance)의 modes 경로는 정규화된 절대 경로로 남는다 (2026-10-21 이전 기록은 받은 값 그대로라 상대 경로·`/` 표기가 섞여 있다).

> modes.json은 **주석 없는 순수 JSON**으로 둔다. 설명은 이 README에 있다. (서버는 주석이 있어도 읽지만, 대시보드에서 저장하면 주석이 사라진다.)

```json
{
  "Roles": {
    "Rebuttal": { "Prompt": "너는 반박자다 ...", "UseGuidelines": true }
  },
  "Modes": {
    "design_main": {
      "Description": "설계 판단 (중요한 것): 주장/설계에 반박 후 양쪽 논리 검증",
      "Flow": "Sequential",
      "Return": "All",
      "MaxInputChars": 100000,
      "Steps": [
        { "Role": "Rebuttal",   "Agent": "gpt_main_sol" },
        { "Role": "LogicCheck", "Agent": "gemini_main_flash", "Sees": "InputAndPrevious" }
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
| `Output` | `findings` · `perf_findings` (판을 고르려면 `findings@1` · `perf_findings@1`) / 생략 | 구조화 출력 형식. 지정하면 서버가 JSON 형식 설명을 시스템 프롬프트에 붙이고 응답을 해석한다. 역할 프롬프트에는 출력 형식을 따로 적지 않는다. 모르는 이름이면 `[모드 설정 오류]`. `WebSearch` 단계와 함께 쓸 수 없다 |
| `OutputSections` | 정수 / 생략 | 자유 텍스트 출력 형식의 절 개수. 있으면 응답 검사기가 1..N 절 제목이 모두 있는지 보고 빠지면 경고 ("응답 검사기"). preflight 역할에 둔다 (P1 4 · P1_Perf 3 · P2 5 · P3 7 · Check 4 · PerfCheck 4) |

내장 역할 `Reviewer`, `Critic`은 코드에 들어 있고, 같은 이름으로 정의하면 덮어쓴다. `Prompt` · `PromptFile` 없이 내장 역할 이름에 `Output` · `UseGuidelines`만 적으면 내장 프롬프트는 두고 그 두 값만 바뀐다. 내장 `Reviewer`는 `Output: findings`다 (modes.json에서 덮어쓰면 그쪽 `Output`을 따른다). `PerfReviewer`는 modes.json에 `Output: perf_findings`로 둔다.

### 모델별 덧붙임 (`prompts/<역할>/<에이전트>.md`)
역할 프롬프트를 모델마다 복사하지 않고, `prompts/<역할>/<에이전트 이름>.md`를 시스템 프롬프트 **맨 끝**(지침 뒤)에 `## 모델별 덧붙임`으로 붙인다 (계획서 `docs/plans/2026-10-23_모델별-프롬프트-덧붙임.md`). 그 (역할, 에이전트)가 자주 하는 실수 · 모델 버릇을 적는 자리다 (계획서 `docs/plans/2026-10-27_실수목록-분리.md`).
* 원본은 저장소 `project-files/prompts/`이고, 서버는 appsettings `Orchestration:PromptsPath`로 읽는다 (상대 경로는 서버 폴더 기준, 예: `../HB_Orchestration/project-files/prompts`). 지정했는데 폴더가 없으면 `[모델별 덧붙임 폴더 오류]`으로 실행하지 않는다. 비우면 **실제로 쓰는** modes.json 옆 `prompts/` (세션 폴더의 `hb-modes.json`을 쓰면 그 프로젝트 폴더).
* 에이전트는 `agents` 교체 **뒤** 실제 이름으로 찾는다 — 전용 모드가 필요 없다. 실행마다 다시 읽고, 파일이 없거나 HTML 주석(`<!-- -->`)을 뺀 본문이 공백뿐이면 붙이지 않는다 (시스템 프롬프트는 바이트 그대로). 기본 파일은 서식 안내 주석뿐이다. 주석 짝이 맞지 않으면(중첩 · 닫지 않음) 안내문이 새지 않게 `[모델별 덧붙임 읽기 실패]`로 실행하지 않는다.
* Debate 토론자도 토론 규칙 **뒤** 맨 끝에 붙는다. 역할·에이전트 이름이 영문·숫자·`_`·`-` 밖의 글자를 가지면 덧붙임을 둘 수 없다(실행은 됨). 파일이 있는데 못 읽으면 `[모델별 덧붙임 읽기 실패]`로 실행하지 않는다. 골든 `--no-guidelines`여도 붙인다 (모델 버릇이지 프로젝트 지침이 아님).
* 보이는 곳: `list_modes`의 `모델별 덧붙임(읽는 곳):` 줄(본문 있는 것만. 설정 폴더 오류면 그 이유, 설정이 비었는데 modes.json 옆 폴더도 없으면 `없음 —` 안내), 대시보드 호출 상세(`Provenance.Append` 경로·해시·원문 — 주석 포함 파일 그대로), 골든 실행 계획 줄. 골든 실행 전 확인 뒤 덧붙임이 바뀌면 호출하지 않는다.
* 규칙: 만드는 쪽은 지휘자뿐 — `save_result`는 `**/prompts/**`에 쓰지 못한다. 골든 기준점은 (역할, 에이전트) 단위다. 골든 사례가 없는 역할에도 실수를 적을 수 있고, 골든 사례가 있는 역할에 적었으면 골든 1건으로 확인한다 (2026-10-27 결정 2 — 예전 "골든 사례가 있는 역할에만" 규칙을 풀었다).
* 기본 파일은 modes.json이 쓰는 (역할, 에이전트) 쌍마다 하나씩 있다. 새 쌍을 넣으면 같은 서식으로 만든다 (`project-files/prompts/README.md`).

### 기본 모드와 이름 규칙

이름은 고를 수 있는 모드면 `{종류}_{등급}`(lite · main · high — high는 비싸거나 두 회사 동시), 단계 모드면 `{종류}_{단계}`다 (계획서 `docs/plans/2026-10-20_모드이름-통일-세션한도-제거.md`).
예전 이름(review · panel · perf_sol · perf_argon · critique · debate · deliberate · research · parallel · work_order · crash · crash_review)으로 부르면 실행하지 않고 새 이름을 알려 준다.
호출 기록·골든 사례·세션 기록에 남은 예전 이름은 **읽을 때만** 새 이름으로 바꿔 읽는다 (`ModeNames`, 파일은 그대로). 없앤 `perf_astra`는 기록에 그대로 보인다.
구성이 바뀐 모드(`perf_argon` argon 단독 → `perf_high` sol | argon, 2026-10-22부터 `review_high` · `perf_high` sol | argon → sol | DeepSeek)는 예전 실행과 **한 모드로 합쳐 집계**된다. 구성별로 보려면 판정 화면의 에이전트 축을 본다.
`review_high` · `perf_high`의 두 번째 리뷰어(DeepSeek, `deepseek_main_flash`)는 Vision이 없어 `images`를 붙이면 실행 전에 거절된다. 이미지가 필요하면 `run_mode`의 `agents`로 argon을 지정해 실행한다 (`"deepseek_main_flash=gemini_high_argon"`).
modes.json에 예전 이름 키가 남아 있으면 `list_modes`가 `[주의]`로 알린다 (그 키는 예전 이름 그대로 실행되지만, 기록은 새 이름으로 읽혀 followUp이 어긋난다 — 새 이름으로 바꾼다).

| 종류 | 모드 | 구성 | 부르는 곳 |
|---|---|---|---|
| 리뷰 | `review_main` · `review_high` | sol · (sol \| DeepSeek) | `/cross-review [main\|high]` |
| 성능 | `perf_main` · `perf_high` | sol · (sol \| DeepSeek) | `/mode`, ownReview는 perf-reviewer |
| 설계 판단 | `design_lite` · `design_main` · `design_high` | 비평(sol) · 반박(sol) → 논리 검증(flash) · 반박 ⇄ 옹호 → 판정 | `/mode` |
| 조사 | `research_main` | luna(웹) → sol | `/mode` |
| 크래시 | `crash_main` → `crash_check` | argon → luna(웹) · sol | `/mode` |
| 구현 | `parallel_main` | sol \| flash | `/mode` (요청할 때만) |
| 지시서 | `work_order_main` | luna \| flash | `/work-order` |
| 실행 전 점검 | `preflight_p1` · `p2` · `p3` · `preflight_check` · `preflight_perf` | 단계 모드 | `/preflight` |

### 모드 (`Modes.<이름>`)
| 키 | 값 | 의미 |
|---|---|---|
| `Description` | 문자열 | `list_modes`에서 Claude에게 보이는 한 줄 설명. 짧을수록 토큰 절약 |
| `Flow` | `Sequential` / `Concurrent` / `Debate` | 순서대로 넘기기 / 동시에 독립 실행 / 다회차 토론 |
| `Steps` | 배열 | 단계 목록 (아래 표) |
| `Return` | `Last`(기본) / `All` | Claude에게 돌려줄 범위. Concurrent는 항상 All |
| `MaxInputChars` | 숫자 | 이 모드의 입력 길이 상한. 없으면 `Budget.MaxInputChars` |
| `PromptCache` | `Off` / `Context` / 없음 | 이 모드의 프롬프트 캐시 방식. 없으면 제공사 설정(`Providers.<이름>.PromptCache`). 제공사가 정책 없음(`ProviderDefault`)이면 무시. 같은 첨부를 30분 안에 다시 보내지 않는 모드는 `Off`(쓰기 1.25배만 내지 않게). 기본 modes.json은 preflight 단계 5개 · `parallel_main` · `work_order_main`이 `Off` |
| `RequireOwnReview` | `true` / 없음 | `true`면 `run_mode`의 `ownReview`가 필수 (review 계열). 없으면 필수 아님. `list_modes`에 `[ownReview 필수]` 표시. 기본 modes.json은 `review_main` · `review_high` · `perf_main` · `perf_high` · `crash_main` · `crash_check` · `work_order_main` · `preflight_p1` · `preflight_check` · `preflight_perf` |
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

### 실행 횟수
세션당 실행 한도는 없다 (2026-10-20 제거 — 예전 `MaxRunsPerSession` · `Budget.DefaultMaxRunsPerSession` 키는 남아 있어도 무시된다). 반복 호출은 지휘자가 "이번 호출이 결론을 바꿀 수 있는가"로 판단한다 (CLAUDE.md).
- 서버는 세션별 실행 횟수만 `%LOCALAPPDATA%/HB_Orchestration/sessions/{PID}.json`에 남기고, 대시보드 **세션** 화면이 보여 준다.

### run_mode 인자

| 인자 | 필수 | 내용 |
|---|---|---|
| `mode` | O | 모드 이름 (`list_modes`) |
| `input` | O | 목적 · 의도 · 제약 + 디스크에 없는 자료. 코드는 `context`로 |
| `context` | | `{path, startLine, endLine}` 목록. 서버가 읽어 줄 번호와 함께 붙인다 (`@이름/…` = 추가 루트) |
| `files` | | 관련 파일 (`path:line`). 기록 · 대시보드 링크용, 에이전트에게 안 감 |
| `images` | | png · jpeg 경로 (`/image-prep`로 줄인 것) |
| `reason` | 모든 모드 | 부른 이유 한 줄 (아래) |
| `ownReview` | `[ownReview 필수]` 모드 | 결과를 보기 전에 완성한 자체 검토 원문 (아래) |
| `agents` | | 이번 실행만 에이전트 교체 `from=to` · `*=to` (아래) |
| `reasoning` | | 이번 실행만 추론 강도 `에이전트=강도` · `*=강도` (2절 `ReasoningEffort`) |
| `followUp` | | `{runId, step, requests}` 자료 보충 재질문 (아래 "자료 보충 재질문") |

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
`run_mode`의 `agents` 인자로 modes.json을 고치지 않고 이번 실행만 바꿀 수 있다. Claude Code에서는 "review_main을 astra로 돌려줘"처럼 말하면 된다 (지휘자는 사용자가 요청했을 때만 쓴다, 대상 에이전트가 켜져 있어야 함).
- `"gpt_main_sol=gpt_high_astra"` — 그 에이전트만 교체
- `"*=gpt_high_astra"` — 모든 단계를 교체 (`review_high`처럼 두 회사 비교가 목적인 모드에는 쓰지 말 것)
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
첨부 42,310자 + input 2,180자 = 44,490자 / 상한 350,000자 (13%)   ← claude-cli(sub_*)가 아닐 때
### 1. Cross_P3_A · gpt_main_sol (입력 15,551 / 출력 7,502, 144.0초)
(재시도 1회 후 성공 · 1회차 Timeout)          ← 재시도가 있었을 때만
[경고] 응답 언어 이탈 감지 (가나 비율 10.7%, 308자)  ← 검사기 경고가 있을 때만
<에이전트 응답>
```
- `runId`는 `save_result`에 넘기는 값이다. 대시보드와 calls.jsonl에서도 같은 값으로 찾을 수 있다. 첫 줄은 늘 runId만이다 (세션 기록이 첫 줄로 실행을 잇는다).
- 둘째 줄 첨부 여유: 그 모드의 입력 상한(`MaxInputChars`) 대비 이번 입력 (퍼센트는 내림). 절반을 넘으면 `· followUp 여유 적음 …`이 붙는다 — followUp은 원래 프롬프트 + 응답 + 보충이 같은 상한 안에 들어가야 한다. 지휘자가 다음 호출에서 정의 · 호출부 · 설정 · 테스트를 더 붙여도 되는지 본다 (계획서 `docs/plans/2026-10-27_외부-첨부-범위-규칙.md`). followUp 결과에는 없다. 판정 화면 "자료 요청 처리"의 주별 표로 규칙 전후의 실행당 요청을 비교한다.
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

- **보정**: 여는 `{`가 빠지고 `"findings"`부터 시작하는 응답은 `{`를 붙여 읽고, 결과에 `[경고] 구조화 출력 보정: 여는 { 누락`을 붙인다 (조용히 고치지 않음, 2026-10-20 258건 중 1건).

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
- 실행마다 **성공한 보충 1회** (실패·취소된 시도는 다시 할 수 있다, 같은 실행의 동시 호출은 서버 안 예약으로 하나만). 입력 상한(원래 프롬프트 + 응답 + 보충)은 본다. `reason` 필수. `agents`·`reasoning`·`images`·`ownReview`와 함께 쓰면 거절한다 (원래 단계의 에이전트 · 추론 강도로 보낸다).
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
  - 모델별 프롬프트 덧붙임: `**/prompts/**`
  - 저장소·CI·git 훅: `**/.github/**`, `.gitattributes`, `.gitmodules`, `**/.husky/**`, `**/.githooks/**`, `.pre-commit-config.yaml`
  - IDE: `**/.vscode/**`, `**/.idea/**`
  - 빌드·패키지: `*.csproj`, `*.sln`, `*.slnx`, `*.props`, `*.targets`, `global.json`, `nuget.config`, `package.json`, `**/Packages/manifest.json`, `**/Packages/packages-lock.json`, `**/ProjectSettings/**`
  - 실행 파일·스크립트(어디서든, `docs/`·`review_tmp/` 포함): `*.ps1` `*.psm1` `*.psd1` `*.bat` `*.cmd` `*.sh` `*.py` `*.exe` `*.dll` `*.com` `*.scr` `*.msi` `*.vbs` `*.vbe` `*.js` `*.jse` `*.wsf` `*.wsh` `*.hta` `*.reg` `*.lnk`
  - Unity 컴파일 대상: `**/Assets/**/*.cs`, `**/Packages/**/*.cs`, `*.asmdef`·`*.asmref`(Assets·Packages 아래), `*.rsp`(csc.rsp 컴파일러 인수)
  - 거절 문구 `쓰기 금지 파일입니다 (WriteDenyPatterns '…')`. 응답 코드는 `docs/`·`review_tmp/` 등에 `.md`·`.cs`·`.txt`로 저장한 뒤 합친다. 추가분은 `Context.WriteDenyPatterns`. 호출부가 목록을 넘기지 않으면 내장 목록이 적용된다 (열리는 쪽이 아니라 막는 쪽).
- 쓰기는 같은 폴더의 짧은 임시 파일(`.<guid>.tmp`)에 다 쓴 뒤 이동으로 교체한다. 실패해도 잘린 파일이 남지 않고 `overwrite: true`의 원래 내용은 그대로다. `overwrite: false`이고 대상이 있으면 쓰기 전에 "이미 있습니다"로 거절하고, 확인과 이동 사이에 생긴 경우도 이동 단계에서 같은 문구로 막는다. 쓰기 단계의 실패는 "쓰기 실패".
  - 이동 방식이라 원래 파일의 속성·생성 시각·대체 데이터 스트림은 이어지지 않고, 다른 프로그램이 삭제 공유 없이 열고 있으면 덮어쓰기가 "쓰기 실패"가 된다 (의도된 동작).
- UTF-8(BOM 없음)으로 저장하고 `저장: <경로> (n바이트, SHA-256 …)`을 돌려준다. 실패는 `[저장 불가] …`.
- 도구 설명이 컨텍스트에 늘 올라가므로 설명은 한 문장으로 짧게 두었다.

### record_verdicts — 지적마다 판정 기록
`record_verdicts(runId?, items[], selfReview?)` — item: `findingKey`, `summary?`, `verdict`, `steps?`, `reason?`, `severity?`, `claudeAlsoFound?`, `file?`, `findingIds?`

결과를 종합한 뒤 지휘자가 지적마다 판정을 남긴다. 대시보드 **판정** 화면의 원본이다 (규칙은 CLAUDE.md "결과 종합" 5번).
- `verdict`: `adopted`(채택) · `partial`(부분 채택) · `rejected`(기각) · `deferred`(보류) · `duplicate`(중복). 채택이 아니면 `reason` 필수 (`Records.MaxReasonChars` 이내).
- `steps`: 그 지적을 낸 단계 번호. 그 실행에 없는 번호는 거절. 비우면 **Claude만 지적**(에이전트가 놓친 것)으로 기록하고 `claudeAlsoFound`는 true가 된다.
- `findingIds`(구조화 출력의 지적 ID, 예: `["1-F2","2-F1"]`): 주면 `steps`를 ID에서 채우고(같이 주면 일치해야 함), 비어 있는 `summary`·`file`·`severity`를 **첫 ID**의 지적에서 채운다. severity는 여러 ID 중 가장 높은 것을 high·medium·low로 바꿔 쓴다. 이 실행에 없는 ID는 거절. 판정 줄에 `FindingIds`로 남는다.
- **자료 요청 ID**(`1-N1`)를 `findingIds`로 주면 요청 처리 기록이 된다 (`Kind: request`): `supplied`(보충) · `declined`(보내지 않음, `reason` 필수) · `deferred`. 지적 ID와 한 판정에 섞을 수 없다. 채택률·놓침에는 들어가지 않고 판정 화면 "자료 요청 처리"에 따로 모인다 (처리 안 한 요청 목록 포함).
- 하나라도 틀리면 아무것도 쓰지 않는다 (`[판정 기록 불가] …`에 틀린 항목을 모두 적음). 한 번에 50건까지.
- 저장: `calls.jsonl`과 같은 폴더의 `verdicts.jsonl` (환경 변수 `HB_ORCH_VERDICTS_PATH`로 바꿀 수 있음). 한 줄 = 판정 1회, 한 호출의 줄은 한 번에 쓴다.
- 같은 `runId`·`findingKey`로 다시 쓰면 마지막 판정이 이긴다 (보류 → 결론, 사용자가 뒤집은 판정). 이전 줄은 지우지 않는다.
- 에이전트·역할은 저장하지 않고 `runId` + 단계로 calls.jsonl에서 찾는다. 단계의 대표 호출은 성공한 마지막 시도다.
- **`selfReview`** (`/self-review`, runId 없이): `{ review(서브에이전트 결과 원문), intent, agent?, files?, model?, reason?, subagentTokens?, toolUses?, durationMs? }`. 서버가 실행 1건(Tool·Mode `self_review`, 제공사 `claude-subagent`, 토큰은 `UsageRaw`에만)을 만들고 결과 첫 줄에 runId를 준다. 서브에이전트 지적은 `steps: [1]`.
  - `agent`: `self-reviewer`(기본) · `perf-reviewer` · `security-reviewer` · `direction-reviewer` — 판정 화면에 서브에이전트별 채택률 · 놓침이 나온다.
  - 같은 원문이 최근 7일 기록에 있으면 그 runId를 알려 주며 거절한다 (응답을 못 받아 다시 불러도 두 번 세지지 않음). `runId`와 같이 쓰면 거절.

#### 판정 화면 (`/Verdicts`)
| 칸 | 뜻 |
|---|---|
| 채택률 | (채택 + 부분 채택 × 0.5) / (채택 + 부분 채택 + 기각). 보류·중복은 뺀다. **지휘자가 받아들인 비율**이지 정확도가 아니다 |
| 에이전트만 | 채택·부분 채택 중 ownReview에 없던 것 |
| 놓침 | Claude만 지적해 채택된 것이 있던 실행에서 그 에이전트가 **지적을 내는 단계**였던 수. 지적을 내는 단계 = 구조화 출력을 냈거나 그 실행에서 지적을 하나라도 낸 단계 (판정자·검증 역할 제외). 자유 텍스트로 지적 0건을 낸 리뷰어는 빠진다 |
| 비용 · 채택 1건당 | 판정이 있는 실행의 호출(재시도 포함) 비용 합계와 그것을 (채택 + 부분 채택 × 0.5)로 나눈 값. 기록에서 계산만 한다 |

- 절: 에이전트별 · 모드 · 역할별 · Claude만 지적(에이전트가 놓친 것) · 최근 판정 · 자료 요청 처리 · 판정 없는 실행.
- 기간은 판정 시각이 아니라 실행 시작 시각 기준 (소급 판정도 그 실행의 기간에 들어간다). 기본 30일.
- 호출 상세에는 그 실행의 판정, 호출 목록에는 `판정 n` 배지.

## 4. 토큰 절약 장치

| 장치 | 어디서 | 효과 |
|---|---|---|
| 도구 5개(`list_modes`, `run_mode`, `save_result`, `record_verdicts`, 확인용 `server_info`)만 노출 | MCP | 모드가 늘어도 Claude 컨텍스트의 도구 설명이 그대로 |
| `save_result` | MCP | 응답을 파일로 남길 때 Claude가 다시 출력하지 않음 (출력 토큰 0) |
| 실행 전 검증 | 서버 | 모드·에이전트·역할 오류, 입력 초과, 첨부 · 이미지 오류, `reason` · `ownReview` 누락이면 **토큰 0으로** 즉시 반환 |
| 단계별 `Sees` | modes.json | 다음 단계에 필요한 것만 전달 (예: research 검증자는 후보 목록만) |
| `Return: Last` | modes.json | 최종 판정만 필요하면 Claude가 읽는 양 감소 |
| 앞 단계 실패 시 중단 | 서버 | 의미 없는 뒤 단계 호출 안 함 |
| 공통 출력 규칙 | 서버 | 서론·반복·원문 재인용 금지 → 출력 토큰 감소 |
| 고정 시스템 프롬프트 앞부분 | 서버 | 역할+규칙+지침이 매번 같아 제공사 프롬프트 캐시 적중 |
| `UseGuidelines: false` | modes.json | 코드와 무관한 역할에 지침을 안 붙임 |
| `MaxOutputTokens` | appsettings | 응답 폭주 방지 |
| `MaxRounds` · `HistoryWindow` · `[합의]` 조기 종료 | modes.json | 다회차 토론의 누적 비용 제한 |
| `files`는 에이전트에 안 보냄 | 서버 | 경로 목록은 기록·링크 전용 |
| `context`로 코드 첨부 | 서버 | Claude가 이미 읽은 코드를 input에 다시 쓰지 않음 → **Claude 출력 토큰 절약**. 서버가 `{path, startLine, endLine}`을 디스크에서 읽어 줄 번호와 함께 붙임. 루트 밖·링크(심볼릭·junction)·전송 금지 파일·범위 오류·크기 초과는 호출 전에 거절. 첨부한 범위는 기록의 `Context`, 내용은 `Prompt`에 남음. 8.3 짧은 이름도 긴 이름으로 바꿔 금지 패턴을 검사. 한계: 검사와 읽기 사이에 링크로 바꿔치기하는 경쟁은 막지 않음(단일 사용자 PC 전제) |
| `images`로 이미지 첨부 | 서버 | `image_prep.py shrink`로 줄인 png·jpeg를 서버가 읽어 user 메시지에 `[첨부 자료] → [이미지 N] 이름표 + 이미지 → [요청]` 순서로 붙인다. 입력을 받는 단계에만 붙음. 경로 규칙은 `context`와 같고(`@이름/` 포함), 형식·장수·바이트·긴 변·중복은 호출 전에 거절. 기록에는 `Images`(경로·크기·sha256 앞 12자)만 남고 원문은 남기지 않음. 준비 순서는 CLAUDE.md "이미지 첨부" (collect → 사용자 정리 → shrink) |
| CLAUDE.md의 input 규칙 | Unity 쪽 | diff·최소 자료만, 기본은 호출하지 않기 |

## 5. Unity 프로젝트 쪽
> **`project-files/`가 원본이다.** Unity 프로젝트의 `CLAUDE.md`, `.claude/commands/*.md`, `.claude/scripts/*.py`, `.claude/agents/*.md`는 복사본으로 둔다 (`REVIEW_GUIDELINES.md`는 처음에만 복사하고 그 뒤로는 프로젝트 것). 규칙을 고칠 때는 원본을 고친 뒤 아래 스크립트로 복사한다 (Unity 쪽에서 직접 고치면 두 벌이 갈라진다). 줄바꿈은 LF (`.gitattributes`로 고정).

**처음 설치 (폴더째 붙여넣지 않는다)**
1. 스크립트 `-Apply`로 규칙서·명령을 복사한다 (아래).
2. `project-files/.mcp.example.json`을 Unity 프로젝트 루트에 **`.mcp.json`으로** 복사하고, `<publish 폴더>`를 실제 publish 폴더로 고친다.
3. `project-files/.claude/settings.example.json`을 `.claude/settings.json`으로 복사한다. 이미 있으면 `allow` 항목과 `hooks`를 합친다.
4. Unity 프로젝트 `.gitignore`에 `.mcp.json` · `.claude/push_guard.local.txt` · `.claude/share_guard.local.txt`(기기별 절대 경로 · 개인 값)와 `subagent_in/` · `review_tmp/` · `docs/image/`(서브에이전트 입력 사본 · 리뷰 임시 사본 · 사내 시안)를 넣는다.
5. `.claude/push_guard.local.txt`를 만들고 공개되면 안 되는 개인 값(이메일·로컬 사용자 경로 등)을 한 줄에 정규식 하나로 넣는다 (H2 훅, 없으면 내장 패턴만 검사).
6. `python -I .claude/scripts/check_agents.py`(훅 등록) · `python -I .claude/scripts/test_hooks.py`(훅 동작)가 통과하는지 본다. 훅은 새 세션부터 걸린다.

설정 템플릿은 실제 설정과 이름이 다르다(`*.example.json`). 폴더째 붙여넣어도 Unity 쪽 `.mcp.json` · `.claude/settings.json`이 덮이지 않게 하기 위해서다. 예전에 같은 이름이던 때 폴더째 붙여넣었다가 `.mcp.json`이 `<publish 폴더>` 자리표시로 덮여 MCP 연결이 끊긴 적이 있다.

```powershell
# 비교만 (기본). 종료 코드 0 = 모두 같음, 1 = 다른 것이 있음, 2 = 실행 오류
powershell -ExecutionPolicy Bypass -File tools\Sync-ProjectFiles.ps1 -UnityProject F:\MyGame
# 복사 + 해시 확인
powershell -ExecutionPolicy Bypass -File tools\Sync-ProjectFiles.ps1 -UnityProject F:\MyGame -Apply
# 모든 대상 비교만 (tools\sync-targets.local.txt — sync-targets.example.txt를 복사해 이 PC 경로로, git 제외). 종료 코드는 가장 나쁜 값
powershell -ExecutionPolicy Bypass -File tools\Sync-ProjectFiles.ps1 -All
# 스크립트 시험 (임시 폴더만)
powershell -ExecutionPolicy Bypass -File tools\Test-SyncProjectFiles.ps1
```

- 동기화 대상: `CLAUDE.md`, `.claude/commands/*.md`, `.claude/scripts/*.py`, `.claude/agents/*.md`. 바이트가 같아야 한다.
- 비교 전에 원본 `.claude/agents/*.md`를 `check_agents.py`로 검사한다 (tools 허용 목록 · bypass 계열 permissionMode 금지). 실패하면 아무것도 하지 않고 종료 코드 2.
- **없으면 만듦 (2026-10-22)**: `REVIEW_GUIDELINES.md`는 프로젝트마다 고쳐 쓰는 지침이다. 대상에 없을 때만 원본(기본 지침)을 복사하고, 있으면 달라도 `프로젝트 지침`으로 보여 주기만 한다 (멈추지 않고 `-Force`로도 덮지 않음). 단 사본이 원본 git 이력의 옛 판과 같으면(프로젝트에서 고치지 않음) `기본 지침 옛 판`으로 맞출 대상이 되고 `-Apply`가 갱신한다. `-All` 요약은 맞출 것과 별도로 참고 항목(settings.json 다름 · 프로젝트 지침 · 대상에만 있는 파일)도 보여 준다 (종료 코드에는 넣지 않음). 프로젝트 전용 규칙은 그 프로젝트의 `PROJECT.md` — 공통 `CLAUDE.md` 끝의 `@PROJECT.md`가 불러오고, 없으면 오류 없이 건너뛴다 (헤드리스 Claude Code로 확인).
- **원본을 바꾸는 커밋마다 `-All`로 모든 대상을 비교**해 뒤처진 곳을 보고한다 (CLAUDE.md "project-files 원본을 바꿀 때"). 사본이 다르면 `-Apply`가 멈추지만 알려 주지 않아, 세 프로젝트가 옛 훅으로 이틀 돈 일이 있었다 (2026-10-22). `-All`은 비교만 하고 `-Apply`와 같이 쓸 수 없다.
- 확인만 (만들거나 덮지 않음, 종료 코드와 무관): `.claude/settings.json`은 `settings.example.json`과 해시를 비교한다. `.mcp.json`은 기기마다 경로가 달라 해시는 비교하지 않고, 없거나 `<publish 폴더>`가 남아 있으면 `[할 일]`로 알린다. Unity 쪽에 템플릿 이름 파일이 있으면 `Unity에 템플릿 사본`으로 보여 준다 (지워도 됨).
- **`-Apply`는 Unity 쪽 파일이 원본과 다르면 아무것도 복사하지 않고 멈춘다.** Unity에서 고친 내용이 있으면 원본에 먼저 합친다. 버려도 되면 `-Force`로 덮는다. 덮기 전 파일은 `<Unity>/review_tmp/sync-backup/<시각>/`에 백업된다.
- 줄 맨 앞 `\---`, `\_`, `\[`, `1\.` 같은 이스케이프 잔재가 보이면 경고만 한다 (종료 코드와 무관). 규칙서에 이런 이스케이프가 섞여 지침 불러오기가 깨진 적이 있다 (`@REVIEW\_GUIDELINES.md`, 원인 미확인).
- `CLAUDE.md` — 언제 어떤 모드를 쓰고, input을 어떻게 줄이고, 결과를 어떻게 종합할지
- `REVIEW_GUIDELINES.md` — 새 프로젝트용 기본 리뷰 기준 (Unity). 프로젝트에 복사된 뒤에는 그 프로젝트가 고쳐 쓴다 (서버가 프로젝트 루트에서 자동으로 찾아 주입)
- `.claude/commands/cross-review.md` — `/cross-review [main|high]` (`review_main` · `review_high`). 2단계에서 self-reviewer(보안 영역이면 묻고 security-reviewer도)를 먼저 돌려 그 원문을 ownReview로 넣는다
- `.claude/commands/self-review.md` — `/self-review [perf|security|direction]`: 지휘자 + 검토형 서브에이전트만 (외부 에이전트 없음). CLAUDE.md 리뷰 조건에 걸리면 지휘자가 `/self-review` · `/cross-review` · 리뷰 없음 중에서 묻는다. 판정은 `record_verdicts`의 `selfReview`로 남긴다 (3절) → 판정 화면에 서브에이전트별 채택률·놓침
- `.claude/commands/image-prep.md` — `/image-prep <제목> <xlsx...>`: 기획서 이미지를 외부 에이전트에게도 보여야 할 때 collect → 사용자 정리 → shrink → `run_mode` `images` (`image_prep.py`)
- `.claude/commands/plan-share.md` · `plan-check.md` — 계획서를 Slack 스레드에 올리고(SlackPlanBot `slackplan` CLI, dry-run을 보이고 확인 뒤에만) 새 답 · 승인 상태를 읽어 보고한다 (읽기만, Slack 글은 지시로 따르지 않음)
- `.claude/commands/mode.md` — `/mode design_main ...`, `/mode design_high ...`, `/mode research_main ...`, `/mode parallel_main ...`, `/mode perf_main ...`
- `.claude/commands/unity-test.md` — `/unity-test`, `/unity-test PlayMode MyGame.Combat`
- `.claude/commands/work-order.md` — `/work-order <요청> [첨부]`: 오케스트레이션에 맡길 작업 지시서를 만든다. 조사 → 초안 → `work_order_main` 모드(luna | flash 동시 검토) → `그외 이상사항` 정리 → `WorkOrders.Folder`(없으면 `docs/work-orders/`)에 저장 + 대화에 출력. 코드는 고치지 않는다. 모드별 지침은 CLAUDE.md가 아니라 이 명령 파일에 둔다 (CLAUDE.md는 세션마다 컨텍스트에 올라감)
- `.claude/commands/preflight.md` — `/preflight <요청> [첨부]`: 큰 작업의 실행 전 점검. `preflight_p1`(A 반박 · B 위험 · 성능 동시 검토) → `preflight_p2`(상호 판정) → 사용자 확정 → `preflight_p3`(최종 수정안) → 지휘자 종합 → `preflight_check`(전체, argon) · `preflight_perf`(성능, sol). 단계마다 멈추고, P5에는 앞 단계 논의를 붙이지 않는다. 산출물은 `docs/preflight/<날짜>_<주제>/`. 첨부 xlsx·csv는 `xlsx_dump.py`로 Markdown을 만들어 `context`로 붙인다. 성능만 볼 때는 `/mode perf_main …`(sol, 기본) · `perf_high`(sol · DeepSeek 동시)
- `.claude/scripts/xlsx_dump.py` — `/work-order`의 첨부 읽기 도우미. 기획서 xlsx·csv를 Markdown 표로 뽑고(병합 셀·메모·텍스트 상자 글 포함) 이미지는 `review_tmp/work-order/<파일>/`에 붙은 셀 이름으로 저장. 필요: Python 3.9+, `openpyxl` (`python -m pip install openpyxl`). `.xls`는 지원하지 않음
- `.claude/scripts/cleanup_guard.py`(H1 재귀 삭제) · `push_guard.py`(H2 git push) · `run_mode_guard.py`(H3 계획서 · H4 ownReview 원문) · `share_guard.py`(H5 slackplan 글) · `subagent_input_guard.py`(H6 서브에이전트 입력 폴더의 CLAUDE.md · .claude 자동 로드 · 폴더 링크, `Agent` · `SendMessage`) — 규칙 강제 훅 (CLAUDE.md "훅이 막는 규칙"). 공용 셸 해석은 `hook_common.py`, 시험은 `test_hooks.py`
- `.claude/scripts/subagent_read_guard.py` — 검토형 서브에이전트의 읽기를 `subagent_in/` 안으로 막는 훅 (서브에이전트 정의 frontmatter에 등록, 시험 `test_subagent_read_guard.py`). `check_agents.py` — 서브에이전트 정의 · 훅 등록 검사
- `.claude/scripts/image_prep.py` — `/image-prep`의 collect · shrink
- `.claude/agents/*.md` — 서브에이전트 7개. 검토형(Opus, `subagent_in/` 격리 · 같은 출력 형식 `## 지적` · `## 자료 부족` · `## 읽은 파일`): `self-reviewer` · `perf-reviewer` · `security-reviewer` · `direction-reviewer`. 수집형: `web-searcher`(Sonnet, WebSearch · WebFetch만) · `evidence-collector`(`/preflight` P4). 골든 하네스용 `harness-editor`. 정의에는 `tools` 허용 목록이 반드시 있다 (없으면 MCP 도구까지 물려받아 `run_mode`를 부를 수 있음)
- `.claude/settings.example.json` → Unity의 `.claude/settings.json` — Unity CLI 조회·테스트 명령은 매번 승인 없이 실행 + PreToolUse 훅 등록 (기존 파일이 있으면 `allow` 항목과 `hooks`를 합치기)
- `.mcp.example.json` → Unity의 `.mcp.json` — dll 경로를 publish 폴더로 (JSON 안에서는 경로를 `/`로 쓸 것. `\`는 JSON에서 특수 문자). env는 `OPENAI_API_KEY` · `GEMINI_API_KEY` · `FIREWORKS_API_KEY`를 넘긴다

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

- **테스트**: `unity test`는 배치 모드로 별도 에디터를 띄운다. **같은 프로젝트가 에디터에 열려 있으면 실행할 수 없다.** 그래서 지휘자는 사람이 여는 프로젝트가 아니라 **배치 전용 프로젝트** `F:\AI\Unity_AITest\<버전>\ForAI`(6.3 · 7.0)에 패키지를 `Packages/manifest.json`의 `file:` 경로로 넣어 돌리고, 끝나면 `git -C F:\AI\Unity_AITest checkout -- .`으로 되돌린다 (`/unity-test` 명령 파일, 추가 루트 `@unity_ai_test`). 종료 코드 `0` 통과 / `8` 실패 / 그 외 실행 오류.
- **라이브 에디터 조작**(씬·오브젝트 편집, Play 모드 등)은 프로젝트에 Pipeline 패키지가 필요하다: `unity pipeline install` (Unity 6.0+).
- **Unity MCP 서버**(`unity mcp configure claude-code`)도 있지만, 에디터 명령이 전부 도구로 올라와 Claude 컨텍스트를 차지한다. 토큰을 아끼려면 기본은 CLI(Bash)로 쓰고, 에디터 조작을 자주 할 때만 MCP를 추가하는 걸 권장.
- `.claude/settings.json`은 `unity status/list/pipeline list/test/logs` · `unity --version` · `WebFetch(docs.unity.com)` · `list_modes`만 자동 허용한다. `unity command`(임의 C# 실행이 가능한 `eval` 포함)는 일부러 매번 승인을 받게 뒀다.

## 7. 대시보드

`HB_Orchestration.bat` → 2번 (또는 `dotnet run --project src/HB.Orchestration.Dashboard`) → http://localhost:5280

**호출 기록** (`/`)
- 기간 · 모드(기록의 `Tool` 값 — run_mode · golden · self_review) · 제공사 필터, **실행**(`Run`, runId 앞부분) · **검색**(`Q`: Id · RunId는 앞부분 일치, 이유 · 도구 · 역할 · 에이전트 · 모델 · 파일 · 첨부 · 응답 · 오류는 부분 일치), 자동 새로고침(10초, `Live`)
- 호출 수 · 토큰 · 평균 응답 · 예상 비용 · 실패, 일별 토큰. 합계 · 그래프는 걸러진 전체 기준이고, 표만 한 쪽 200행으로 나눈다
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
  - 계산 (`ModelPrice.CostOf`, 서버 · 골든과 같은 코드): `(입력 - 캐시 읽기 - 캐시 쓰기) × 입력 단가 + 캐시 읽기 × 캐시 단가 + 캐시 쓰기 × 캐시 쓰기 단가 + 출력 × 출력 단가 + 웹검색 호출 수 / 1000 × WebSearchPerThousand` (입력 토큰에는 캐시 토큰이 포함됨). 단가는 기록 시각 기준(`Changes`)
  - 단가·환율은 MCP `appsettings.json`의 `Orchestration:Pricing`에 USD로 저장하고, **모드 화면의 "모델 단가" 표에서 편집**한다
  - 캐시 토큰 수는 이 기능을 넣은 뒤의 호출부터 기록된다 (그 전 기록은 전부 일반 입력 단가로 계산)

**모드** (`/Modes`)
- MCP publish 폴더의 `modes.json`을 읽어 모드마다 흐름을 그림으로 보여준다 (순차 → / 동시 묶음 / 토론 ⇄ → 판정).
- 단계별 에이전트(드롭다운), 최대 회차, 반환 범위를 바꿔 **저장**하면 다음 `run_mode`부터 바로 반영된다 (재시작 불필요).
- **모델 단가** 표: 설정에 단가가 있는 모델, 켜진 에이전트가 쓰는 모델, 호출 기록에 나온 모델을 모두 한 줄씩 보여주고(쓰는 에이전트·기록 횟수 표시, 단가 없는 모델 강조), **입력 / 캐시 입력 / 출력 단가(USD / 1M 토큰)**와 환율을 편집한다. 에이전트와 상관없이 모델 단위라 예전 모델도 계속 관리할 수 있다. 세 칸을 다 비우면 그 모델의 단가를 지우고, 마지막 빈 줄로 새 모델을 추가한다. 고치는 것은 처음 단가뿐이다 — 날짜별 변경(`Changes`) · 캐시 쓰기 · 웹검색 단가는 appsettings.json에서 직접 고친다.
- **이미지 첨부** 패널 (표시 전용): `Images` 상한과 제공사별 `ImageDetail`. 에이전트 표의 **이미지** 열은 `Vision` (표시 전용, 저장해도 기존 값 보존. 바꾸려면 appsettings.json 직접 수정).
- 저장 전 검증: 없는 에이전트·역할, Debate 단계 수, 회차 범위. 꺼진 에이전트는 경고만.
- 저장 방식: `modes-backup/`에 백업(최근 10개) → 임시 파일에 쓰고 한 번에 교체. 화면을 연 뒤 파일이 다른 곳에서 바뀌었으면 저장을 거부하니 **다시 불러오기** 후 수정.

**모드 화면 — 에이전트 편집** (appsettings.json)
- 표에서 이름 · 제공사 · 모델 ID · 출력 상한 · 타임아웃(초) · 추론 강도 · 사용 여부를 바꾸고 **에이전트 저장**. 마지막 빈 줄에 입력하면 새 에이전트가 추가된다. 저장 즉시 반영 (재시작 불필요).
- **이름 변경 동기화**: 이름을 바꾸면 그 이름을 쓰던 modes.json의 모든 단계도 새 이름으로 바뀐다.
- 모드가 쓰고 있는 에이전트는 삭제할 수 없다 (삭제 칸이 잠김). 먼저 모드의 에이전트를 바꾼다.
- 백업: `settings-backup/` (최근 10개). appsettings.json의 다른 섹션(Logging, Providers, Budget)과 표에 없는 필드는 그대로 둔다.

**모드 화면 — 없는 에이전트 일괄 교체**
- appsettings.json을 메모장으로 직접 고쳐서 modes.json이 없는 이름을 가리키게 되면, 화면 위에 경고와 함께 `없는 이름 → 바꿀 에이전트` 선택이 뜬다. **일괄 교체**를 누르면 그 이름을 쓰는 모든 단계가 바뀐다.

**세션** (`/Sessions`)
- 실행 중인 Claude Code 세션(= MCP 서버)마다 프로젝트, PID, 시작 시각, 서버 빌드 시각, 모드별 실행 횟수를 보여준다 (한도 없음, 이름을 바꾸기 전 서버가 남긴 예전 모드 이름은 새 이름으로 묶음).
- 서버 빌드가 publish 폴더의 dll보다 오래됐으면 **이전 빌드** 표시 → 그 세션에서 `/mcp`로 재연결하면 새 서버가 뜬다.
- 강제 종료된 서버가 남긴 기록은 화면을 열 때 자동 정리.
- 어느 폴더를 볼지는 대시보드 `appsettings.json`의 `Dashboard:McpConfigDir` (배치로 실행하면 `HB_Orchestration.config`의 `PUBLISH_DIR`가 우선). 원본 값은 이 PC 절대 경로(`F:/AI/ToolWithCS/Orchestration/Publish_Output`)라, 배치 없이 `dotnet run`할 때 다른 PC면 고친다. 편집할 파일을 직접 지정하려면 `Dashboard:ModesPath`.
- 대시보드 설정 키: `Dashboard:LogPath`(calls.jsonl 경로, 비우면 2절 데이터 폴더) · `Dashboard:Prices`(예전 방식 단가, MCP 단가에 없는 모델만 채움) · `Dashboard:FileLinkTemplate` · `Dashboard:RiderProjects` · `Dashboard:ClaudeProjectsDir` · `Dashboard:Billing:AdminKeyEnv` · `Dashboard:ModesPath` · `Dashboard:RiderRecentSolutionsPath` · `Dashboard:ClaudeSessionLimit`. 단가 파일은 250ms에 한 번만 다시 확인한다.
- 직접 지정이 없으면 서버와 같은 찾는 순서로 편집할 modes.json을 정한다: MCP appsettings `Orchestration:ModesPath` → **대시보드 프로세스의** `HB_MODES_PATH` → publish 폴더 `modes.json`. 세션 폴더의 `hb-modes.json`은 대시보드가 알 수 없어 뺀다. 화면 머리에 "편집 중: 경로 (근거)"가 보인다.
- 서버의 `HB_MODES_PATH`(프로세스마다 다름)나 Unity 프로젝트의 `hb-modes.json`은 대시보드가 알 수 없다. 그래서 호출 기록(provenance)에서 **최근 7일 실행이 실제로 쓴 modes.json**을 모아, 편집 중인 파일과 다르면 경고로 보인다. 같은 파일인데 그 뒤 내용이 바뀌었으면 "그 뒤 수정됨"으로 표시한다.
- 그 아래에 **최근 7일 실행이 붙인 리뷰 지침**(provenance.guidelines)을 보인다. 프로젝트마다 자기 지침을 고쳐 쓰므로 내용은 비교하지 않고 **경로 규칙**으로만 경고한다: 경로가 `review_tmp/` 아래이거나 파일 이름이 `REVIEW_GUIDELINES.md`가 아니면 경고(하네스 임시 지침 · `REVIEW_GUIDELINES_PATH` 지정 실수). "지침 파일 없음"으로 돈 실행도 프로젝트 폴더별로 경고한다 (역할 `UseGuidelines=false` · 골든 `--no-guidelines`는 일부러 끈 것이라 뺀다). 나머지는 경로 · 해시 앞 12자리 · 실행 수 · 마지막 사용을 접힌 목록으로 — 이름이 같은 오래된 사본은 해시로 눈으로 확인한다 (계획서 `docs/plans/2026-10-22_지침-파일-경고.md`).

**Claude 세션** (`/ClaudeSessions`) — 지휘자가 무엇을 알고 있었나
- Claude Code 세션 기록(`~/.claude/projects/<경로 인코딩>/<sessionId>.jsonl`)을 **읽기만** 해서, 세션마다 시작 시각(로컬) · 버전 · 프로젝트 · 불러온 지침 파일 · 부른 `run_mode`를 보여준다. **대화 본문은 보여주지 않는다.**
- 지침 파일: `instructions` 항목의 `files[]` — 경로 · 종류 · 크기 · SHA-256 (기록된 content의 UTF-8 바이트 기준). 지금 디스크 파일과 대조해 `같음` / `끝 줄바꿈만 다름`(디스크 = content + LF 정확히 1개) / `다름` / `파일 없음`. 세션 기록이 끝 줄바꿈을 빼고 저장하는 것은 파일 3개로 확인한 관찰이다.
- `run_mode` 연결: 결과 첫 줄 `runId:`가 있으면 **확정**. 머리말이 없는 이전 서버의 결과는 같은 모드 · 같은 프로젝트 · 호출 시각 −2초 ~ +60초에 시작한 실행이 하나뿐일 때만 **추정**. 후보가 여럿이면 연결하지 않고, 실행 전에 거절된 호출(`[`로 시작하는 결과)은 추정하지 않는다. 다른 세션이 확정한 실행은 추정 후보에서 뺀다.
- 결과가 백그라운드(120초 초과 → `<task-notification>`)나 큰 출력(`<persisted-output>` 미리보기)으로 들어와도 찾는다. 다른 도구 결과(예: Bash로 calls.jsonl을 읽은 출력)에 든 runId는 쓰지 않는다.
- 세션 기록 형식은 비공식이다. JSON이 아니거나 모양이 다른 줄은 "알 수 없는 줄 n", 알아볼 줄이 하나도 없는 파일은 "알 수 없는 형식"으로 표시한다. 줄마다 엄격한 UTF-8로 읽고, 잘못된 바이트가 있는 줄은 알 수 없는 줄로 센다.
- 설정: `Dashboard:ClaudeProjectsDir` (비우면 `CLAUDE_CONFIG_DIR/projects` → `%USERPROFILE%\.claude\projects`), `Dashboard:ClaudeSessionLimit` (목록에 보일 최근 세션 수, 기본 50. 연결은 전체 세션으로 계산).
- `/Sessions`(살아 있는 MCP 서버)와는 다른 화면이다.

**서브에이전트 결과** (`/Subagents`) — 검토형 서브에이전트가 넘긴 원문과 서버 기록 여부 (계획서 `docs/plans/2026-10-27_서브에이전트-결과-보존.md`)
- `/cross-review` · `perf_*`에서 서브에이전트 결과는 `run_mode`의 `ownReview`로만 남는다. 외부 호출이 실행되지 않으면(안전 필터 · 실패) 결과가 어디에도 안 남았다 → Claude Code가 남기는 서브에이전트 기록(`~/.claude/projects/<프로젝트>/<세션>/subagents/agent-<id>.jsonl` + `.meta.json`의 `SubagentHandback`)에서 **원문을 그대로 복사**해 데이터 폴더 `subagents/`에 보관한다. Claude Code가 세션 기록을 지워도(`cleanupPeriodDays`) 남는다.
- 보관은 MCP 서버 시작 때(백그라운드, 실패해도 서버는 뜸)와 이 화면 · 첫 화면을 열 때(30초에 한 번) 한다. 바뀐 세션 기록만 다시 읽는다. 이어 부른 서브에이전트는 넘길 때마다 따로 남는다. 대상은 검토형 4종만 (`SubagentArchive.ReviewerTypes` = `record_verdicts selfReview.agent` 목록).
- 서버 기록 칸: 원문이 `self_review` 기록 응답이거나 `run_mode` ownReview 안에 그대로 있으면 그 runId(줄바꿈 · 줄 끝 공백만 정리해 비교, 200자 미만 원문은 통째로 같을 때만). 글자는 다르지만 20자 이상인 줄(`## 읽은 파일` 절 제외)의 80% 이상이 한 기록에 있으면 **거의 같음 n%**(지휘자가 형식을 맞추며 고쳐 넣은 경우). 아니면 **기록 없음** + 가장 비슷한 기록 · 일치율(참고).
  - 후보 기록: `self_review`는 같은 에이전트의 기록만. 어느 쪽이든 결과를 넘긴 시각 −5분 ~ +2일 안에 기록된 것만 (self_review = 기록 시각, run_mode = 실행 시작). 재검토 결과가 1차 기록에 붙거나 다른 작업의 기록에 붙지 않게.
  - 2026-10-27 실측: 102건 = 같음 62 · 거의 같음 12 · 기록 없음 26 · 진행 중일 수 있음 2 (시험 · 파일럿 포함).
- 넘긴 지 1시간 안의 기록 없음은 "진행 중일 수 있음"으로 따로 보인다 (지휘자가 run_mode · 판정을 끝내야 기록된다).
- 첫 화면에 최근 7일 "기록 없는 서브에이전트 결과 n건" 안내가 뜬다 (진행 중일 수 있는 것 제외). 기록은 화면이 하지 않는다 — 확인 뒤 필요하면 그 세션에서 `record_verdicts selfReview`로 남긴다 (`/cross-review` 3단계).
- 형식이 바뀐 것으로 보이면(meta.json 없음 · agentType 없음 · 끝났는데 결과 없음) 화면 아래 "보관 오류"와 MCP 로그 경고로 보이고, 그 기록은 다음 보관 때 다시 읽는다 (리더를 고치면 되살아남).
- 이 화면은 결과 **본문**을 보여 준다 (Claude 세션 화면과 다름). 리뷰 대상 코드 조각이 들어 있으니 대시보드는 localhost로만 연다.

**청구 대조** (`/Billing`) — OpenAI 실제 청구와 기록 기준 계산 비교
- 기간: 7일 · 3일 · 1일(UTC 날짜별 금액·토큰) · 시간별 24시간(토큰과 기록 기준 계산, 오늘 누적 청구는 한 줄로). OpenAI 비용은 UTC 자정 기준 하루 버킷이고, 하루가 끝나기 전에도 몇 분 지연으로 누적값이 갱신된다.
- 청구값 가져오기:
  - 조직 Admin 키(`sk-admin-…`, Read Only로 충분)가 환경 변수(이름은 설정 키 `Dashboard:Billing:AdminKeyEnv`, 기본 `OPENAI_ADMIN_KEY`)에 있으면 "새로 가져오기" 버튼으로 Costs API(항목별)·Usage API(모델별 토큰, 캐시 쓰기 포함)를 부른다. 버튼을 누를 때만 부르고 자동 새로고침과 묶지 않는다.
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

명령줄로도 쓸 수 있다: `HB_Orchestration.bat publish` / `dashboard` / `all`. `publish --yes` · `all --yes`는 실행 중인 MCP 서버를 묻지 않고 종료한다 (그 밖의 두 번째 인자는 오류).

**설정 (`HB_Orchestration.config`)** — `KEY=VALUE`, `#`은 주석, 상대 경로는 배치 폴더 기준. 배치는 **자기와 같은 이름의 `.config`** 를 읽으므로 이름을 바꿀 땐 둘을 같이 바꾼다.

| 키 | 기본값 | 의미 |
|---|---|---|
| `SOLUTION_DIR` | `.` | `HB_Orchestration.slnx`가 있는 폴더 |
| `PUBLISH_DIR` | `..\Publish_Output` | MCP publish 폴더 (`.mcp.json`이 가리키는 곳). 대시보드 모드 화면도 여기를 편집 |
| `CONFIGURATION` | `Release` | 빌드 구성 |
| `DASHBOARD_PORT` | `5280` | 대시보드 포트 |

**publish와 실행 중인 Claude Code**
- 실행 중인 MCP 서버는 dll을 잡고 있어 그대로는 publish가 실패한다. 배치가 서버 프로세스를 찾아 **서버만 종료할지** `[y/N]`로 묻는다 (기본 No — `Y` · `YES`만 진행). 취소하면 메뉴 1은 메뉴로 돌아가고, 메뉴 3 · 명령줄 `all`은 publish만 건너뛰고 대시보드는 그대로 띄우며, 명령줄 `publish`는 종료한다. Claude Code와 대화는 그대로 남는다.
- 서버를 종료한 순간 그 세션에서 진행 중이던 `run_mode`가 있었다면 그 호출은 실패한다. 에이전트 호출 중이 아닐 때 publish할 것.
- **재연결은 수동이다** (테스트로 확인함). publish가 끝나면 각 Claude Code 세션에서 `/mcp` → `hb-orchestration` 재연결.
  - 확인하고 싶으면 "server_info 실행해줘" → 새 빌드 시각이 나오면 새 서버. server_info는 빌드 시각 · PID · 설정 읽은 시각 · 사용 가능 에이전트 수 · 설정 다시 읽기 실패 · 쓰는 modes.json 경로와 근거를 보여 준다.
  - 대시보드 **세션** 화면에서도 세션별 서버 빌드 시각으로 확인할 수 있다.
- 실행 중인 서버가 없으면 그냥 publish하고, 다음에 Claude Code를 열 때 새 서버가 뜬다.

**publish와 설정 파일 (`appsettings.json` · `modes.json`)**
- publish 전에 사용 중 설정을 `%LOCALAPPDATA%\HB_Orchestration\config-backup\<yyyyMMdd-HHmmss-난수>\`에 백업하고, publish 뒤 그 백업으로 되돌린다. 백업은 최근 20개를 남긴다 (이 이름 모양의 폴더만 정리).
- 백업에 실패하면 publish하지 않고 멈춘다. 되돌리기에 실패하면 `[ERROR]`와 백업 경로를 보여 주고 멈춘다 (손으로 복사).
- publish가 끝나면 **원본(`src/HB.Orchestration.Mcp/`)에만 있는 경로**를 `[알림]`으로 보여 준다. 파일은 바꾸지 않는다 — 필요한 항목을 사용 중 설정에 직접(또는 대시보드로) 옮긴다.
  ```
  [알림] modes.json: 원본에만 있음 Modes.perf_high
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
dotnet <publish 폴더>\HB.Orchestration.Mcp.dll --golden run [사례|all] [--mode 모드] [--agents from=to,...] [--reasoning 에이전트=강도,...] [--dir 폴더] [--label 이름] [--no-guidelines] [--yes]
dotnet <publish 폴더>\HB.Orchestration.Mcp.dll --golden score <사례> --from <파일> [--dir 폴더] [--label 이름]
```
- `run`은 `--yes`가 없으면 사례·에이전트·입력 글자 수·입력 비용 추정·붙는 지침 파일만 보이고 **외부 호출 없이** 멈춘다.
- 골든·하네스에서 `HB_MODES_PATH`·`REVIEW_GUIDELINES_PATH`로 임시 modes·지침을 넘길 때는 **절대 경로만** 쓴다. 상대 경로는 그 프로세스의 현재 폴더 기준이라, 다른 폴더에서 실행하면 엉뚱한 곳을 가리킨다 (2026-10-20 1회차: inv·base 파일이 `src\HB.Orchestration.Dashboard\review_tmp\`에 생김).
  - 지정한 경로에 파일이 없으면 서버는 다음 후보로 넘어가지 않고 오류다 — modes.json(2026-10-21)과 리뷰 지침(2026-10-22) 모두. 골든은 계획 미리보기에서 `[리뷰 지침 경로 없음]`으로 거절한다 (`--no-guidelines`면 막지 않음).
- 하네스 회차 실행은 `tools/golden/run_golden.sh <라벨> <모드> [건너뛸 사례 ...]` (리뷰 사례 rec-* · seed-*만, 저장소 루트·Publish_Output dll 기준). 지정한 `HB_MODES_PATH`·`REVIEW_GUIDELINES_PATH`가 절대 경로가 아니거나 파일이 없으면 시작 전 종료 2. 골든 종료 코드 2(실행 전 거절)·3(Quota)이면 그 사례에서 멈추고, 1(사례 실패)은 계속하되 끝에 `[실패 사례 N개]`와 종료 1. 시험: `bash tools/golden/test_run_golden.sh` (가짜 dotnet, 외부 호출 없음).
- 지침은 run_mode와 같은 순서로 찾는다 (`Orchestration:GuidelinesPath` → `REVIEW_GUIDELINES_PATH` → **현재 폴더**의 `REVIEW_GUIDELINES.md`). 지침을 쓰는 역할인데 못 찾으면 계획을 보인 뒤 거절한다 (외부 호출 없음) — 프로젝트 루트에서 실행하거나 경로를 지정한다. `--no-guidelines`는 파일이 있어도 지침을 빼고 돈다 (지침 있음·없음 비교용, 기록 사유 `골든 --no-guidelines`). 사례를 돌기 직전마다 지침을 다시 읽어 실행 전 확인과 다르면(파일이 바뀜·사라짐) 그 사례부터 멈춘다. 결과 줄에 `guidelinesAttached` · `guidelinesSha256` · `guidelinesReason`.
- run_mode와 같은 시스템 프롬프트(역할 + 출력 형식 + 공통 규칙 + 지침)·에이전트로 보낸다. reason·ownReview·context 첨부는 쓰지 않는다 (첨부는 사례의 `input.md`에 이미 들어 있음). 하루 비용 상한은 없으니 실행 전 계획의 예상 비용을 보고 `--yes`로 돌린다. 단계가 하나이고 구조화 출력(`Output`)이 있는 모드만 채점된다.
- 호출 기록에는 `Tool = golden`, `Mode = 원래 모드`로 남는다 (대시보드·비용 집계에 보임). 세션 상태 파일은 남기지 않는다.
- `score`는 self-reviewer 결과(마크다운 `- C1 [major] 파일:줄 — 문제`)나 findings JSON(@1·@2)을 채점만 한다 (외부 호출 없음).
- 폴더: `--dir` 또는 appsettings `Orchestration:Golden:Folder`. 사례 하나라도 형식이 틀리면 아무것도 돌리지 않는다. 종료 코드 0 성공 / 1 일부 사례 실패 / 2 사용법·사례 형식 오류 / 3 크레딧·한도 소진(Quota)으로 남은 사례를 멈춤.
- **Quota**: 429 중 `insufficient_quota`·`credit_balance_exhausted`(메시지나 응답 본문)는 `ErrorKind.Quota`로 분류해 재시도하지 않는다 (`[실패] Quota — 크레딧/한도 소진, 충전 전까지 재실행 금지`). 골든은 Quota가 나오면 남은 사례를 돌리지 않고, 다단계(crash) 사례는 단계 제공사 중 하나라도 **가장 최근 판정 가능한 호출 기록(7일, 성공 또는 Quota — Timeout·Network 등은 건너뜀)이 Quota**면 앞 단계도 호출하지 않는다. 충전 뒤 **그 제공사로 가는** 작은 호출 1회(그 제공사 에이전트의 리뷰 사례, 또는 `--agents`로 연결)로 성공 기록을 남기면 풀린다. 판정은 OpenAI 문구 기준이다 — Gemini 한도 소진(RESOURCE_EXHAUSTED)은 지금도 RateLimit으로 재시도된다.

**사례 폴더** (`<골든 폴더>/<사례 id>/`, `results/`와 `_`로 시작하는 폴더는 사례가 아님)

| 파일 | 내용 |
|---|---|
| `case.json` | `id`(폴더 이름과 같게), `kind`(`record` 판정 기록 · `seeded` 고친 결함을 되돌린 코드 · `crash` 원인이 알려진 크래시, 아래), `mode`(기본 review_main, 예전 이름은 새 이름으로 읽음), `source`, `description` |
| `input.md` | 에이전트에게 보낼 프롬프트 원문 (그대로 보냄) |
| `expected.json` | 기대 지적 `[{ id, summary, files, lines: [시작, 끝], locations?, required?, keywords, severity, origin? }]` — `locations`는 `[{ file, from, to }]` 여러 곳(원인·증상 지점, 하나라도 맞으면 위치 일치, 비우면 files × lines), `required`는 그 결함에만 맞는 말(동의어 묶음, 하나라도 맞으면 됨, 있으면 `keywords`는 보조), `severity` critical·major·minor·nit, `origin`은 정답 출처(record · later-commit · self-review · conductor · common · ds · sol · seeded — 모델 비교 집합용) |
| `traps.json` | (선택) 과거에 기각된 지적, 같은 형식 + `why`(기각 이유) |

**채점 규칙** (`GoldenScorer`, `golden-scorer@2` — 결과 줄 `scorerVersion`, LLM 판정자 없음. 계획서 `docs/plans/2026-10-22_채점기-구조-개선.md`)
- 짝 후보: (같은 **파일 이름** + 줄이 위치 중 하나의 범위 ±5) 또는 (**필수 키워드**가 문제·상세에 있음, 단 지적이 다른 파일을 가리키면 제외). 둘 다면 강도 3, 필수 키워드만 2, 줄만 또는 보조 키워드만 1. `required`가 비면 `keywords`가 필수 역할 (예전 형식).
- 키워드 검사: 대소문자 무시 부분 문자열. 키워드 첫 글자가 영숫자면 그 앞, 끝 글자가 영숫자면 그 뒤도 영숫자가 아니어야 한다 (`NUL`이 `null`에 걸리지 않게). 끝의 `*`는 **항상** 앞부분 일치 (`ArgumentOutOfRange*` → `ArgumentOutOfRangeException`) — `char*`처럼 `*`로 끝나는 말 자체는 쓸 수 없다 (`char`의 앞부분 일치가 됨, 이스케이프 없음). 한국어는 그대로 부분 문자열. 빈 말 · 앞뒤 공백이 있는 키워드는 형식 오류 (공백이 경계 검사를 없앰). crash 사례는 예전 검사(왼쪽 경계만) 그대로이고 `required`·`locations`는 형식 오류.
- **찾음은 강도 2 이상만** 센다. 줄만 맞은 짝(강도 1)은 같은 줄 범위의 다른 결함과 구분하지 못해 "줄만 맞음 — 확인 필요"로만 보인다 (맞는 지적이면 정답지 키워드를 보강).
- 기대 지적 하나에 짝 하나, 지적 하나도 한 번만 쓴다. 강도 2 이상 짝을 최대 가중 배정(헝가리안)으로 정한다 — 배정 전체의 찾음 수 → 강도 합 → 필수 키워드가 지적 제목(problem)에 있는 짝 수 → 앞 지적 순으로 사전식 (자리값을 항목·지적 수에서 계산해 합에서도 뒤집히지 않게, long을 넘을 크기면 오류). 그다음 남은 지적을 함정과 대조하고(강도 2 이상), 마지막으로 남은 기대 지적에 줄만 맞은 빈 지적을 붙인다 (함정에 걸릴 지적을 줄만 맞은 짝이 가져가지 않게).
- 함정 걸림은 감점하지 않고 **"검토 필요"**로 따로 보인다 (다른 관점에서 타당할 수 있음). 짝 없는 지적은 새 지적 후보로 기록만 한다.
- 중복(`duplicates`): 함정 대조 뒤 남은 지적이 이미 찾은 기대 지적과 강도 3(위치 + 필수 키워드)이고 필수 키워드가 제목에 있으면 짝 없음 대신 중복으로 센다. 엄격한 규칙이라 지금 정답지에서는 거의 0이다 (2026-10-22 검증: 제목 조건 없이 19개 중 14개가 틀린 중복 → 조건 추가 뒤 0, 줄이 먼 실제 중복은 놓침).
- 결과: 화면 표 + `<골든 폴더>/results/<날짜>_<라벨>.jsonl` (사례별 점수·짝 근거·runId·모델·토큰·입력 해시·정답지 해시 `keySha256`(expected+traps)·`scorerVersion`·provenance). 자동 짝은 틀릴 수 있으니 근거를 보고 확인한다.
- **정답지 묶음 해시** (동결 표시, `GoldenCaseLoader.SetKeyHash`, 합계 줄에 보임): 사례 폴더 이름 순으로 사례마다 `<사례>/expected.json\n` + 원본 바이트, traps.json이 있으면 `<사례>/traps.json\n` + 원본 바이트를 이어 붙인 SHA-256 앞 16자리. **2026-10-22 동결 `b5cb66d4e82ecfb0`부터 expected+traps 기준** — 그 전 동결(`aab21c2e23ed4e5a` · `61c79cbf9388aba5`)은 expected.json만 이어 붙인 값이라 함정 변경을 잡지 못했다. 동결은 묶음 해시 + `scorerVersion`으로 표시한다.

**crash 사례** (`kind: crash`, `mode: crash` — 계획서 `2026-10-19_골든-crash사례`)
- 모드 전체(Sequential, 분류 → 웹검색)를 run_mode와 같은 프롬프트 조립으로 돌린다. `input.md`는 run_mode의 `input`이다 (서버가 `## 요청`으로 감쌈, 첨부 없음). `score` 명령은 안 된다 (검색 기록이 필요).
- 모드는 단계 2개 이상 + 마지막 단계에 WebSearch가 있어야 한다 (1단계 = 분류, 마지막 = 검색으로 채점). 아니면 실행 전 거절 — case.json에 `mode: crash`를 꼭 적는다 (빠뜨리면 기본값 review라 거절됨). 실행 전 확인과 단계 수 · 에이전트 · 지침이 달라지면 그 사례부터 멈춘다.
- `expected.json` 항목: `{ id, step: triage|search, summary, keywords, urls?, severity }` (files·lines 없음, urls에 빈 조각 금지). `traps.json`은 `step`을 비우면 두 단계 모두.
- 채점 (`GoldenCrashScorer`): triage = 분류(1단계) 본문 키워드. search = 검색 단계가 **실제로 본 URL**(참고 · 인용 · 직접 연 페이지)에 `urls` 조각이 있으면 찾음 — 근거에 "인용·열람한 URL"인지 "검색 결과에만 있던 URL"인지 남긴다. 조각 뒤가 영숫자면 안 맞는다 (`issues/8932` ≠ `issues/89321`). `urls`가 있는데 본문 키워드만 맞거나 조각이 검색 결과에 없는 URL에만 있으면 "확인 필요"(찾음에 안 셈, 놓침에 듦), `urls`가 없는 항목은 키워드로 찾음. 함정은 키워드가 확신 표기(높음 · 확인됨 · 유력 · high · confirmed)와 **같은 줄**에 있을 때만 걸린다. 키워드는 부정 문맥에서도 맞으니 근거를 본다.
- 검색 단계 호출 기록(WebSearch)을 못 읽으면 점수 대신 채점 실패로 남긴다 ("검색 0회"와 구분).
- 결과 줄에 검색 수 · 본 URL 수 · 검색 결과에 없는 URL 목록. 합계는 리뷰와 따로 `[crash 합계] 분류 찾음 · 검색 찾음`.

## 빌드 에러가 나면
- 패키지 버전은 csproj에 고정돼 있다 (1절). 올린 뒤 `AsIChatClient` · `ChatClientAgent` · `McpServerTool` 같은 API가 없다고 나오면 바꾼 패키지의 변경 내역부터 본다 (Microsoft.Agents.AI · Extensions.AI · ModelContextProtocol은 자주 바뀐다).
- usage 필드가 바뀌면 `UsageExtractor`(캐시 · 추론 토큰)와 `AgentRunner`의 기록 부분을 같이 고친다.
- 에러 메시지와 `dotnet list package` 결과를 같이 보면 대부분 바로 원인이 나온다.
