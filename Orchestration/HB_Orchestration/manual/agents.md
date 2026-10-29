# 에이전트 설정 (appsettings.json)

[← README](../README.md)

## 에이전트 — `appsettings.json` ("누가")

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

제공사는 `openai` · `openai-web` · `gemini` · `fireworks` · `local` · `anthropic-cli` · `codex` 일곱이다 (Type은 `openai-web`만 `openai-responses`, `anthropic-cli`는 `claude-cli`, `codex`는 `codex-cli`, 나머지는 `openai-compatible`). 실제 쓰는 값은 publish 폴더의 appsettings.json이고, `list_modes` 마지막의 `에이전트:` 줄에 보인다.

- `MaxOutputTokens`: 응답 상한. **추론 모델은 생각 토큰도 포함**되므로 낮게 잡으면 답변 없이 끝난다 (서버가 `[빈 응답]` 실패로 기록). 폭주 방지용으로 넉넉하게 (1만 이상 권장).
- 키가 없거나 설정이 틀린 에이전트는 그것만 건너뛰고 서버는 정상 시작. `list_modes`에 `(사용불가)`로 표시.
- **저장하면 바로 반영된다.** 서버가 호출 때마다 파일 수정 시각을 보고, 바뀌었으면 설정·에이전트를 다시 읽는다 (재시작 불필요). JSON이 깨졌으면 이전 설정으로 계속 동작하고 `list_modes`·`server_info`에 경고가 뜬다.
- 대시보드 **모드** 화면에서 추가·수정·삭제할 수 있고, **이름을 바꾸면 그 이름을 쓰는 modes.json 단계도 같이 바뀐다** ([대시보드](dashboard.md)).
- appsettings.json도 주석 없는 순수 JSON으로 둔다. 필드 설명은 아래 표.

### appsettings.json 필드 (`Orchestration` 섹션)
| 키 | 의미 |
|---|---|
| `ModesPath` | modes.json 위치를 직접 지정 (비우면 [모드와 run_mode 도구 (modes.json)](modes.md)의 찾는 순서) |
| `GuidelinesPath` | 리뷰 지침 파일 (비우면 `REVIEW_GUIDELINES_PATH` 환경 변수 → Unity 프로젝트 루트의 `REVIEW_GUIDELINES.md`). 직접 지정한 경로(이 설정 또는 환경 변수)에 파일이 없으면 다음 후보로 넘어가지 않고 run_mode가 `[리뷰 지침 경로 없음]`으로 거절한다 (2026-10-22, modes.json 지정 경로와 같은 규칙) |
| `PromptsPath` | 모델별 덧붙임(외부 에이전트 실수 목록) 폴더. 원본은 `project-files/prompts`, 상대 경로는 서버 폴더 기준. 비우면 modes.json 옆 `prompts/`. 지정했는데 폴더가 없으면 `[모델별 덧붙임 폴더 오류]`으로 거절한다 ("모델별 덧붙임") |
| `Providers.<이름>.Type` | `openai-compatible`(Chat Completions — OpenAI · Gemini · Fireworks · Ollama · LM Studio) · `openai-responses`(OpenAI Responses API, 웹검색 단계용) · `claude-cli`(아래 "claude-cli 제공사") · `codex-cli`(아래 "codex-cli 제공사") |
| `Providers.<이름>.ExecutablePath` | `claude-cli` · `codex-cli` 전용: 실행 파일 절대 경로. 비우면 `claude`는 PATH, `codex`는 npm 전역 설치 → PATH |
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
| `Agents[].ReasoningEffort` | 추론 강도 `none` · `low` · `medium` · `high` · `xhigh` (대소문자 무시). **에이전트 전용** (같은 제공사라도 모델마다 받는 값이 달라 제공사 기본값은 없음). 비우면 보내지 않음 = 제공사·모델 기본값. 다른 값이면 그 에이전트를 건너뛴다. Chat Completions는 `reasoning_effort`, Responses(openai-web)는 `reasoning.effort`로 나간다. 실행마다 `run_mode`의 `reasoning`(`"에이전트=강도"` · `"*=강도"`, 에이전트는 `agents` 교체 뒤 이름) · 골든 `--reasoning`으로 덮어쓴다. followUp은 원래 호출 값을 그대로 쓴다. 추론 토큰도 `MaxOutputTokens` 안에 들어가므로 강도를 올리면 상한에 닿아 본문 없이 끝날 수 있다 (2026-10-20 sol·argon·deepseek 64,000으로 올림). 호출 기록 `ReasoningEffort`에 실제로 보낸 값이 남고, `list_modes` 에이전트 줄에 설정 값이 보인다. 대시보드 에이전트 편집에서도 고친다. 제공사별 받는 값: OpenAI gpt-6.1-sol은 low~xhigh(none·minimal 400, 기본 medium) · Gemini OpenAI 호환은 low~high(xhigh 매핑 없음, 3.x·4는 끌 수 없음) · Fireworks는 low~xhigh·none (모델별 기본값 문서 없음). **Gemini OpenAI 호환은 강도를 비우면 추론을 거의 하지 않고**(2026-10-21 실측: argon 기본 21초 · total = prompt + completion, high 명시 201초), 추론 토큰을 usage의 `total_tokens`에만 넣는다 → 서버가 Gemini 제공사(엔드포인트 generativelanguage.googleapis.com)에서만 추론 칸이 없고 total > prompt + completion이면 그 차이를 `ReasoningTokens`로 기록하고 `OutputTokens`에 더한다(비용이 출력 단가로 잡힘). **`gemini_high_argon`은 ReasoningEffort가 비어 있어 호환 경로 기본값(추론 거의 없음)으로 동작한다** — 이 에이전트를 쓰는 crash_main · preflight_* 모두 해당 (review_high · perf_high는 2026-10-22부터 DeepSeek). high를 명시하면 비용 약 6배·시간 약 10배인데 골든 rec-4942 찾음은 1→2/11뿐이라 바꾸지 않았다 (2026-10-21 사용자 결정, 에이전트 이름은 provenance와 연결돼 그대로). appsettings 에이전트의 `Note` 칸에도 같은 설명 (서버가 읽지 않는 메모 칸 — 주석과 달리 대시보드 저장 때 지워지지 않음). |
| `ResponseChecks.ExpectedLanguage` | 기대하는 응답 언어. 지금은 `ko`만 지원. **키가 없으면 `ko`(검사 켬), `""`이면 끔** (아래 "응답 검사기") |
| `ResponseChecks.ForeignScriptRatio` | 코드 블록을 뺀 본문에서 일본어 가나 비율, 또는 한자 비율(중국어)이 이 값 이상이면 경고 (기본 0.02 = 2%) |
| `Debug.DumpToolResults` | `true`면 `run_mode`가 반환하는 문자열을 그대로 `%LOCALAPPDATA%/HB_Orchestration/dumps/`에 남긴다 (조사용, 기본 `false`). 결과가 Claude에게 가는 동안 바뀌는지 가를 때 쓰고, 끝나면 끈다 |
| `Records.BlobFolder` | 원문 저장 폴더 (시스템 프롬프트 · 지침 · modes.json · 설정 · 4,096바이트 이상 프롬프트). 비우면 `calls.jsonl` 옆 `blobs/`, 상대 경로면 `calls.jsonl` 폴더 기준 (아래 "호출 기록의 지침·설정 해시") |
| `Records.MaxReasonChars` · `Records.MaxOwnReviewChars` | `run_mode`의 `reason` · `ownReview` 글자 수 상한 (기본 500 · 30,000, 0 = 무제한). 넘으면 잘라 쓰지 않고 거절 (실행 횟수 차감 없음) |
| `Records.StoreSettingsText` | 서버 설정(`appsettings.json`) 원문도 저장할지 (기본 `false` = 해시와 키 패턴 검사 결과만). `true`여도 키 패턴이 보이면 저장하지 않음 |
| `WorkOrders.Folder` | `/work-order`가 지시서를 남길 폴더 (프로젝트 루트 기준 상대 경로). 값이 있으면 `list_modes` 끝에 `지시서 폴더: …` 줄이 붙고, 비우면 줄이 없고 명령은 `docs/work-orders/`를 쓴다. 절대 경로·`..`·`@이름`은 `[주의]`로만 표시. **서버는 이 폴더에 쓰지 않는다** (지휘자가 직접 씀) |
| `Pricing.KrwPerUsd` | 1달러당 원. 비우면 대시보드는 달러만 표시 |
| `Pricing.Models.<모델 ID>` | `InputPerMillion`, `CachedInputPerMillion`(비우면 입력과 같음), `CacheWritePerMillion`(캐시 쓰기, 비우면 입력과 같음 — OpenAI GPT-5.6 이후는 입력의 1.25배, 쓰기 토큰은 호출 기록 `CacheWriteTokens`), `OutputPerMillion` — USD / 1M 토큰. 웹검색 단계를 쓰는 모델에는 `WebSearchPerThousand`(1,000회당 USD)도 넣는다. 키는 에이전트 이름이 아니라 **모델 ID**(기록에 남는 값). 지금 안 쓰는 예전 모델도 남겨 두면 예전 기록의 비용이 계속 계산됨. 위 칸은 **처음 단가**이고, 단가가 바뀌면 칸을 고치지 말고 `Changes[]`에 `{ "From": "yyyy-MM-dd"(UTC 0시부터), 바뀌는 칸만, "Note" }`을 더한다 — 기록 시각 기준으로 그때 단가가 적용된다 (고치면 지난 호출 비용까지 새 단가로 다시 계산됨). 날짜 형식 오류 · 같은 날짜 중복 · 단가 칸 없음 · 음수는 서버가 설정을 읽을 때 경고만 한다. 대시보드 모드 화면은 처음 단가만 고치고 `Changes`는 파일에서 직접 고친다. 단가는 **실효 단가**(할인 후 실제로 내는 값)로 둔다 — 예: `gemini-4-argon`은 $2 / 캐시 $0.10 / $10(11-30까지 소개 가격, 12-01부터 $4/$20). Gemini 콘솔 SKU 금액은 정가($4/$20)로 집계되고 50% 할인이 '절감'에서 상계되므로, 청구 대조는 SKU 금액이 아니라 할인 후 금액이나 크레딧 사용분과 한다 (2026-10-21 크레딧 대조, 모델 항목의 `Note`). 서버는 비용 계산에 쓰지 않고(설정을 읽을 때 `Changes` 문제만 경고) 대시보드 비용 · 골든 실행 전 비용 추정이 쓴다 |

### claude-cli 제공사 (`sub_*` 모드)

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
- **정의 색인**: 이 PC의 claude -p는 Read만 주므로, 작업 폴더 절의 파일 목록에 줄 수와 `정의: class X 12 · Run() 40 · …`(C# · Python · Markdown · JSON 얕은 키 · PowerShell, 줄 단위 정규식 — 대략의 위치)를 붙인다. 파일당 80개 · 전체 2만 자 · 1MB 이하 UTF-8 파일만 (`DefinitionIndex`).

### codex-cli 제공사 (`review_main`)

외부 리뷰(`review_main`)를 OpenAI API 대신 **Codex CLI + ChatGPT 구독 로그인**으로 돌린다. 리뷰어가 저장소 사본에서 정의 · 호출부를 직접 찾아 읽는다 (지휘자가 고른 첨부에 갇히지 않게).
- 실행: `codex exec --json --ephemeral --ignore-user-config -c windows.sandbox="unelevated" -c web_search="disabled" -c forced_login_method="chatgpt" -s read-only --skip-git-repo-check -C <작업 폴더> -m <Model> --disable apps · plugins · remote_plugin · browser_use · browser_use_external · computer_use · in_app_browser · multi_agent · image_generation -c model_reasoning_effort=… -c developer_instructions=<시스템 프롬프트> [--output-schema <구조화 출력 스키마>] -`, 사용자 프롬프트는 stdin. 한 턴만 (followUp · 토론은 거절).
  - `--ignore-user-config`: `~/.codex/config.toml`의 MCP 서버 · 훅이 붙지 않게. 그래서 샌드박스도 인자로 고른다 (MXC는 git이 작업 폴더로 chdir 하지 못함). `--ephemeral`: `~/.codex/sessions`에 명령 출력(소스 사본)이 든 기록을 남기지 않는다. 시스템 프롬프트는 명령줄로 가서 2.4만 자를 넘으면 실패.
  - 실행 파일: `ExecutablePath`(절대 경로) → npm 전역 설치의 네이티브 `codex.exe`(`%APPDATA%\npm\node_modules\@openai\codex\…\vendor\<triple>\bin`) → PATH의 절대 경로 항목. `codex.cmd`는 쓰지 않는다.
- **과금 · 키**: `ApiKeyEnv`를 두면 설정 오류. `forced_login_method="chatgpt"`로 저장된 인증이 API 키여도 쓰지 않는다. 자식 환경은 **허용 목록**(`CodexCliChatClient.KeptEnvironment` — PATH · SystemRoot · TEMP · USERPROFILE · APPDATA · CODEX_HOME · 프록시 등)만 남기고 모두 지운다 — API 과금으로 넘어가거나 셸 명령(`Get-ChildItem Env:`)으로 비밀이 모델 문맥에 들어가지 않게 (git ls-files · `--version`도 같은 환경). 인증은 `CODEX_HOME`(`~/.codex/auth.json`). 제공사 `codex`는 `Budget.FreeProviders`에 있어 비용 0 · 사용량 집계에서 빠진다 — 대신 구독 한도(5시간 · 주간)를 쓴다 (`exec --json`에는 한도 %가 없어 기록하지 않음).
- **작업 폴더** (`ReviewWorkFolder` Codex 모양): 루트가 git이면 `git ls-files`(intent-to-add 포함)의 파일을 작업 트리 상태로 복사 — 비추적 파일 · 금지 패턴 · Codex 지시 · 설정 자리(`AGENTS.md` · `AGENTS.override.md` · `.codex/` · `.agents/` — context로 넘겨도 뺀다) · 링크 · 이진 · `MaxFileBytes` 초과는 뺀다. 파일 검사는 금지 패턴을 한 번만 컴파일하고 링크는 폴더마다 한 번 본다, git은 `core.fsmonitor=false`. 사본 만들기는 `Task.Run`에서. 2만 파일 · 64MB를 넘거나 git이 아니면(골든 사례 폴더 등) context 파일만. context 파일은 위에 덮어 복사(비추적 diff 포함). 프롬프트에는 context의 `.diff` · `.patch` **본문**과 나머지 context의 "중심 (경로:줄)" 목록 — 정의 색인은 넣지 않는다(rg로 찾음). 작업 폴더가 없는 호출(골든 고정 프롬프트)은 빈 임시 폴더에서 돈다. **주의**: 읽기 전용 샌드박스는 쓰기만 막는다 — 디스크 전체를 읽을 수 있다 (셸 명령의 네트워크는 막힘 실측, 2026-10-29). 작업 폴더의 `.codex/config.toml` · `.agents/skills`는 0.165.0에서 적용되지 않음을 실측했지만 사본에서도 뺀다.
- **결과**: 마지막 `agent_message`가 응답, `turn.completed` usage → 토큰(입력 = 캐시 포함, 추론 따로). usageRaw = usage · thread_id · 명령 수 · 명령 목록(명령 · 종료 코드, 60개 · 300자까지, 출력 전문 없음) · `codex --version`. `turn.failed` · 답 없음 · usage 없음은 실패. 문구로 분류: usage limit · usage_limit · credits_depleted → Quota, 429 · rate limit → RateLimit, not supported · invalid · 인자 오류 · 로그인 → Client.
- 프로세스: Job 객체로 자손(셸 명령)까지 묶고, 부모가 끝나고 3초 뒤 남은 자손을 끝낸다 (파이프를 물고 남으면 읽기가 막힘). 시간 상한(`TimeoutSeconds`, 없으면 15분) · 취소면 모두 끝낸다.
- **한도 소진 대체** (`StepDefinition.FallbackAgent`): `review_main` 단계는 `codex_cli_sol` + 대체 `gpt_main_sol`. Codex가 `[실패] Quota`면 같은 입력으로 대체 에이전트에 한 번 다시(이때 context는 지금처럼 첨부 본문), 최근 1시간 안 Quota 기록이면 Codex를 부르지 않고 바로 대체. 결과 끝에 이유 줄 · 에이전트 교체 줄이 붙고 실패한 호출도 기록에 남는다. `agents`로 바꾼 실행에는 대체하지 않는다.
- 2026-10-29 골든 `rec-ffd7-configdrift`: Codex 37초 · 찾음 2/5 (API sol 기준점 3/5, 1회씩).

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

요청이 나간 뒤 끊긴 호출(`Timeout` · `Network` · `Canceled` · `Lost`)은 청구되지만 usage를 받지 못해 토큰이 비어 있다. 대시보드가 기록을 읽을 때 추정 사용량을 채운다 (`UsageEstimator`, 기록 파일은 그대로).

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
| 외국어 소량 섞임 | 언어 이탈 경고가 없을 때, 같은 본문에서 큰따옴표류(`"…"` · `“…”` · `「…」` · `『…』`) 안을 빼고 가나 2자 이상, 또는 한글 · 영문자 · 가나에 바로 붙은 한자 덩어리(`디렉터리를指定的하면` · `실제既存`). 괄호 안이 한자뿐인 한자 병기(`주(州)` · `정적(靜的)`)는 보지 않는다. 띄어 쓴 한자 한 글자는 잡지 않는다. 기준은 지난 응답 836건으로 정함 — 걸린 것 11건 중 10건이 진짜 섞임(sol · argon · DeepSeek), 1건은 한자 예를 따옴표 없이 쓴 리뷰 (2026-10-28) | 경고 `[경고] 외국어 소량 섞임 (가나 63자 · 한자 26곳, 예: "…")` |
| 영어 문장 섞임 | 같은 본문에서 한글이 없고 영어 낱말 5개 이상인 줄이 2줄 이상. 낱말 = 첫 글자만 대문자일 수 있는 소문자 영어 낱말 — CamelCase · 대문자 약어 · 경로 · 밑줄 · 숫자가 든 토큰은 식별자로 보고 세지 않는다. `>` 인용 줄 · `|` 표 줄, 해석에 실패한 JSON 원문은 보지 않는다. 기준은 지난 응답 755건(크래시 역할 61건 포함)으로 정함 — 걸린 것은 sol의 작업 메모(5e81a2ed47) 한 건뿐 (2026-10-27) | 경고 `[경고] 영어 문장 섞임 (n줄, 예: "…")` |
| 출력 형식 절 빠짐 | 역할에 `OutputSections`(N)가 있고 종료 이유가 길이 제한이 아닐 때, 코드 밖에서 1..N 절 제목이 모두 있는지. 제목 꼴(`#` · `**` · `__` 뒤 `N.` · `N)`, 함께 와도 됨)이 한 줄이라도 있으면 그 꼴만 보고 [설치 · 빌드 · 실행](setup.md)과 같은 단계만 센다. 없으면 맨 앞부터 쓴 `N. 제목`(첫 `—` · `:` 앞 제목 부분 40자 이하)을 본다. 상한 전에 모델이 스스로 멈춘 응답(종료 이유 `stop`)을 잡는다. 지난 preflight 응답 36건에서 걸린 것은 5e81a2ed47 한 건뿐 | 경고 `[경고] 출력 형식 절 빠짐 (3., 4. / 전체 [대시보드](dashboard.md))` |

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
- 표 밖의 필드: `append`(모델별 덧붙임 파일 경로 · 해시, 아래 [모드와 run_mode 도구 (modes.json)](modes.md)) · `blobFolder`(원문을 저장한 폴더).

### 데이터 폴더 (`%LOCALAPPDATA%\HB_Orchestration\`)

서버 · 골든 · 대시보드가 같은 폴더를 쓴다. `HB_ORCH_LOG_PATH`(calls.jsonl 경로)를 주면 그 파일의 폴더가 기준이 되고, 아래 하위 폴더도 함께 옮겨진다 — 단 `config-backup/`은 배치가 `%LOCALAPPDATA%\HB_Orchestration\`에 고정해 둔다.

| 경로 | 내용 |
|---|---|
| `calls.jsonl` | 호출 기록. 한 줄 = 시도 1회 (UTF-8, camelCase). 여러 서버 프로세스가 같이 덧붙인다 |
| `blobs/<sha256>.txt` | 시스템 프롬프트 · 지침 · modes.json · (선택) 설정 원문, 그리고 **4,096바이트 이상인 프롬프트**. 큰 프롬프트는 기록에 앞 1,000자 + `promptSha256` · `promptBytes`만 남는다 — 원문은 대시보드 호출 상세가 blob에서 읽는다. `Records.BlobFolder`로 위치를 바꿀 수 있다 |
| `inflight/<기록 Id>.json` | 호출 중 표시 (위 "끊긴 호출 비용 추정") |
| `review_in/<임의 이름>/` | `claude-cli` · `codex-cli` 단계의 작업 폴더 (context 파일 전체 복사, codex는 저장소 추적 파일 사본 + context). 실행이 끝나면 지우고, 남은 것은 하루 뒤 다음 실행이 지운다 |
| `verdicts.jsonl` | `record_verdicts` 판정. 같은 키는 마지막 줄이 이긴다. `HB_ORCH_VERDICTS_PATH`로 따로 둘 수 있다 |
| `sessions/<pid>.json` | 서버 프로세스별 모드 실행 횟수 |
| `subagents/<agentId>_<호출 id>.json` | 검토형 서브에이전트(self · perf · security · direction-reviewer)가 지휘자에게 넘긴 결과 원문의 보관본 ([대시보드](dashboard.md) "서브에이전트 결과"). 한 번 쓰면 바꾸지 않는다. `_state/scanned.json` = 보관을 마친 검토형 기록 (경로 → 크기:수정 시각, 지워진 기록은 빠진다) |
| `dumps/` | `Debug.DumpToolResults`가 켜졌을 때만 |
| `config-backup/<시각>/` | 배치 publish 전 설정 백업 (최근 20개, [설치 · 빌드 · 실행](setup.md)) |

예전 기록의 큰 프롬프트를 blobs로 옮기려면 서버 없이 한 번 돌린다 (원래 파일은 `calls.jsonl.bak-<시각>-<PID>`로 남김. 종료 코드 0 = 성공 / 1 = blob 저장 실패가 있었거나, 압축 중 파일 앞부분이 바뀌어 아무것도 바꾸지 않음 — 이때도 백업은 남는다. 파일이 없으면 예외로 끝난다):

```powershell
dotnet <publish 폴더>\HB.Orchestration.Mcp.dll --compact-log [calls.jsonl 경로]
```

### 재시작이 필요한 경우
| 바꾼 것 | 필요한 동작 |
|---|---|
| modes.json (모드·역할·에이전트 배치) | 없음 — 다음 호출부터 반영 |
| appsettings.json (에이전트·모델·제공사·단가) | 없음 — 다음 호출부터 반영 |
| 서버 코드 (publish) | 배치가 MCP 서버만 종료 → Claude Code에서 `/mcp`로 재연결 ([설치 · 빌드 · 실행](setup.md)). Claude Code·대화는 그대로 |
| **API 키 환경 변수** | **PC 재시작** (직접). 환경 변수는 프로그램이 시작될 때 물려받는 값이라, Unity Hub·Toolbox 같은 트레이 프로그램까지 새로 떠야 확실하다 |
