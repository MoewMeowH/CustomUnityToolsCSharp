# 에이전트 설정 (appsettings.json)

[← README](../README.md)

## 에이전트 — `appsettings.json` ("누가")

에이전트 하나 = 제공사 + 모델 + 출력 상한입니다.

- 이름은 자유입니다 (영문 · 숫자 · `_` · `-`).
- 등급별로 나눠 두면 모드에서 고르기 쉽습니다.

원본 `src/HB.Orchestration.Mcp/appsettings.json`의 에이전트 (이름 = `{회사}_{등급}_{모델 별칭}`):

| 이름 | 제공사 | 모델 | 쓰는 곳 |
|---|---|---|---|
| `gpt_high_astra` | openai | gpt-6-astra | 교체용 상위 모델 (기본 모드에는 없음) |
| `gpt_main_sol` | openai | gpt-6.1-sol | 리뷰 · 반박 · 판정 · 성능 · preflight 대부분 |
| `gpt_mini_luna` | openai | gpt-6-luna | 지시서 검토 (work_order_main) |
| `gpt_mini_luna_web` | openai-web | gpt-6-luna | 웹검색 단계 (research_main Scout · crash_main CrashSearch) |
| `gemini_high_argon` | gemini | gemini-4-argon | crash 분류 · preflight B · preflight_check |
| `gemini_main_flash` | gemini | gemini-3.8-flash | 논리 검증 · 토론 상대 · parallel · 지시서 검토 |
| `gemini_mini_lite` | gemini | gemini-3.5-flash-lite | 가벼운 교체용 |
| `deepseek_main_flash` | fireworks | `accounts/fireworks/models/deepseek-v4p1-flash-1017` | review_high · perf_high의 두 번째 시선 (`Vision: false`) |
| `local` | local (Ollama) | `ollama list`의 모델 | 꺼 둠 (`Enabled: false`) |
| `claude_cli_opus` | anthropic-cli | claude-opus-5-5 | API 리뷰 `sub_*` ([claude-cli 제공사](#claude-cli-제공사-sub_-모드)) |
| `codex_cli_sol` | codex | gpt-6.1-sol | `review_main` 1순위 (ChatGPT 구독, [codex-cli 제공사](#codex-cli-제공사-review_main)) |

제공사는 일곱입니다.

| 제공사 | Type |
|---|---|
| `openai` · `gemini` · `fireworks` · `local` | `openai-compatible` |
| `openai-web` | `openai-responses` |
| `anthropic-cli` | `claude-cli` |
| `codex` | `codex-cli` |

실제로 쓰는 값은 publish 폴더의 appsettings.json입니다. `list_modes` 마지막의 `에이전트:` 줄에 보입니다.

알아 둘 것:

- **`MaxOutputTokens`는 넉넉하게** (1만 이상 권장). 응답 상한인데, **추론 모델은 생각 토큰도 포함**됩니다. 낮게 잡으면 답변 없이 끝나고, 서버가 `[빈 응답]` 실패로 기록합니다. 폭주 방지용으로만 씁니다.
- 키가 없거나 설정이 틀린 에이전트는 그것만 건너뜁니다. 서버는 정상 시작하고, `list_modes`에 `(사용불가)`로 표시됩니다.
- **저장하면 바로 반영됩니다.** 서버가 호출 때마다 파일 수정 시각을 보고, 바뀌었으면 설정 · 에이전트를 다시 읽습니다. 재시작은 필요 없습니다.
  - JSON이 깨졌으면 이전 설정으로 계속 동작하고, `list_modes` · `server_info`에 경고가 뜹니다.
- 대시보드 **모드** 화면에서 추가 · 수정 · 삭제할 수 있습니다 ([대시보드](dashboard.md)). **이름을 바꾸면 그 이름을 쓰는 modes.json 단계도 같이 바뀝니다.**
- appsettings.json도 주석 없는 순수 JSON으로 둡니다. 필드 설명은 아래 표에 있습니다.

## appsettings.json 필드 (`Orchestration` 섹션)

### 경로

| 키 | 비우면 | 의미 |
|---|---|---|
| `ModesPath` | [modes.md](modes.md)의 찾는 순서 | modes.json 위치를 직접 지정 |
| `GuidelinesPath` | `REVIEW_GUIDELINES_PATH` 환경 변수 → Unity 프로젝트 루트의 `REVIEW_GUIDELINES.md` | 리뷰 지침 파일 |
| `PromptsPath` | modes.json 옆 `prompts/` | 모델별 덧붙임(외부 에이전트 실수 목록) 폴더. 원본은 `project-files/prompts`. 상대 경로는 서버 폴더 기준 |

- `GuidelinesPath`: 직접 지정한 경로(이 설정 또는 환경 변수)에 파일이 없으면, 다음 후보로 넘어가지 않고 run_mode가 `[리뷰 지침 경로 없음]`으로 거절합니다. modes.json 지정 경로와 같은 규칙입니다.
- `PromptsPath`: 지정했는데 폴더가 없으면 `[모델별 덧붙임 폴더 오류]`로 거절합니다 ([modes.md](modes.md) "모델별 덧붙임").

### 제공사 (`Providers.<이름>.*`)

| 키 | 생략하면 | 의미 |
|---|---|---|
| `Type` | — | 아래 Type 표 |
| `ExecutablePath` | `claude`는 PATH, `codex`는 npm 전역 설치 → PATH | `claude-cli` · `codex-cli` 전용. 실행 파일 절대 경로 |
| `Endpoint` | (OpenAI는 비움) | API 주소. 아래 주소 표 |
| `ApiKeyEnv` | (로컬처럼 키가 필요 없으면 생략) | API 키를 담은 **환경 변수 이름** (`OPENAI_API_KEY` · `GEMINI_API_KEY` · `FIREWORKS_API_KEY`). 키 값은 서버 프로세스가 시작될 때 물려받은 값만 보입니다 |
| `TimeoutSeconds` | SDK 기본 100초 | 요청 하나의 네트워크 타임아웃(초). 에이전트에 값이 없을 때 적용 |
| `MaxAttempts` | 2 | 한 단계의 최대 시도 횟수 (첫 시도 포함). 에이전트에 값이 없을 때 적용 |
| `PromptCache` | `ProviderDefault` | 프롬프트 캐시 방식. 아래 [프롬프트 캐시](#프롬프트-캐시-promptcache) |
| `ImageDetail` | 보내지 않음 (= 제공사 기본값) | 이미지 부분에 실을 `detail` 값 (OpenAI: `auto` · `low` · `high` · `original`) |
| `StructuredOutput` | `Prompt` | 구조화 출력 요청 방식. 아래 [구조화 출력](#구조화-출력-structuredoutput) |

`ImageDetail`이 제공사 단위인 까닭: Gemini OpenAI 호환 엔드포인트가 이 필드를 문서화하지 않습니다.

**Type**

| 값 | 뜻 |
|---|---|
| `openai-compatible` | Chat Completions — OpenAI · Gemini · Fireworks · Ollama · LM Studio |
| `openai-responses` | OpenAI Responses API, 웹검색 단계용 |
| `claude-cli` | 아래 [claude-cli 제공사](#claude-cli-제공사-sub_-모드) |
| `codex-cli` | 아래 [codex-cli 제공사](#codex-cli-제공사-review_main) |

**Endpoint**

| 제공사 | 주소 |
|---|---|
| OpenAI | (비움) |
| Gemini | `https://generativelanguage.googleapis.com/v1beta/openai/` |
| Fireworks | `https://api.fireworks.ai/inference/v1` |
| Ollama | `http://localhost:11434/v1` |
| LM Studio | `http://localhost:1234/v1` |

### 에이전트 (`Agents[].*`)

| 키 | 생략하면 | 의미 |
|---|---|---|
| `Name` | — | 에이전트 이름 (영문 · 숫자 · `_` · `-`, 64자 이내). modes.json의 `Agent`가 이 이름을 가리킵니다 |
| `Provider` | — | 위 `Providers`의 이름 |
| `Model` | — | 제공사의 모델 ID |
| `MaxOutputTokens` | 제한 없음 | 응답 상한 |
| `TimeoutSeconds` | 제공사 값 → SDK 기본 100초 | 요청 하나의 네트워크 타임아웃(초). 긴 응답을 쓰는 모델(sol 등)만 길게 잡을 때 씁니다. 대시보드 에이전트 표의 "타임아웃(초)" 칸 |
| `MaxAttempts` | 제공사 값 → 2 (= 최대 1회 재시도) | 한 단계의 최대 시도 횟수 (첫 시도 포함, 1~5). 아래 [재시도](#재시도) |
| `Enabled` | 켬 | `false`면 끕니다 |
| `MaxTurns` | 40 | `claude-cli` 전용. 도구 턴 상한 (`--max-turns`) |
| `MaxBudgetUsd` | 없음 | `claude-cli` 전용. 실행 1회 비용 상한 USD (`--max-budget-usd`). CLI 추정 기준이라 한 응답만큼 넘을 수 있습니다 |
| `Vision` | `true` | 이미지 입력을 받는 모델인지. `false`인 에이전트가 낀 모드에 `images`가 오면 실행 전에 거절합니다 |
| `StructuredOutput` | 제공사 값 → `Prompt` | 아래 [구조화 출력](#구조화-출력-structuredoutput) |
| `ReasoningEffort` | 보내지 않음 | 추론 강도. 아래 [추론 강도](#추론-강도-reasoningeffort) |

### 프롬프트 캐시 (`PromptCache`)

OpenAI Chat Completions 전용입니다. 다른 제공사에는 넣지 않습니다 (모르는 필드 오류가 날 수 있습니다).

| 값 | 동작 |
|---|---|
| `ProviderDefault` (생략) | 손대지 않습니다. OpenAI는 입력 전부를 캐시에 쓰고, 쓰기분을 입력의 1.25배로 청구합니다 |
| `Off` | 쓰지 않습니다. 읽기 할인도 없습니다 |
| `Context` | 첨부 자료가 약 3,000자 이상이면 그 끝에만 저장 지점을 둡니다. 짧으면 `Off`와 같습니다 |

캐시는 30분 보관됩니다.

- 같은 첨부로 30분 안에 다시 물으면 첨부 부분이 0.05배(sol)로 읽힙니다.
- 30분이 지나 다시 쓰면 그 1회는 1.25배입니다.

### 구조화 출력 (`StructuredOutput`)

역할에 `Output`이 있을 때 구조화 출력을 어떻게 요청할지 정합니다. 순서는 **에이전트 → 제공사 → `Prompt`** 입니다.

| 값 | 동작 |
|---|---|
| `Prompt` | 시스템 프롬프트에만 JSON 형식을 적습니다 |
| `Schema` | 거기에 `response_format: json_schema`(strict)까지 보내 강제합니다 |

- 모델이 `Schema`를 지원하지 않으면 400 → 그 단계가 실패합니다. 자동으로 `Prompt`로 다시 보내지 않으니, 이 값을 `Prompt`로 바꿉니다.
- 기본 설정: 제공사 단위로는 `openai`만 `Schema`, 에이전트 단위로는 `gemini_high_argon` · `deepseek_main_flash`도 `Schema`입니다 ([modes.md](modes.md) "구조화 출력").

### 추론 강도 (`ReasoningEffort`)

값: `none` · `low` · `medium` · `high` · `xhigh` (대소문자 무시).

- **에이전트 전용**입니다. 같은 제공사라도 모델마다 받는 값이 달라 제공사 기본값은 없습니다.
- 비우면 보내지 않는다 = 제공사 · 모델 기본값.
- 목록에 없는 값이면 그 에이전트를 건너뜁니다.
- 나가는 필드: Chat Completions는 `reasoning_effort`, Responses(openai-web)는 `reasoning.effort`.

덮어쓰기:

- 실행마다 `run_mode`의 `reasoning`으로 덮어씁니다 (`"에이전트=강도"` · `"*=강도"`, 에이전트는 `agents` 교체 뒤 이름).
- 골든은 `--reasoning`으로 덮어씁니다.
- followUp은 원래 호출 값을 그대로 씁니다.

**주의**: 추론 토큰도 `MaxOutputTokens` 안에 들어갑니다. 강도를 올리면 상한에 닿아 본문 없이 끝날 수 있습니다. 원본 설정의 sol · argon · deepseek는 64,000입니다.

어디서 보나:

- 호출 기록 `ReasoningEffort`에 실제로 보낸 값이 남습니다.
- `list_modes` 에이전트 줄에 설정 값이 보입니다.
- 대시보드 에이전트 편집에서도 고칩니다.

제공사별 받는 값:

| 제공사 | 받는 값 |
|---|---|
| OpenAI gpt-6.1-sol | low~xhigh (none · minimal은 400, 기본 medium) |
| Gemini OpenAI 호환 | low~high (xhigh 매핑 없음, 3.x · 4는 끌 수 없음) |
| Fireworks | low~xhigh · none (모델별 기본값 문서 없음) |

**Gemini OpenAI 호환의 특이점**

- **강도를 비우면 추론을 거의 하지 않습니다.**
- 추론 토큰을 usage의 `total_tokens`에만 넣습니다. 그래서 서버는 Gemini 제공사(엔드포인트 generativelanguage.googleapis.com)에서만, 추론 칸이 없고 total > prompt + completion이면:
  - 그 차이를 `ReasoningTokens`로 기록하고
  - `OutputTokens`에 더합니다 (비용이 출력 단가로 잡힙니다).
- **`gemini_high_argon`은 ReasoningEffort가 비어 있어 호환 경로 기본값(추론 거의 없음)으로 동작합니다.** 이 에이전트를 쓰는 crash_main · preflight_* 모두 해당합니다 (review_high · perf_high는 DeepSeek을 씁니다).
  - 비워 둔 까닭: high를 명시하면 비용 약 6배 · 시간 약 10배입니다.
  - 에이전트 이름은 호출 기록(provenance)과 이어져 있어 그대로 둡니다.
  - appsettings 에이전트의 `Note` 칸에도 같은 설명이 있습니다. `Note`는 서버가 읽지 않는 메모 칸이고, 주석과 달리 대시보드 저장 때 지워지지 않습니다.

### 예산 · 첨부 · 이미지

| 키 | 기본값 | 의미 |
|---|---|---|
| `Budget.FreeProviders` | `local` | 대시보드 비용 집계 · 오늘 사용량에서 뺄 제공사 |
| `Budget.MaxInputChars` | 350,000 | `run_mode` 입력 최대 글자 수 (input + `context` 첨부 합계) |
| `Context.MaxRanges` | 30 | `context` 범위 개수 상한 |
| `Context.MaxFileBytes` | 2,000,000바이트 | 읽기 전에 확인하는 파일 크기 상한 |
| `Context.DenyPatterns` | (내장 목록) | 외부로 보내지 않을 파일 glob **추가분** |
| `Context.WriteDenyPatterns` | (내장 목록) | save_result가 쓰지 않을 파일 glob **추가분** (쓰기에만 적용) |
| `Context.ExtraRoots` | 비어 있음 | 프로젝트 루트 밖에서 `context`로 **읽을** 수 있는 추가 루트 (`이름 → 절대 경로`). [modes.md](modes.md) "추가 루트" |
| `Images.MaxCount` | 20 | `run_mode` `images` 장수 상한 |
| `Images.MaxBytes` | 2,000,000 | 장당 바이트 상한 |
| `Images.MaxDimension` | 1024 | 긴 변 px 상한 |

- `Budget.FreeProviders`: 설정 값은 기본값 뒤에 **덧붙습니다**. 그래서 `local`은 항상 빠집니다 (`[]`로 써도 마찬가지).
  - 예전 키 `DailyTokenLimit` · `DailyCostLimitUsd`(하루 토큰 · 비용 상한)는 없어졌습니다. 남아 있어도 무시합니다.
- `Budget.MaxInputChars`: Debate는 토론자가 매 턴 요청 전체를 받으므로 비용이 턴 수만큼 곱해집니다.
- `Context.DenyPatterns`의 내장 목록은 항상 적용됩니다:
  ```
  .env  .env.*  *.pem  *.key  *.p8  *.p12  *.pfx  *.keystore  *.jks
  *.mobileprovision  *.provisionprofile  credentials*  secrets*  .git/**  **/.git/**
  ```
- `Context.WriteDenyPatterns`: 내장 쓰기 금지 목록(실행 · 설정 파일, [modes.md](modes.md) save_result 절)과 `DenyPatterns`는 항상 적용됩니다.
- 이미지:
  - 서버는 이미지를 줄이지 않습니다. 상한을 넘으면 거절합니다. 축소는 `image_prep.py shrink`로 합니다.
  - 이미지는 `MaxInputChars`에 들어가지 않습니다. 토큰은 실제 사용량으로 비용에 잡힙니다.

### 응답 검사 · 기록 · 기타

| 키 | 기본값 | 의미 |
|---|---|---|
| `ResponseChecks.ExpectedLanguage` | `ko` (검사 켬) | 기대하는 응답 언어. 지금은 `ko`만 지원. `""`이면 끕니다. 아래 [응답 검사기](#응답-검사기) |
| `ResponseChecks.ForeignScriptRatio` | 0.02 (2%) | 코드 블록을 뺀 본문에서 일본어 가나 비율, 또는 한자 비율(중국어)이 이 값 이상이면 경고 |
| `Debug.DumpToolResults` | `false` | `true`면 `run_mode`가 반환하는 문자열을 그대로 `%LOCALAPPDATA%/HB_Orchestration/dumps/`에 남깁니다 |
| `Records.BlobFolder` | `calls.jsonl` 옆 `blobs/` | 원문 저장 폴더. 상대 경로면 `calls.jsonl` 폴더 기준. 아래 [Provenance](#호출-기록의-지침--설정-해시-provenance) |
| `Records.MaxReasonChars` | 500 | `run_mode`의 `reason` 글자 수 상한 (0 = 무제한) |
| `Records.MaxOwnReviewChars` | 30,000 | `run_mode`의 `ownReview` 글자 수 상한 (0 = 무제한) |
| `Records.StoreSettingsText` | `false` | 서버 설정(`appsettings.json`) 원문도 저장할지. `false`면 해시와 키 패턴 검사 결과만 |
| `WorkOrders.Folder` | (비움) | `/work-order`가 지시서를 남길 폴더 (프로젝트 루트 기준 상대 경로) |
| `Pricing.KrwPerUsd` | (비움) | 1달러당 원. 비우면 대시보드는 달러만 표시 |
| `Pricing.Models.<모델 ID>` | — | 모델 단가. 아래 [단가](#단가-pricing) |

- `Debug.DumpToolResults`: 조사용입니다. 결과가 Claude에게 가는 동안 바뀌는지 가를 때 쓰고, 끝나면 끕니다.
- `Records.BlobFolder`에 저장하는 원문: 시스템 프롬프트 · 지침 · modes.json · 설정 · 4,096바이트 이상 프롬프트.
- `Records.MaxReasonChars` · `MaxOwnReviewChars`: 넘으면 잘라 쓰지 않고 거절합니다 (실행 횟수 차감 없음).
- `Records.StoreSettingsText`: `true`여도 키 패턴이 보이면 저장하지 않습니다.
- `WorkOrders.Folder`:
  - 값이 있으면 `list_modes` 끝에 `지시서 폴더: …` 줄이 붙습니다.
  - 비우면 줄이 없고, 명령은 `docs/work-orders/`를 씁니다.
  - 절대 경로 · `..` · `@이름`은 `[주의]`로만 표시합니다.
  - **서버는 이 폴더에 쓰지 않습니다** (지휘자가 직접 씁니다).

### 단가 (`Pricing`)

`Pricing.Models.<모델 ID>`의 칸 (USD / 1M 토큰):

| 칸 | 비우면 | 뜻 |
|---|---|---|
| `InputPerMillion` | — | 입력 |
| `CachedInputPerMillion` | 입력과 같음 | 캐시 읽기 |
| `CacheWritePerMillion` | 입력과 같음 | 캐시 쓰기. OpenAI GPT-5.6 이후는 입력의 1.25배. 쓰기 토큰은 호출 기록 `CacheWriteTokens` |
| `OutputPerMillion` | — | 출력 |
| `WebSearchPerThousand` | — | 웹검색 단계를 쓰는 모델만. 1,000회당 USD |

- 키는 에이전트 이름이 아니라 **모델 ID**(기록에 남는 값)입니다.
- 지금 안 쓰는 예전 모델도 남겨 두면 예전 기록의 비용이 계속 계산됩니다.
- 단가는 **실효 단가**(할인 후 실제로 내는 값)로 둡니다.
  - 예: `gemini-4-argon`은 $2 / 캐시 $0.10 / $10 (11-30까지 소개 가격, 12-01부터 $4 / $20).
  - Gemini 콘솔 SKU 금액은 정가($4 / $20)로 집계되고, 50% 할인은 '절감'에서 상계됩니다. 그래서 청구 대조는 SKU 금액이 아니라 할인 후 금액이나 크레딧 사용분과 합니다.

**단가가 바뀌면** 위 칸(처음 단가)을 고치지 말고 `Changes[]`에 항목을 더합니다. 기록 시각 기준으로 그때 단가가 적용됩니다. 칸을 고치면 지난 호출 비용까지 새 단가로 다시 계산됩니다.

```json
"Changes": [
  { "From": "yyyy-MM-dd", "<바뀌는 칸>": …, "Note": "…" }
]
```

- `From`은 UTC 0시부터입니다. 바뀌는 칸만 넣습니다.
- 날짜 형식 오류 · 같은 날짜 중복 · 단가 칸 없음 · 음수는, 서버가 설정을 읽을 때 경고만 합니다.
- 대시보드 모드 화면은 처음 단가만 고칩니다. `Changes`는 파일에서 직접 고칩니다.

누가 쓰나: 서버는 비용 계산에 쓰지 않습니다 (설정을 읽을 때 `Changes` 문제만 경고). 대시보드 비용과 골든 실행 전 비용 추정이 씁니다.

## claude-cli 제공사 (`sub_*` 모드)

검토형 서브에이전트(self-reviewer 등)를 Claude Code 구독 대신 **Anthropic API 키**로 돌립니다.

- 서버가 `claude --bare --restricted -p`를 읽기 전용 에이전트로 띄웁니다.
- 에이전트가 작업 폴더의 파일을 통째로 직접 읽습니다. 지휘자가 고른 범위만 받는 외부 에이전트와 다릅니다.
- **Windows의 Claude Code 2.1.311은 `--tools`에 Grep · Glob을 줘도 Read만 줍니다.** 검색은 못 하고, 프롬프트의 파일 목록에서 골라 읽습니다.

모드 · 에이전트:

| 항목 | 값 |
|---|---|
| 모드 | `sub_self` · `sub_perf` · `sub_security` · `sub_direction` |
| 역할 | `SubSelf` · `SubPerf` · `SubSecurity` · `SubDirection` = 각 검토형 서브에이전트 정의 본문 (읽는 것만 작업 폴더 · Read 기준) |
| 에이전트 | `claude_cli_opus` 공용 = claude-opus-5-5 · effort high · MaxTurns 40 · MaxBudgetUsd 2 · 재시도 없음 (제공사도 MaxAttempts 1) |

**검토형 리뷰의 기본**입니다 — `/self-review` · `/cross-review` 2단계 · `perf_*` ownReview. 서브에이전트는 `/self-review sub …`로 고르거나 크레딧이 소진됐을 때 씁니다.

### 실행

```
claude --bare --restricted -p --no-session-persistence --output-format json
  --model <Model> --effort <ReasoningEffort> --max-turns <MaxTurns> [--max-budget-usd <MaxBudgetUsd>]
  --permission-mode dontAsk --tools Read,Glob,Grep
  --settings <폴더 밖 읽기 막음>
  --append-system-prompt-file <역할 + 출력 형식 + 공통 규칙 + 지침>
```

사용자 프롬프트는 stdin, 작업 폴더는 [아래](#작업-폴더).

플래그를 고른 까닭:

- `blockReadsOutsideWorkingDirectories`는 상대 · 절대 경로 모두 폴더 밖 읽기를 거절합니다.
- `--bare`만으로는 작업 폴더의 `.claude/settings.json`이 **적용됩니다**. 그래서 `--restricted`를 함께 씁니다 — 사용자 · 프로젝트 · 로컬 설정 파일을 무시하고, `--settings`만 적용하고, 파일 도구를 작업 폴더로 제한합니다.
- `--bare -p`는 세션 기록(소스 사본 포함)을 `~/.claude/projects`에 남깁니다. `--no-session-persistence`로 끕니다.

실행 파일 찾기:

- `ExecutablePath`(절대 경로만) 또는 PATH의 **절대 경로 항목에서만** `claude.exe`를 찾습니다.
  - 까닭: 상대 이름을 넘기면 Windows가 서버의 현재 폴더(프로젝트 루트)를 먼저 찾아, 거기 놓인 claude.exe가 키를 받을 수 있습니다.
- npm 설치본(claude.cmd)은 찾지 않습니다. 그런 PC는 `ExecutablePath`에 실행 파일을 넣습니다.

### 과금 · 키

- 자식 프로세스에만 `ANTHROPIC_API_KEY` = `ApiKeyEnv`(기본 `HB_REVIEWER_API_KEY`) 값을 줍니다.
- 자식 환경에서 지우는 것:
  - `ANTHROPIC_AUTH_TOKEN` · `CLAUDE_CODE_OAUTH_TOKEN` · 클라우드 제공사 플래그 · `ANTHROPIC_BASE_URL`
  - 키를 담은 원래 변수
  - 이름이 `_API_KEY` · `_ADMIN_KEY` · `_TOKEN` · `_SECRET`으로 끝나는 변수 (다른 제공사 키)
- 설정 · 실행 기록(record의 ToString)에 키 값은 찍히지 않습니다.
- `--bare`는 구독 로그인(OAuth)을 읽지 않습니다. 키가 없으면 구독으로 넘어가지 않고 실패합니다.
- `ApiKeyEnv`에 `ANTHROPIC_API_KEY`를 쓰면 에이전트를 건너뜁니다. 사용자 환경 변수로 두면 Claude Code 자체가 API 과금으로 바뀌기 때문입니다.

키는 각 프로젝트 `.mcp.json`의 `env`로 넘깁니다.

```json
"env": { "HB_REVIEWER_API_KEY": "${HB_REVIEWER_API_KEY:-}" }
```

환경 변수를 새로 넣었으면 Claude Code(백그라운드에 남은 claude 프로세스 포함)를 모두 끄고 다시 켜야 MCP 서버가 값을 받습니다. /mcp 재연결만으로는 옛 환경 그대로입니다.

### 작업 폴더

- 서버가 `<데이터 폴더>/review_in/<임의>/`에 `context`가 가리키는 파일을 **파일 전체**로, 상대 경로 그대로 복사합니다.
  - 경로 규칙 · 추가 루트 `@이름/` · 전송 금지 · 파일 크기 상한은 첨부와 같습니다.
  - 합계 4,000,000바이트 상한.
- 프롬프트의 첨부 절에는 본문 대신 두 목록만 들어갑니다:
  - **파일 목록** (경로 · SHA-256 앞 12자 — 기록에 남아 나중에 대조)
  - "이번 검토의 중심" (경로:줄)
- 본문을 보내지 않으므로 모드 입력 상한(MaxInputChars)은 input에만 적용합니다.
- context가 없으면 실행하지 않습니다.
- 한 모드에 다른 제공사 에이전트를 섞을 수 없습니다. images · followUp은 받지 않습니다.

**정의 색인** (`DefinitionIndex`): claude -p가 Read만 주므로, 파일 목록에 줄 수와 정의 위치를 붙입니다.

```
정의: class X 12 · Run() 40 · …
```

- 대상: C# · Python · Markdown · JSON 얕은 키 · PowerShell. 줄 단위 정규식이라 대략의 위치입니다.
- 파일당 80개 · 전체 2만 자 · 1MB 이하 UTF-8 파일만.

### 결과

- CLI JSON의 `result`가 응답입니다.
- 토큰: 입력 = 캐시 안 됨 + 캐시 쓰기 + 캐시 읽기. 캐시 읽기 · 쓰기는 따로 기록합니다.
- 단가 `claude-opus-5-5`: 입력 4 / 읽기 0.2 / 쓰기 5 / 출력 20.
- usageRaw에 CLI 원문을 남깁니다 (본문 제외: `total_cost_usd` · `num_turns` · `permission_denials` …).

실패 처리:

| 상황 | 처리 |
|---|---|
| `error_max_turns` · `error_max_budget_usd` · refusal | 재시도 없는 실패. 이때도 결과 JSON의 토큰 · usageRaw를 실패 기록에 남겨 비용에서 빠지지 않습니다 |
| "credit balance" 문구 | 상태 코드와 상관없이 Quota |
| 429 · 5xx | [재시도](#재시도) 규칙대로 |
| CLI가 입력을 읽기 전에 끝남 (인자 오류 · 판 변경) | 네트워크 오류가 아니라 CLI 출력으로 실패를 남깁니다 |
| 추론 강도 `none` | 거절 (Opus 5.5는 thinking을 끌 수 없음) |
| 결과 JSON에 `permission_denials`가 있음 | `[경고] claude-cli 도구 사용 N건이 거절됨` |
| 결과에 `result` · `usage`가 없음 | CLI 판이 바뀐 것으로 보고 실패 |

시간: 에이전트 · 제공사 `TimeoutSeconds`(없으면 15분)를 넘으면 프로세스 트리를 끝내고, 종료를 기다린 뒤(작업 폴더 잠김 방지) Timeout으로 남깁니다.

### 크레딧 소진 대체

- claude-cli 단계는 그 제공사의 최근 판정 가능한 기록(성공 또는 Quota)이 1시간 안의 Quota면, 호출 없이 `[실패] Quota (최근) …`를 돌려줍니다. 지휘자는 서브에이전트로 대신합니다.
- 1시간 뒤에는 한 번 시도합니다.
  - 까닭: 계속 막으면 성공 기록이 생기지 않아, 크레딧을 채워도 풀리지 않습니다.
  - 크레딧이 없을 때 실패 호출은 비용 0입니다. 충전 뒤 최대 1시간이면 풀립니다.
- 대시보드 첫 화면:
  - `API 리뷰(sub_*) 이번 달 N회 · $X` (claude-cli 제공사 기준, 단가 없는 기록은 건수로)
  - 마지막 판정 가능한 기록이 Quota면 `크레딧 소진 — 서브에이전트로 대체 중` (1시간이 지났으면 `다음 호출에서 다시 시도`)
- 절차는 `/self-review` 명령 파일 "API 리뷰".

### ownReview로 쓸 때

`/cross-review` 2단계 · `perf_*`에서:

- 응답 본문(`## 지적`부터)만 넣습니다.
- reason 태그는 `[ownReview:sub_self]` · `[ownReview:sub_perf]` · `[ownReview:sub_self+sub_security]`.
- H4 훅이 읽은 파일을 작업 폴더 기준 상대 경로로 검사합니다.

## codex-cli 제공사 (`review_main`)

외부 리뷰(`review_main`)를 OpenAI API 대신 **Codex CLI + ChatGPT 구독 로그인**으로 돌립니다. 리뷰어가 저장소 사본에서 정의 · 호출부를 직접 찾아 읽습니다 (지휘자가 고른 첨부에 갇히지 않게).

### 실행

```
codex exec --json --ephemeral --ignore-user-config
  -c windows.sandbox="unelevated" -c web_search="disabled" -c forced_login_method="chatgpt"
  -s read-only --skip-git-repo-check -C <작업 폴더> -m <Model>
  --disable apps · plugins · remote_plugin · browser_use · browser_use_external · computer_use · in_app_browser · multi_agent · image_generation
  -c model_reasoning_effort=… -c developer_instructions=<시스템 프롬프트>
  [--output-schema <구조화 출력 스키마>] -
```

- 사용자 프롬프트는 stdin.
- 한 턴만 돕니다. followUp · 토론은 거절합니다.

플래그를 고른 까닭:

- `--ignore-user-config`: `~/.codex/config.toml`의 MCP 서버 · 훅이 붙지 않게 합니다. 그래서 샌드박스도 인자로 고릅니다 (MXC는 git이 작업 폴더로 chdir 하지 못합니다).
- `--ephemeral`: `~/.codex/sessions`에 명령 출력(소스 사본)이 든 기록을 남기지 않습니다.
- 시스템 프롬프트는 명령줄로 가서, 2.4만 자를 넘으면 실패합니다.

실행 파일 찾는 순서 (`codex.cmd`는 쓰지 않습니다):

1. `ExecutablePath` (절대 경로)
2. npm 전역 설치의 네이티브 `codex.exe` (`%APPDATA%\npm\node_modules\@openai\codex\…\vendor\<triple>\bin`)
3. PATH의 절대 경로 항목

### 과금 · 키

- `ApiKeyEnv`를 두면 설정 오류입니다.
- `forced_login_method="chatgpt"`라서, 저장된 인증이 API 키여도 쓰지 않습니다.
- 인증은 `CODEX_HOME`(`~/.codex/auth.json`).
- 자식 환경은 **허용 목록**(`CodexCliChatClient.KeptEnvironment` — PATH · SystemRoot · TEMP · USERPROFILE · APPDATA · CODEX_HOME · 프록시 등)만 남기고 모두 지웁니다.
  - 까닭: API 과금으로 넘어가거나, 셸 명령(`Get-ChildItem Env:`)으로 비밀이 모델 문맥에 들어가지 않게.
  - git ls-files · `--version`도 같은 환경에서 돕니다.
- 제공사 `codex`는 `Budget.FreeProviders`에 있어 비용 0이고 사용량 집계에서 빠집니다. 대신 구독 한도(5시간 · 주간)를 씁니다. `exec --json`에는 한도 %가 없어 기록하지 않습니다.

### 작업 폴더 (`ReviewWorkFolder` Codex 모양)

**루트가 git이면** `git ls-files`(intent-to-add 포함)의 파일을 작업 트리 상태로 복사합니다.

- 빼는 것:
  - 비추적 파일 · 금지 패턴
  - Codex 지시 · 설정 자리: `AGENTS.md` · `AGENTS.override.md` · `.codex/` · `.agents/` (context로 넘겨도 뺍니다)
  - 링크 · 이진 · `MaxFileBytes` 초과
- 구현 메모: 금지 패턴은 한 번만 컴파일하고, 링크는 폴더마다 한 번 봅니다. git은 `core.fsmonitor=false`. 사본 만들기는 `Task.Run`에서 합니다.
- context 파일은 그 위에 덮어 복사합니다 (비추적 diff 포함).

**2만 파일 · 64MB를 넘거나 git이 아니면**(골든 사례 폴더 등) context 파일만 복사합니다.

**작업 폴더가 없는 호출**(골든 고정 프롬프트)은 빈 임시 폴더에서 돕니다.

프롬프트에 들어가는 것:

- context의 `.diff` · `.patch` **본문**
- 나머지 context의 "중심 (경로:줄)" 목록
- 정의 색인은 넣지 않습니다 (rg로 찾습니다).

**주의**: 읽기 전용 샌드박스는 쓰기만 막습니다 — 디스크 전체를 읽을 수 있습니다. 셸 명령의 네트워크는 막힙니다.

작업 폴더의 `.codex/config.toml` · `.agents/skills`는 Codex 0.165.0에서는 적용되지 않지만, 사본에서도 뺍니다.

### 결과

- 마지막 `agent_message`가 응답입니다.
- `turn.completed` usage → 토큰 (입력 = 캐시 포함, 추론 따로).
- usageRaw = usage · thread_id · 명령 수 · 명령 목록(명령 · 종료 코드, 60개 · 300자까지, 출력 전문 없음) · `codex --version`.
- `turn.failed` · 답 없음 · usage 없음은 실패입니다.

실패 문구 분류:

| 문구 | 분류 |
|---|---|
| usage limit · usage_limit · credits_depleted | Quota |
| 429 · rate limit | RateLimit |
| not supported · invalid · 인자 오류 · 로그인 | Client |

프로세스:

- Job 객체로 자손(셸 명령)까지 묶습니다.
- 부모가 끝나고 3초 뒤 남은 자손을 끝냅니다 (파이프를 물고 남으면 읽기가 막힙니다).
- 시간 상한(`TimeoutSeconds`, 없으면 15분) · 취소면 모두 끝냅니다.

### 한도 소진 대체 (`StepDefinition.FallbackAgent`)

`review_main` 단계는 `codex_cli_sol` + 대체 `gpt_main_sol`입니다.

- Codex가 `[실패] Quota`면 같은 입력으로 대체 에이전트에 한 번 다시 보냅니다. 이때 context는 평소처럼 첨부 본문으로 갑니다.
- 최근 1시간 안에 Quota 기록이 있으면 Codex를 부르지 않고 바로 대체합니다.
- 결과 끝에 이유 줄 · 에이전트 교체 줄이 붙습니다. 실패한 호출도 기록에 남습니다.
- `agents`로 바꾼 실행에는 대체하지 않습니다.

## 재시도

SDK 자동 재시도(기본 3회, 기록에 안 남음)는 끄고 서버가 직접 재시도합니다.

| 오류 | 재시도 | 기록의 `ErrorKind` |
|---|---|---|
| 네트워크 타임아웃, 408 | O | `Timeout` |
| 429 요청 과다 | O (`Retry-After` · `retry-after-ms`를 따름, **30초를 넘으면 기다리지 않고 실패**) | `RateLimit` |
| 5xx | O | `Server` |
| 연결 실패 (응답 없음) | O | `Network` |
| 그 외 4xx (400 · 401 · 402 크레딧 부족 · 404 …) | X — 다시 보내도 같습니다 | `Client` |
| 사용자 취소 | X — 시도 기록을 남기고 중단 | `Canceled` |
| 프로세스 강제 종료 (작업 중지 · 세션 종료) | X | `Lost` (다음 정리 때, 아래 [끊긴 호출 비용 추정](#끊긴-호출-비용-추정)) |
| 빈 응답 | X — 원인이 출력 상한이라 같습니다 | — |

- 서버가 대기 시간을 알려 주지 않으면 2초 → 5초를 기다린 뒤 다시 보냅니다.
- **시도마다 calls.jsonl에 한 줄**씩 남습니다 (`Attempt` 1부터, 같은 `RunId` · `Step`). 대시보드 목록에 `시도 n/N` 배지가 붙습니다.
- `run_mode` 결과에는 `(재시도 1회 후 성공 · 1회차 Timeout)`처럼 한 줄로 알립니다.
- **한 단계의 최대 대기 ≈ 타임아웃 × 시도 횟수** (+ 시도 사이 대기). 예: 타임아웃 300초, MaxAttempts 2 → 최대 약 10분.

## 끊긴 호출 비용 추정

요청이 나간 뒤 끊긴 호출(`Timeout` · `Network` · `Canceled` · `Lost`)은 청구되지만, usage를 받지 못해 토큰이 비어 있습니다. 대시보드가 기록을 읽을 때 추정 사용량을 채웁니다 (`UsageEstimator`). 기록 파일은 그대로 둡니다.

**같은 `RunId` · `Step` · 에이전트에 성공한 시도가 있으면** 그 입력을 씁니다 (캐시 적중 0).

| 실패 종류 | 추정 |
|---|---|
| `Timeout` · `Network` | 입력 + 출력. 서버가 끝까지 생성해 청구했다고 봅니다 |
| `Canceled` · `Lost` | 입력만. 연결을 닫아 생성이 멈췄다고 봅니다 |

**성공한 시도가 없으면** 입력만 어림합니다: (프롬프트 + 지침 UTF-8 바이트) × 0.3.

- `Timeout` · `Network`은 "출력 미상"으로 표시합니다 (실제 청구는 더 클 수 있습니다).
- 웹검색 호출은 검색 결과가 입력에 붙으므로 어림이 하한이 됩니다.
- followUp · 이미지 호출의 바이트 어림은 "하한"으로 표시합니다.

**넣지 않는 것**: HTTP 상태로 거절된 실패(4xx · 5xx · `Quota` · `RateLimit`, 그리고 `Timeout`으로 분류되는 HTTP 408)는 청구되지 않는 것으로 봅니다.

- `Timeout` · `Network`에는 요청을 보내기 전 실패(연결 거부 · DNS)도 섞입니다. 기록으로는 구분할 수 없어 툴팁에 적습니다.

대시보드 표시:

- 추정 기록의 토큰 · 비용 앞에 `≈`
- 합계 옆에 "추정 $x 포함 (N건, 출력 미상 M건)"
- 청구 대조의 계산값 아래에 추정분
- 호출 상세에 근거

스트리밍(끊긴 시점까지 받은 출력을 세고, 끊으면 생성도 멈춤)은 쓰지 않습니다.

### 호출 중 표시 (`inflight/`)

프로세스가 응답 전에 죽은 호출을 `Lost`로 남기는 장치입니다.

- 요청을 보내기 직전에 `calls.jsonl` 옆 `inflight/<기록 Id>.json`(PID · 프로세스 시작 시각 · 기본 기록)을 쓰고, 시도 기록을 남긴 뒤 지웁니다.
- 정리 시점: 서버 · 골든의 매 단계 시작, 그리고 대시보드가 기록을 읽을 때. 남긴 프로세스가 끝난 표시를 `Lost` 기록으로 옮깁니다:
  ```
  [끊김] 프로세스가 응답 전에 종료됨 …
  ```
- 동시 정리: 여러 프로세스가 같이 정리해도, 표시 파일 이름을 먼저 바꾼 쪽만 옮깁니다 (`.claimed-<PID>-<시작 Ticks>-…`).
  - 차지한 채 죽은 파일은 되돌려 다시 정리합니다.
  - 주인이 죽은 쓰다 만 임시 파일(`.tmp-<PID>-<시작 Ticks>`)은 지웁니다.
- 기록 저장에 실패하면 표시를 남겨 둡니다.
- 기록 직후 죽어 같은 Id가 둘이 되면, 읽을 때 하나만 둡니다.
- 대시보드는 5초에 한 번 정리하고, 목록은 시각순으로 정렬합니다 (늦게 붙은 Lost 때문).
- 서버의 `ReadSince`는 Lost 줄을 멈출 기준으로 쓰지 않습니다.

## 응답 검사기

경고는 실패가 아닙니다 (빈 응답만 실패). 응답은 그대로 전달하고, `run_mode` 결과의 해당 단계 아래에 한 줄씩 붙입니다.

```
[경고] 응답 언어 이탈 감지 (가나 비율 10.7%, 308자)
[경고] 응답 언어 이탈 감지 (한자 비율 65.5%, 610자)
```

| 검사 | 처리 |
|---|---|
| 빈 응답 | **실패** `[빈 응답]`. 종료 이유가 길이 제한이면 MaxOutputTokens 안내 |
| 출력 상한에서 잘림 | 경고. 응답의 종료 이유가 길이 제한(`length`)일 때 |
| 언어 이탈 | 경고 (한 줄: 가나가 넘으면 가나, 아니면 한자) |
| 외국어 소량 섞임 | 경고 `[경고] 외국어 소량 섞임 (가나 63자 · 한자 26곳, 예: "…")` |
| 영어 문장 섞임 | 경고 `[경고] 영어 문장 섞임 (n줄, 예: "…")` |
| 출력 형식 절 빠짐 | 경고 `[경고] 출력 형식 절 빠짐 (3., 4. / 전체 7절)` |

검사마다 기준:

**언어 이탈**

- 본문 = 코드 블록(```` ``` ```` · `~~~`)과 인라인 코드를 뺀 것.
- 가나 비율 ≥ `ForeignScriptRatio`이고 가나 5자 이상이면 걸립니다.
- 가나가 기준 밑이면 한자(CJK 통합 · 확장 A · 호환)도 같은 기준으로 봅니다 (중국어).

**외국어 소량 섞임** — 언어 이탈 경고가 없을 때만 봅니다.

- 같은 본문에서 큰따옴표류(`"…"` · `“…”` · `「…」` · `『…』`) 안을 뺍니다.
- 걸리는 것:
  - 가나 2자 이상
  - 한글 · 영문자 · 가나에 바로 붙은 한자 덩어리 (`디렉터리를指定的하면` · `실제既存`)
- 보지 않는 것:
  - 괄호 안이 한자뿐인 한자 병기 (`주(州)` · `정적(靜的)`)
  - 띄어 쓴 한자 한 글자

**영어 문장 섞임**

- 같은 본문에서, 한글이 없고 영어 낱말 5개 이상인 줄이 2줄 이상이면 걸립니다.
- 낱말 = 첫 글자만 대문자일 수 있는 소문자 영어 낱말.
  - CamelCase · 대문자 약어 · 경로 · 밑줄 · 숫자가 든 토큰은 식별자로 보고 세지 않습니다.
- 보지 않는 줄: `>` 인용 줄 · `|` 표 줄 · 해석에 실패한 JSON 원문.

**출력 형식 절 빠짐**

- 역할에 `OutputSections`(N)가 있고 종료 이유가 길이 제한이 아닐 때 봅니다.
- 코드 밖에서 1..N 절 제목이 모두 있는지 봅니다.
- 제목 꼴: `#` · `**` · `__` 뒤 `N.` · `N)` (함께 와도 됨).
  - 이 꼴이 한 줄이라도 있으면 그 꼴만 보고, `1.` 제목과 같은 단계만 셉니다.
  - 없으면 맨 앞부터 쓴 `N. 제목`을 봅니다 (첫 `—` · `:` 앞 제목 부분 40자 이하).
- 상한 전에 모델이 스스로 멈춘 응답(종료 이유 `stop`)을 잡습니다.

그 밖:

- 경고는 기록(`Warnings`)에도 남고, 대시보드 목록과 상세 화면에 `경고 n` 배지로 보입니다.
- 검사기 자체가 오류를 내면 경고 없이 넘어가고 서버 로그만 남깁니다.
- `ExpectedLanguage`는 "비우면 기존 동작" 원칙의 **예외**로, 키가 없어도 검사가 켜집니다. 경고는 결과를 바꾸지 않고, 사람이 대시보드를 보기 전에는 모르는 이탈을 기본으로 잡기 위해서입니다. 끄려면 `""`.

## 호출 기록의 지침 · 설정 해시 (`Provenance`)

호출 기록만으로 "이 에이전트가 어떤 지침과 설정으로 실행됐나"를 확정하기 위해, 모든 기록 줄에 `provenance`를 남깁니다 (SHA-256 소문자 hex).

| 필드 | 무엇을 해시하나 | 원문 저장 |
|---|---|---|
| `instructionsSha256` · `instructionsBytes` | 에이전트에 넘긴 시스템 프롬프트 문자열 그대로의 UTF-8(BOM 없음) 바이트. 역할 + 공통 규칙 + 지침 (+ Debate 토론자는 토론 규칙) | O |
| `guidelines` | 붙였으면 `attached: true` + 지침 파일 경로 · 원본 바이트 해시(`sha256sum`과 같음). 안 붙였으면 `attached: false` + `reason` (`역할 UseGuidelines=false` / `지침 파일 없음`) | O |
| `modes` | 실행 시점 modes.json 원본 바이트 | O |
| `settings` | 적용 중인 `appsettings.json`을 읽을 때 함께 읽은 원본 바이트 + 키 패턴 검사(`secretScan`: `통과` / `의심 n건`) | **X** (기본, `Records.StoreSettingsText`) |

표 밖의 필드:

- `append`: 모델별 덧붙임 파일 경로 · 해시 ([modes.md](modes.md) "모델별 덧붙임")
- `blobFolder`: 원문을 저장한 폴더

원문 저장:

- 원문은 `blobs/<sha256>.txt`에 **해시별로 한 번만** 저장합니다. 같은 지침이면 새 파일이 생기지 않습니다.
- 여러 서버 프로세스가 동시에 써도 내용이 같아 안전합니다 (임시 파일 → 덮어쓰지 않는 이동).
- 저장에 실패해도 호출은 그대로 진행합니다. 실패는 서버 로그와 기록의 `storeErrors`에 남습니다 (없으면 필드 생략).
- 해시는 저장 실패와 관계없이 남습니다.

해시 범위:

- 서버가 에이전트(MAF)에 넘긴 문자열까지입니다. HTTP 요청 본문(JSON 이스케이프)은 해시하지 않습니다.
- `settings`의 해시는 실행 시점에 파일을 다시 읽은 값이 아니라 **지금 적용 중인 설정의 바이트**입니다.
  - 다시 읽기에 실패해 이전 설정이 적용 중이면, 이전 바이트의 해시와 `reloadError`가 남습니다.
  - 환경 변수(`Orchestration__…`)로 덮어쓴 값은 잡히지 않습니다.

그 밖:

- 지침 · modes.json을 읽다가 실패하면(다른 프로그램이 잠금 등) 실행 횟수 차감 없이 `[리뷰 지침 읽기 실패]` / `[모드 설정 오류]`로 거절합니다.
- provenance 기능이 생기기 전의 기록에는 `provenance`가 없습니다 (대시보드 "기록 없음").

## 데이터 폴더 (`%LOCALAPPDATA%\HB_Orchestration\`)

서버 · 골든 · 대시보드가 같은 폴더를 씁니다.

- `HB_ORCH_LOG_PATH`(calls.jsonl 경로)를 주면 그 파일의 폴더가 기준이 되고, 아래 하위 폴더도 함께 옮겨집니다.
- 단 `config-backup/`은 배치가 `%LOCALAPPDATA%\HB_Orchestration\`에 고정해 둡니다.

| 경로 | 내용 |
|---|---|
| `calls.jsonl` | 호출 기록. 한 줄 = 시도 1회 (UTF-8, camelCase). 여러 서버 프로세스가 같이 덧붙입니다 |
| `blobs/<sha256>.txt` | 시스템 프롬프트 · 지침 · modes.json · (선택) 설정 원문, 그리고 **4,096바이트 이상인 프롬프트**. `Records.BlobFolder`로 위치를 바꿀 수 있습니다 |
| `inflight/<기록 Id>.json` | 호출 중 표시 ([위](#호출-중-표시-inflight)) |
| `review_in/<임의 이름>/` | `claude-cli` · `codex-cli` 단계의 작업 폴더 (claude는 context 파일 전체 복사, codex는 저장소 추적 파일 사본 + context). 실행이 끝나면 지우고, 남은 것은 하루 뒤 다음 실행이 지웁니다 |
| `verdicts.jsonl` | `record_verdicts` 판정. 같은 키는 마지막 줄이 이깁니다. `HB_ORCH_VERDICTS_PATH`로 따로 둘 수 있습니다 |
| `sessions/<pid>.json` | 서버 프로세스별 모드 실행 횟수 |
| `subagents/<agentId>_<호출 id>.json` | 검토형 서브에이전트가 지휘자에게 넘긴 결과 원문의 보관본 |
| `dumps/` | `Debug.DumpToolResults`가 켜졌을 때만 |
| `config-backup/<시각>/` | 배치 publish 전 설정 백업 (최근 20개, [setup.md](setup.md#publish와-설정-파일)) |

- 큰 프롬프트는 기록에 앞 1,000자 + `promptSha256` · `promptBytes`만 남습니다. 원문은 대시보드 호출 상세가 blob에서 읽습니다.
- `subagents/`: self · perf · security · direction-reviewer의 결과입니다 ([dashboard.md](dashboard.md) "서브에이전트 결과").
  - 한 번 쓰면 바꾸지 않습니다.
  - `_state/scanned.json` = 보관을 마친 검토형 기록 (경로 → 크기:수정 시각, 지워진 기록은 빠집니다).

예전 기록의 큰 프롬프트를 blobs로 옮기려면 서버 없이 한 번 돌립니다.

```powershell
dotnet <publish 폴더>\HB.Orchestration.Mcp.dll --compact-log [calls.jsonl 경로]
```

- 원래 파일은 `calls.jsonl.bak-<시각>-<PID>`로 남깁니다.
- 파일이 없으면 예외로 끝납니다.

| 종료 코드 | 뜻 |
|---|---|
| 0 | 성공 |
| 1 | blob 저장 실패가 있었거나, 압축 중 파일 앞부분이 바뀌어 아무것도 바꾸지 않음. 이때도 백업은 남습니다 |

## 재시작이 필요한 경우

| 바꾼 것 | 필요한 동작 |
|---|---|
| modes.json (모드 · 역할 · 에이전트 배치) | 없음 — 다음 호출부터 반영 |
| appsettings.json (에이전트 · 모델 · 제공사 · 단가) | 없음 — 다음 호출부터 반영 |
| 서버 코드 (publish) | 배치가 MCP 서버만 종료 → Claude Code에서 `/mcp`로 재연결 ([setup.md](setup.md#publish와-실행-중인-claude-code)). Claude Code · 대화는 그대로 |
| **API 키 환경 변수** | **PC 재시작** (직접). 환경 변수는 프로그램이 시작될 때 물려받는 값이라, Unity Hub · Toolbox 같은 트레이 프로그램까지 새로 떠야 확실합니다 |
