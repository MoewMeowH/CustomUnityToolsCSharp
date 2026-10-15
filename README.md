# ToolWithCS

Unity 개발을 돕는 개인용 C# 도구 모음.

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
│  │  └─ bin/Release/netstandard2.0/EString.Script.Generator.dll   ← Unity에 넣는 dll
│  └─ bin/Release/netstandard2.0/EString.dll                       ← 예전 어셈블리 이름의 산출물
├─ UnityTestFrameWorkChecker/
│  └─ HB_TestFrameworkHelper/    ← 분석기 · 코드 수정 소스 (.sln, .csproj)
│     └─ bin/Release/netstandard2.0/HB_TestFrameworkHelper.dll     ← Unity에 넣는 dll
└─ Orchestration/
   ├─ HB_Orchestration/          ← 솔루션 · 배치 · 문서 (자세한 설명은 이 폴더의 README)
   └─ Publish_Output/            ← publish 결과 (git 제외)
```

## 빌드 환경

| 프로젝트 | 대상 | 필요한 것 |
|---|---|---|
| EString, UnityTestFrameWorkChecker | `netstandard2.0` | .NET SDK (6 이상이면 충분). Roslyn 3.8.0 기준이라 Unity 2020.2 이상에서 동작 |
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
Orchestration/**/bin/
Orchestration/**/obj/
Orchestration/**/.idea/
Orchestration/Publish_Output/
*.user
```

- **EString, UnityTestFrameWorkChecker는 `bin/`을 일부러 커밋합니다.** Unity 프로젝트에 바로 복사해 쓸 수 있게 빌드된 dll을 저장소에 같이 둡니다.
  소스를 고치면 **Release로 다시 빌드한 뒤 dll도 같이 커밋**해야 합니다. 그렇지 않으면 소스와 dll이 어긋납니다.
- 두 프로젝트의 `obj/`는 제외 규칙에 없으므로 커밋할 때 걸러 주세요. 필요하면 `.gitignore`에 `EString/**/obj/`와 `UnityTestFrameWorkChecker/**/obj/`를 추가하면 됩니다.
- Orchestration은 빌드 산출물을 커밋하지 않습니다. `Publish_Output/`은 배치가 매번 다시 만듭니다.
- API 키는 어떤 파일에도 넣지 않습니다. 시스템 환경 변수로만 둡니다 (HB_Orchestration README 2장).
