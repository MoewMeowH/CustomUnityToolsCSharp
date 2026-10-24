# ToolWithCS

Unity 개발을 돕는 개인용 C# 도구 모음.

## 저장소의 의의
AI 에 익숙해지고 익숙해지는 김에 평소 필요하다 느꼈던 툴이나 만들까? 가 점점 커져버림
오케스트레이션을 구축하면서 방향과 위험사항, 사용 사례등을 내가 정리하고 그에 대한 생성을 오케스트레이션이 함

| 폴더 | 종류 | 한 줄 요약 | 쓰는 곳 |
|---|---|---|---|
| [`EString/`](EString/README.md) | Roslyn 소스 생성기 | `[E2String]`이 붙은 enum에 박싱·할당 없는 문자열/정수 변환 확장 메서드를 생성 | Unity (dll 복사) |
| [`UnityTestFrameWorkChecker/`](UnityTestFrameWorkChecker/README.md) | Roslyn 분석기 + 코드 수정 | `[NeedSlotNullCheck]` 슬롯 필드의 타입과 `[SerializeField]` 누락을 컴파일 단계에서 검사 | Unity (dll 복사) / Rider |
| [`Orchestration/HB_Orchestration/`](Orchestration/HB_Orchestration/README.md) | MCP 서버 + 대시보드 | Claude Code를 지휘자로 두고 GPT · Gemini · 로컬 모델을 모드 단위로 호출 | Claude Code (Unity 프로젝트에서 실행) |

## 구조

```
ToolWithCS/
├─ .gitignore
├─ EString/
│  ├─ Script/                    ← 생성기 소스 (EString.csproj, E2StringGenerator.cs)
│     └─ bin/Release/netstandard2.0/EString.dll                    ← Unity에 넣는 dll
├─ UnityTestFrameWorkChecker/
│  └─ HB_TestFrameworkHelper/    ← 분석기 · 코드 수정 소스 (.sln, .csproj)
│     └─ bin/Release/netstandard2.0/HB_TestFrameworkHelper.dll     ← Unity에 넣는 dll
├─ Orchestration/
│  ├─ HB_Orchestration/          ← 솔루션 · 배치 · 문서 (자세한 설명은 이 폴더의 README)
│  │  ├─ src/ · tests/           ← MCP 서버 · 대시보드 · 공용 코드, xunit 테스트
│  │  ├─ project-files/          ← Unity 프로젝트에 복사하는 CLAUDE.md · .claude/(명령 · 훅 · 서브에이전트) 원본
│  │  └─ tools/                  ← project-files 동기화 · 골든 회차 스크립트
│  └─ Publish_Output/            ← publish 결과 (git 제외)
└─ docs/TestFramework/           ← UnityTestFrameWorkChecker 임시 작업 로그
```

## 빌드 환경

| 프로젝트 | 대상 | 필요한 것 |
|---|---|---|
| EString | `netstandard2.0` | .NET SDK (6 이상이면 충분). `Microsoft.CodeAnalysis.CSharp` 4.3.0 (`IIncrementalGenerator`) — Unity 6.3 이상 (Unity 6은 생성기에 Roslyn 4.3을 요구) |
| UnityTestFrameWorkChecker | `netstandard2.0` | .NET SDK (6 이상이면 충분). Roslyn 3.8.0 분석기 — Unity 6.3 이상 (분석기는 더 새 컴파일러에서도 로드됨) |
| HB_Orchestration | `net10.0` | .NET 10 SDK (없으면 `Directory.Build.props`에서 `net9.0`으로 변경) |

```powershell
# Unity용 dll 두 개 (Release)
dotnet build EString/Script/EString.csproj -c Release
dotnet build UnityTestFrameWorkChecker/HB_TestFrameworkHelper/HB_TestFrameworkHelper.csproj -c Release

# 오케스트레이션은 배치 메뉴로 (publish · 대시보드)
Orchestration/HB_Orchestration/HB_Orchestration.bat
```

Rider에서는 각 폴더의 솔루션을 따로 엽니다.

- `UnityTestFrameWorkChecker/HB_TestFrameworkHelper/HB_TestFrameworkHelper.sln`
- `Orchestration/HB_Orchestration/HB_Orchestration.slnx`
- EString은 솔루션이 없으므로 `EString/Script/EString.csproj`를 직접 엽니다.

## Git 정책 (`.gitignore`)

```
.idea/  .vs/  *.user  *.suo  obj/          ← IDE 설정 · 빌드 중간 산출물
**/bin/**                                   ← 빌드 산출물은 기본 제외
!EString/Script/bin/Release/netstandard2.0/EString.dll
!UnityTestFrameWorkChecker/HB_TestFrameworkHelper/bin/Release/netstandard2.0/HB_TestFrameworkHelper.dll
Orchestration/Publish_Output/
Orchestration/HB_Orchestration/docs/        ← 계획서 · 작업 로그 (로컬에만)
review_tmp/  subagent_in/  docs/image/  GoldenSet/   ← 리뷰 임시 사본 · 사내 시안 · 사내 코드 (H2 푸시 가드도 막음)
.claude/push_guard.local.txt  Orchestration/HB_Orchestration/tools/sync-targets.local.txt   ← 이 PC의 개인 값 · 경로
```

- **Unity에 넣는 Release dll 두 개만 일부러 커밋합니다.** Unity 프로젝트에 바로 복사해 쓸 수 있게 하려는 것입니다. 같은 폴더의 `.pdb` · `.deps.json`, `bin/Debug/`, 언어별 리소스 폴더는 커밋하지 않습니다.
  소스를 고치면 **Release로 다시 빌드한 뒤 dll도 같이 커밋**해야 합니다. 그렇지 않으면 소스와 dll이 어긋납니다.
- 커밋할 dll을 새로 추가하거나 이름을 바꾸면 `.gitignore`의 `!` 예외 줄도 같이 고쳐야 합니다.
- Orchestration은 빌드 산출물을 커밋하지 않습니다. `Publish_Output/`은 배치가 매번 다시 만듭니다.
- API 키는 어떤 파일에도 넣지 않습니다. 시스템 환경 변수로만 둡니다 (`OPENAI_API_KEY` · `GEMINI_API_KEY` · `FIREWORKS_API_KEY`, HB_Orchestration README 2장).
- 루트의 `CLAUDE.md` · `REVIEW_GUIDELINES.md` · `PROJECT.md` · `.claude/` · `.mcp.json`은 이 PC의 Claude Code 작업 설정이라 커밋하지 않습니다 (원본은 `Orchestration/HB_Orchestration/project-files/`). **`.gitignore`에 넣지 않았으므로 `git add .` · `git add -A`를 쓰지 말고 파일을 지정해 추가합니다** — `.mcp.json`에는 이 PC 절대 경로가 들어 있고, H2 훅은 비밀 값 모양 · 지정 폴더만 막습니다. `Orchestration/HB_Orchestration/docs/`(계획서 · 작업 로그)는 `.gitignore`로 뺍니다.

