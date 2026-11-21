# HB_Lint

규칙을 **노드 그래프**로 조립하는 범용 Roslyn 분석기. 규칙 하나 = 그래프 하나 = 규칙 파일(`*.hblint.json`) 하나. 생성기 `hblint-gen`이 규칙 파일들을 C# 어셈블리 어트리뷰트(`[assembly: HBLint.Rule("…")]`)로 바꿔 넣으면, 그 규칙 어셈블리를 참조하는 코드를 분석기가 검사한다. 검사받는 코드에는 아무것도 붙이지 않는다.

- 검사 로직을 규칙마다 코드로 짜지 않는다. 분석기는 그래프의 노드(타입 계열 · 메서드 · 멤버 사용 · 필터 …)를 해석하는 엔진 하나다. Roslyn을 몰라도 노드를 이어 규칙을 만든다.
- Unity 전용이 아니다. 분석기 · 어트리뷰트 모두 netstandard2.0이고 UnityEngine을 참조하지 않는다. Unity에 기대는 규칙은 `requires`로 범위를 정한다.
- 규칙을 additional file이 아니라 C# 소스로 받는다 (Unity에서 additional file이 분석기로 넘어가지 않는 경우가 있어서).
- 그래프 편집 앱은 아직 없다. 지금은 규칙 파일(JSON)을 직접 고치고 `hblint-gen`으로 생성한다 (`hblint.schema.json`으로 편집기 자동 완성 · 검사).

## 구조

```text
unity/com.hb.lint/            Unity 패키지 (UPM) — 분석기 DLL과 어트리뷰트만
  Analyzers/                  분석기 DLL (tools/Build-UnityPackage.ps1이 빌드해 넣음)
  Runtime/                    HBLint.RuleAttribute (asmdef HBLint.Attributes, 의존 없음) — 어트리뷰트 소스의 원본
src/HBLint.Core/              규칙 IR · 노드 카탈로그 · 검증기 · JSON · 생성기 (netstandard2.0, 의존 없음)
src/HBLint.Analyzers/         분석기 (netstandard2.0, Microsoft.CodeAnalysis.CSharp 4.3 — Unity 6.3+). Core의 Json/ · Ir/ 소스를 함께 컴파일
src/HBLint.Attributes/        unity/…/Runtime 소스를 링크해 컴파일 (NuGet · .NET용)
src/HBLint.Generator.Cli/     생성기 CLI hblint-gen (net10.0)
tests/                        dotnet test
tools/Build-UnityPackage.ps1  분석기를 Release로 빌드해 패키지 Analyzers/로 복사
samples/                      예시 규칙 파일 셋 · 스키마 · 생성물 (hblint-gen init이 만드는 것과 같음)
samples/more/                 예시 규칙 더 (CLL0004~0012 — 할당 · Unity 성능 9종, init은 넣지 않음, 필요한 것만 규칙 폴더에 복사)
```

분석기 코드를 바꿨으면 `tools/Build-UnityPackage.ps1`을 돌려 패키지 DLL을 갱신한다.

## 규칙 파일 (*.hblint.json)

`samples/no-getcomponent-in-update.hblint.json` — MonoBehaviour의 Update 계열(과 그것이 부르는 같은 클래스 메서드 한 단계)에서 GetComponent를 쓰면 경고:

```json
{
  "$schema": "./hblint.schema.json",
  "version": 1,
  "id": "CLL0002",
  "title": "Update 계열에서 GetComponent 금지",
  "severity": "warning",
  "requires": ["UnityEngine.CoreModule"],
  "nodes": {
    "monoBehaviour": { "kind": "types.derived", "from": "scope", "type": "UnityEngine.MonoBehaviour" },
    "updates": { "kind": "methods.named", "from": "monoBehaviour", "names": ["Update", "LateUpdate", "FixedUpdate"] },
    "helpers": { "kind": "methods.follow", "from": "updates", "depth": 1 },
    "getComponent": { "kind": "sites.member", "from": "helpers", "members": ["UnityEngine.Component.GetComponent", "UnityEngine.GameObject.GetComponent"] },
    "report": { "kind": "report", "from": "getComponent", "message": "Update 계열에서 GetComponent를 부르지 마세요 — Awake에서 필드에 캐싱하세요." }
  }
}
```

머리 칸:

| 칸 | |
|---|---|
| `version` | 1 (필수) |
| `id` | 진단 id `CLL0001`~`CLL0200` (필수, 규칙마다 달라야 함, 지운 규칙의 id는 다시 쓰지 않는다 — 남은 `#pragma`가 새 규칙을 조용히 막지 않게) |
| `title` | 설명 (생성물 주석) |
| `severity` | `error` · `warning`(기본) · `info`. Unity 콘솔에는 info가 보이지 않는다. `error`는 Unity 컴파일을 막는다 |
| `requires` | 검사 중인 어셈블리가 이 어셈블리들을 모두 참조할 때만 규칙을 쓴다 |
| `ignoreAssemblies` | `*` 와일드카드 어셈블리 이름 — 맞으면 검사하지 않는다 (분석기는 asmdef를 못 읽으므로 에디터 전용 어셈블리 등을 이름으로) |
| `nodes` | 노드 이름 → 노드 (필수) |
| `layout` | 편집기용 노드 위치 · 메모 (분석기에 넘기지 않음) |

### 노드와 소켓

노드는 `from`으로 앞 노드를 받는다. `"from": "scope"`는 규칙 시작(컴파일 전체). 선의 종류(소켓)는 왼쪽에서 오른쪽으로 좁아진다 — **Scope → Types → Methods → Sites → Groups → Report**. 받을 수 없는 소켓끼리 이으면 검증 오류다. 값(이름 · 숫자 · 메시지)은 선이 아니라 노드 안 칸이다. report는 규칙마다 하나.

| 노드 (`kind`) | 받음 → 내보냄 | 칸 · 동작 |
|---|---|---|
| `types.derived` | Scope · Types → Types | `type`: 기준 타입과 그 자식 (인터페이스면 그것을 구현한 타입) |
| `methods.named` | Types → Methods | `names`: 그 타입에 선언된 메서드. 메서드 안 로컬 함수는 그 메서드로 보고, 람다 · 접근자 안은 보지 않는다 (나중에 도는 콜백일 수 있어서) |
| `methods.coroutine` | Types → Methods | 그 타입에 선언된 `IEnumerator` · `IEnumerator<T>` 반환 일반 메서드 (칸 없음). 반환형만 보므로 `IEnumerable` 구현의 `GetEnumerator`처럼 코루틴이 아닌 것도 들어간다 |
| `methods.follow` | Methods → Methods | `depth`(0~2): 그 메서드가 부르는 **같은 클래스** 메서드를 그만큼 따라간다. 따라가서 찾으면 메시지 끝에 `(Update → Helper)` 경로가 붙는다 |
| `sites.member` | Scope · Types · Methods → Sites | `members`: `타입 메타데이터 이름.멤버 이름` — 오버로드 · 제네릭 전부, 그것을 **오버라이드한 멤버**까지 (`new`로 가린 같은 이름은 아님). 속성 · 필드도. 타입은 멤버가 **선언된 타입**으로 (`UnityEngine.Component.GetComponent`). `타입.*`은 그 타입에 선언된 메서드 · 속성 · 필드 전부 (`System.Linq.Enumerable.*` — 쿼리 식 포함). `uses`: `call` · `read` · `write` · `compare`(속성 · 필드가 `==` · `!=`의 한쪽 — 암시적 변환은 지나감(`count == maybe`), `(object)x == y`처럼 명시적 변환이 끼거나 `Equals` · `switch` · `is "x"`는 아님. 비교도 읽기라서 `read`에 들어감) (없으면 전부) |
| `sites.namespace` | 〃 | `namespaces`: 그 네임스페이스와 그 아래의 타입 · 멤버 · `using` (`usings: false`면 using 지시문은 뺌). `UnityEditor.AssetDatabase.Refresh()`처럼 왼쪽 타입이 이미 걸리면 오른쪽 멤버는 다시 걸지 않는다 |
| `sites.declaredInDefine` | 〃 | `define`: **같은 어셈블리에서** 그 디파인 안에만 선언된 메서드 · 필드 · 속성 · 타입을 쓴 곳 (partial이면 선언이 모두 안일 때만). `#if D … #else …(같은 이름) #endif`처럼 디파인 없이 켜지는 갈래에 같은 이름이 낱말로 있으면 걸지 않는다. 다른 어셈블리의 `#if`는 알 수 없다 |
| `sites.nullOperator` | 〃 | `?.` · `??` · `??=` · `is null`(`is not null` · `and`/`or` 패턴 안 포함). `operators`로 고를 수 있다. 잡지 않는 것: `is {}` · `ReferenceEquals` · `switch`의 `null` 갈래 |
| `sites.new` | 〃 | `new X()` · `new()` · `new T()` · `new X[n]` · `new[] { }` · 배열 초기화 `{ … }` · 무명 `new { }` · `new Action(M)`. 대상 타입 = 만드는 형. params 인자 배열 · 메서드 그룹 · 람다의 암시적 델리게이트 변환은 잡지 않는다 (소스에 new가 없음) |
| `sites.boxing` | 〃 | 박싱 변환 (대입 · 인자 · params · 인터페이스로 · nullable · 캐스트). 대상 타입 = 박싱되는 값의 형. **잡지 않는 것**: `string + 값` · `+=` (컴파일러가 `값.ToString()`으로 바꿔 박싱하지 않음 — 연결은 `sites.stringConcat`), 보간 `$"{i}"`(대상 BCL에 따라 박싱될 수 있음) · 오버라이드 없는 struct의 `ToString` · `GetHashCode` · `Equals` 호출 · `HasFlag`의 인스턴스 · `foreach (object x in List<int>)` (변환으로 보이지 않는 박싱) |
| `sites.stringConcat` | 〃 | string `+` · `+=` · 보간. 식 하나에 한 번(`a + b + c`, 연결 안 보간은 바깥 하나), 상수끼리는 뺀다. `string.Concat` · `Format` 직접 호출은 잡지 않는다 |
| `sites.lambda` | 〃 | 람다 · 무명 메서드를 **만드는 자리**. `capturingOnly`(기본 true): 지역 · 매개변수 · `this`를 캡처하는 것만 (안쪽 람다가 캡처한 것은 안쪽 람다 몫). 쿼리 식 안 암시적 람다 · 메서드 그룹 → 델리게이트는 잡지 않는다 |
| `filter.targetType` | Sites → Sites | `type`: 사이트가 다루는 식의 정적 타입이 이 계열인 것만. `category`: `reference` · `value` — 참조 · 값 형식인 것만 (타입 매개변수는 `class` · `struct` 제약으로, 제약이 없으면 어느 쪽도 아님). 둘 중 하나 이상, 둘 다면 둘 다 맞아야. 타입 매개변수의 계열은 제약으로 본다. 정적 타입이 인터페이스 · `object`면 `type`에 걸리지 않는다. 대상 타입은 `sites.nullOperator`(왼쪽 식) · `sites.member`(인스턴스) · `sites.new` · `sites.boxing` 사이트에만 있다 — 다른 사이트 뒤에 이으면 늘 0건 |
| `filter.context` | Sites → Sites | `context` · `where`(`inside` · `outside`). `loop`: for · foreach · while · do의 본문 · 조건 · for 증감식 (for 초기화 · foreach 컬렉션 식은 한 번만 돌아서 밖, 람다 · 로컬 함수 경계에서 멈춤). `lambda`: 람다 · 무명 메서드 몸통 (Methods에서 받은 사이트는 원래 람다 몸통을 빼므로 Scope · Types에서 뜻이 있음) |
| `filter.define` | Sites → Sites | `define` · `where`(`inside` · `outside`): **그 디파인이 없으면 다른 기호가 무엇이든 꺼지는 구간** 안 · 밖. `#if` · `#elif` · `#else` · 중첩 · `!` `&&` `\|\|` `==` `!=`를 읽는다. `#if UNITY_EDITOR \|\| DEVELOPMENT_BUILD`는 개발 빌드에서 켜지므로 구간이 아니다 |
| `sites.union` | Sites 여럿 → Sites | `from`: 노드 이름 배열(둘 이상). 같은 종류의 사이트(구문끼리 · 멤버 사용끼리)가 같은 위치면 한 번만 알린다. `sites.member`와 구문 사이트(`sites.namespace` 등)가 같은 이름을 함께 잡으면 두 번 나올 수 있다 |
| `groups.by` | Sites → Groups | `key`: `method` = 사이트를 직접 감싸는 몸통(메서드 · 접근자 · 람다 · 로컬 함수 각각 따로)마다, `methodAndTarget` = 그 안에서 같은 대상마다 — 대상은 멤버 사용의 인스턴스 (`transform`과 `this.transform`은 같음, 지역 · 매개변수 · 필드 사슬로 가름 — 지역 변수는 선언으로 가르므로 중간에 다른 값을 다시 대입해도 같은 대상, `GetComponent<T>()` 같은 호출 결과는 매번 다른 대상, 대상이 없는 사이트는 빠짐) |
| `groups.pair` | Sites `a` + Sites `b` → Groups | `key`는 위와 같음. a · b 사이트가 둘 다 있고 서로 다른 사이트가 둘 이상인 묶음만 (같은 메서드의 if · else 갈래도 한 묶음) |
| `groups.count` | Groups → Groups | `min`(2~100): 사이트가 그 수 이상인 묶음만 |
| `report` | Sites · Groups → 끝 | `message`(필수), `appendPath`(기본 true — 따라가기 경로 붙이기). Groups를 받으면 **묶음마다 하나** — 첫 사이트 위치에 내고 나머지 사이트는 추가 위치 |

어느 노드에서 시작하느냐가 범위다: `sites.*`를 `scope`에서 받으면 컴파일 전체, Types에서 받으면 그 타입 계열 안, Methods에서 받으면 그 메서드(따라가기 포함) 안. Groups가 있는 규칙은 타입 하나의 사이트를 다 모은 뒤 판정하므로 타입 밖 코드(최상위 문)는 묶이지 않는다.

예시 규칙 더 (`samples/more/`, 쓰려면 규칙 폴더에 복사 — `$schema`가 규칙 폴더의 `./hblint.schema.json`을 가리키므로 `samples/more/` 안에서는 편집기 자동 완성이 안 되고 복사한 뒤에 된다):

| id | 파일 | 그래프 |
|---|---|---|
| CLL0004 | `new-in-update` | MonoBehaviour → Update 계열 → `sites.new` → 대상 타입(`reference`) |
| CLL0005 | `boxing-in-update` | MonoBehaviour → Update 계열 → `sites.boxing` |
| CLL0006 | `linq-in-update` | MonoBehaviour → Update 계열 → `sites.member`(`System.Linq.Enumerable.*`) |
| CLL0007 | `string-concat-in-update` | MonoBehaviour → Update 계열 → `sites.stringConcat` |
| CLL0008 | `capturing-lambda-in-update` | MonoBehaviour → Update 계열 → `sites.lambda` |
| CLL0009 | `waitforseconds-in-coroutine-loop` | MonoBehaviour → `methods.coroutine` → `sites.new` → 대상 타입(`WaitForSeconds`) → 맥락(루프 안) |
| CLL0010 | `tag-compare` | `sites.member`(`Component.tag` · `GameObject.tag`, `compare`) |
| CLL0011 | `position-rotation-pair` | MonoBehaviour → 짝 묶기(a: `Transform.position` 쓰기, b: `rotation` 쓰기, `methodAndTarget`) |
| CLL0012 | `transform-repeat-in-update` | MonoBehaviour → Update 계열 → `sites.member`(`transform` 읽기) → 묶기(`methodAndTarget`) → 개수(4) |

검증(`hblint-gen validate`, 분석기도 같은 검사): JSON 형식 · 모르는 칸 · kind · 필수 칸 · 값 범위 · 노드 수(256개까지) · 없는 노드 · 소켓 · 고리 · report 하나 · 규칙 사이 id 중복(분석기는 먼저 읽은 규칙만 쓰고 나머지를 CLL9002로). report에 닿지 않는 노드는 경고이고 분석기는 그 노드를 무시한다.

생성된 코드(`<auto-generated>`)는 검사하지 않는다.

## hblint-gen

```text
dotnet run --project src/HBLint.Generator.Cli -- init <규칙 폴더> <LintRules.g.cs> [--asmdef <규칙 어셈블리 이름>]
dotnet run --project src/HBLint.Generator.Cli -- validate <규칙 폴더>
dotnet run --project src/HBLint.Generator.Cli -- generate <규칙 폴더> <LintRules.g.cs> [--check]
dotnet run --project src/HBLint.Generator.Cli -- schema <hblint.schema.json> [--check]
```

- 규칙 폴더 = 그 폴더 **바로 아래**의 `*.hblint.json` (하위 폴더는 보지 않는다 — 생성물 · asmdef 폴더를 그 아래에 둬도 섞이지 않게).
- `init`: 규칙 폴더에 규칙 파일이 없으면 예시 셋(`samples/`와 같음)을, `--asmdef`를 주면 생성물 폴더에 asmdef가 없을 때 규칙 asmdef(`HBLint.Attributes` 참조, Auto Referenced)를 만든다. 규칙 폴더의 `hblint.schema.json`은 늘 최신으로 쓴다. 그 뒤 `generate`.
- 오류가 하나라도 있으면 모두 보여 주고(`파일(줄,칸): 내용`) 생성물은 바꾸지 않는다 (종료 코드 1).
- 내용이 같으면 파일을 건드리지 않는다. 출력은 늘 같다 (id 순 · UTF-8 BOM 없음 · LF).
- `--check`는 쓰지 않고 비교만 한다 (줄바꿈 차이는 무시, 다르면 종료 코드 1) — CI에서 규칙 파일과 생성물이 어긋났는지 볼 때.
- 생성물 `LintRules.g.cs`는 저장소에 커밋한다. 규칙을 고친 사람이 `generate`하고, 팀원은 pull로 받는다.

## 진단 id

| ID | |
|---|---|
| CLL0001~CLL0200 | 규칙 (규칙 파일의 `id`). Roslyn 분석기는 미리 선언한 id로만 보고할 수 있어 이 묶음 안에서 고른다 |
| CLL9001 | 규칙의 대상 타입 · 멤버를 찾을 수 없음 (`requires`는 통과했는데 — 이름 오타). 타입을 못 찾으면 그 규칙은 빠지고, 멤버는 못 찾은 것만 빠진다 |
| CLL9002 | 규칙을 읽을 수 없음 (검증 오류 · 다른 규칙과 id가 겹침 — 그 규칙만 빠진다) |

CLL9001 · CLL9002는 위치 없는 진단이라 Unity 콘솔에 보이지 않는다 — 규칙을 고치면 `hblint-gen validate` · `generate`로 먼저 확인한다.

규칙 하나를 끄기:

- 코드 한 곳: `#pragma warning disable CLL0002` · `[SuppressMessage]`.
- Unity 프로젝트 전체: `Assets/Default.ruleset`에 `<Rule Id="CLL0002" Action="None" />` (`<Rules AnalyzerId="HBLint.Analyzers" RuleNamespace="HBLint.Analyzers">` 안). Unity 6.3에서 확인. `.editorconfig`는 프로젝트 루트 · `Assets/` 어느 쪽에 둬도 Unity가 쓰지 않는다 (6.3에서 확인).
- .NET SDK 프로젝트: `.editorconfig`의 `dotnet_diagnostic.CLL0002.severity = none`.

## 규칙을 어디서 읽나

분석기는 검사 중인 어셈블리 자신과, 그것이 참조하는 어셈블리 중 `HBLint.Attributes`를 참조하는 어셈블리(= 규칙 어셈블리)의 `[assembly: HBLint.Rule(…)]`을 읽는다.

- 규칙 어셈블리를 **직접** 참조하지 않는 어셈블리는 검사하지 않는다 (opt-in). Unity asmdef 참조는 간접으로 이어지지 않으므로 C → B → 규칙 asmdef인 C는 검사되지 않는다.
- 검사 중인 어셈블리가 `HBLint.Attributes`를 참조하지 않아도 된다 (Unity에서 규칙 asmdef만 참조한 경우) — 그때는 규칙 어셈블리 메타데이터에서 직접 읽는다.
- 어트리뷰트는 이름으로 찾으므로 어트리뷰트 어셈블리 이름은 `HBLint.Attributes` 그대로 두고, 생성물과 다른 어셈블리에 둔다.
- 타입 이름은 메타데이터 이름이다: 네임스페이스 포함, 중첩 타입은 `Outer+Inner`, 제네릭은 `` List`1 ``.

## Unity (UPM 패키지, 6000.3+)

### 설치

1. `Packages/manifest.json`의 `dependencies`에 한 줄 넣는다 (Package Manager > `+` > *Install package from git URL…*에 URL만 넣어도 된다):

   ```json
   "com.hb.lint": "https://github.com/MoewMeowH/CustomUnityToolsCSharp.git?path=HB_Lint/unity/com.hb.lint"
   ```

   특정 커밋에 고정하려면 URL 끝에 `#<커밋 해시>`를 붙인다.
2. Unity 프로젝트 폴더에서 `init`을 한 번 돌린다 (이 저장소를 받아 둔 곳의 CLI 프로젝트 경로로):

   ```text
   dotnet run --project <HB_Lint>/src/HBLint.Generator.Cli -- init Assets/HBLint Assets/HBLint/Rules/LintRules.g.cs --asmdef CLL.LintRules
   ```

   - `Assets/HBLint/*.hblint.json` — 예시 규칙 셋 · `Assets/HBLint/hblint.schema.json`
   - `Assets/HBLint/Rules/CLL.LintRules.asmdef` — 규칙 asmdef · `Assets/HBLint/Rules/LintRules.g.cs` — 생성물
3. Unity가 가져와 컴파일하면 위반 코드에 `warning CLL0001` 등이 뜬다.

규칙을 고치면 `generate Assets/HBLint Assets/HBLint/Rules/LintRules.g.cs`로 다시 만든다.

### 0.1에서 올릴 때

0.1은 Unity 메뉴(`Tools/HB Lint/설치`)로 `rules.json` → `LintRules.g.cs`를 만들었고, 그 생성물은 0.2에서 지운 종류별 어트리뷰트(`HBLint.BanNullOperatorRule` 등)를 쓴다. 패키지만 올리면 규칙 asmdef가 CS0246으로 깨져 컴파일이 막히므로, 올린 직후 위 `init`(규칙 폴더 `Assets/HBLint`)을 돌려 `LintRules.g.cs`를 새로 만든다. 옛 규칙은 `*.hblint.json`으로 다시 적는다 (예시 셋이 옛 세 종류와 같은 뜻). 옛 `Assets/HBLint/rules.json` · `rules.schema.json` · `ProjectSettings/HBLintSettings.asset`은 지워도 된다.

### 어느 코드가 검사되나

- **Assembly-CSharp**(asmdef 없는 스크립트)는 자동으로 검사된다 (규칙 asmdef가 Auto Referenced).
- **asmdef**는 `CLL.LintRules`를 *직접* 참조에 넣어야 검사된다. 검사할 asmdef마다 넣는다.
- 패키지 · 플러그인 어셈블리도 분석기는 받지만 `CLL.LintRules`를 참조하지 않으면 규칙이 없어 아무것도 하지 않는다.

## 시험

```text
dotnet test HB_Lint.slnx
```
