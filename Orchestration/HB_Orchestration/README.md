# HB_Orchestration

Claude Code를 **지휘자**로 두고, 다른 회사 모델(GPT · Gemini · DeepSeek · Codex)을 **모드** 단위로 불러 쓰는 MCP 서버와 대시보드입니다.

지휘자(Claude Code)가 `run_mode("review_main", …)`처럼 부르면, 서버가 정해 둔 에이전트들에게 코드 · 설계를 보내고 결과를 모아 돌려줍니다. 모든 호출은 기록되고 대시보드에서 비용 · 판정과 함께 볼 수 있습니다.

```
Claude Code ──MCP──▶ HB.Orchestration.Mcp
  run_mode            ├─ modes.json     "무엇을 어떤 순서로" (저장하면 바로 반영)
  list_modes          ├─ appsettings    "누가" (제공사 · 모델 · 단가)
  save_result         └─ 호출마다 기록 ──▶ calls.jsonl ◀── 대시보드
  record_verdicts
  server_info
```

## 무엇을 할 수 있나

| 하고 싶은 것 | 모드 (예) | 하는 일 |
|---|---|---|
| 코드 리뷰 | `review_main` · `review_high` | Codex(또는 GPT) · DeepSeek가 변경을 리뷰 |
| 검토형 리뷰 (API) | `sub_self` · `sub_perf` · `sub_security` · `sub_direction` | Claude(API 키)가 읽기 전용으로 코드 · 성능 · 보안 · 방향을 검토 |
| 설계 결정 | `design_lite` · `design_main` · `design_high` | 비평 → 반박 · 논리 검증 → 다회차 토론 후 판정 |
| 성능 점검 | `perf_main` · `perf_high` | 핫패스 · 할당 · 동기화 위주 검토 |
| 기능 · 도구 조사 | `research_main` | 웹검색으로 후보를 모으고 적용 가능성 판단 |
| 크래시 조사 | `crash_main` | 분류 · 비슷한 사례 검색 · 재현 스텝 |
| 큰 작업 사전 점검 | `preflight_*` | 반박 · 위험 · 성능을 동시에 보고 확정 사항을 정리 |
| 작업 지시서 검토 | `work_order_main` | `/work-order`가 만든 지시서 초안 검토 |

전체 목록은 Claude Code에서 `list_modes`로 볼 수 있습니다.

## 빠른 시작

**필요한 것**: Windows · .NET 10 SDK · Claude Code · 쓸 모델의 API 키(환경 변수 `OPENAI_API_KEY` · `GEMINI_API_KEY` · `FIREWORKS_API_KEY` 등).

1. **빌드 · 시험**
   ```powershell
   dotnet build HB_Orchestration.slnx
   dotnet test tests/HB.Orchestration.Tests
   ```
2. **publish** — `HB_Orchestration.bat`을 더블클릭하고 메뉴 `1`. 기본 출력은 `..\Publish_Output` (`HB_Orchestration.config`에서 바꿈). `dotnet run`으로 MCP를 등록하지 않습니다.
3. **프로젝트에 연결** — 쓸 프로젝트 루트에 `project-files/.mcp.example.json`을 `.mcp.json`으로 복사하고 publish 폴더 경로를 넣습니다. 규칙 · 명령 · 훅은 `tools/Sync-ProjectFiles.ps1`로 복사합니다 → [프로젝트에 연결하기](manual/project-setup.md)
4. **확인** — Claude Code에서 `/mcp`로 `hb-orchestration`이 연결됐는지 보고 "server_info 실행해줘".
5. **대시보드** — 배치 메뉴 `2` (기본 http://localhost:5280).

API 키 환경 변수를 새로 넣었으면 PC를 다시 시작하는 게 확실합니다 (프로그램은 켜질 때의 환경 변수만 봅니다).

## 폴더 구조

```
HB_Orchestration/
├─ HB_Orchestration.slnx          솔루션 (Rider · VS)
├─ HB_Orchestration.bat           publish · 대시보드 실행 메뉴
├─ HB_Orchestration.config        위 배치의 경로 설정
├─ src/
│  ├─ HB.Orchestration.Shared/    호출 기록 모델 · 저장
│  ├─ HB.Orchestration.Agents/    제공사 · 에이전트 · 모드 실행기 (핵심)
│  ├─ HB.Orchestration.Mcp/       MCP 서버 · appsettings.json · modes.json
│  ├─ HB.Orchestration.ChatUi/    채팅 화면 공용 부품 (Markdown 렌더 등)
│  └─ HB.Orchestration.Dashboard/ 대시보드 (호출 기록 · 모드 편집 · 판정)
├─ tests/HB.Orchestration.Tests/  시험 (xunit)
├─ project-files/                 프로젝트 쪽 규칙 · 명령 · 서브에이전트 · 훅의 원본과 설정 견본
├─ manual/                        자세한 설명서 (아래)
└─ tools/                         동기화 · 골든 실행 스크립트
```

## 설명서

| 문서 | 내용 |
|---|---|
| [설치 · 빌드 · 실행](manual/setup.md) | 패키지 · 빌드, `HB_Orchestration.bat` 메뉴 · 설정, publish와 실행 중인 서버, 빌드 에러 |
| [에이전트 설정](manual/agents.md) | `appsettings.json` 필드, 제공사(claude-cli · codex-cli 포함), 재시도, 비용 추정, 응답 검사기, 데이터 폴더, 재시작이 필요한 경우 |
| [모드와 run_mode](manual/modes.md) | `modes.json` 구조(역할 · 모드 · 단계), 웹검색 · 토론, run_mode 인자 · 결과 형식, 구조화 출력, save_result · record_verdicts, 토큰 절약 |
| [프로젝트에 연결하기](manual/project-setup.md) | 처음 설치, 동기화 스크립트, 명령 · 훅 목록, Unity CLI 연동 |
| [대시보드](manual/dashboard.md) | 화면별 설명 (호출 · 모드 · 판정 · 세션 · 채팅방 등) |
| [골든 세트](manual/golden.md) | 프롬프트 · 모드 · 모델을 바꿀 때 하는 회귀 평가 |
