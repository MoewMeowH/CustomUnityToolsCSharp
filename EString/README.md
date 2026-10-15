# EString — E2String 소스 생성기

`[E2String]`이 붙은 enum마다 **박싱 · 리플렉션 · 문자열 할당 없이** 동작하는 변환 확장 메서드를 컴파일 시점에 생성하는 Roslyn 소스 생성기입니다.
`Enum.ToString()` · `Enum.Parse` · `Enum.IsDefined`를 런타임에 부르지 않으려는 용도입니다.

- 대상: `netstandard2.0`, `Microsoft.CodeAnalysis.CSharp` 3.8.0 (`ISourceGenerator`)
- 호환: Unity 2020.2 이상 (Unity 6 포함). 생성 코드는 C# 8 문법만 쓰며 Unity API에 의존하지 않습니다.
- 산출물: `Script/bin/Release/netstandard2.0/EString.dll`

## 사용 예

```csharp
namespace Game
{
    [E2String]
    public enum ItemGrade { Common, Rare, Epic }

    [E2String, System.Flags]
    public enum Perm { None = 0, Read = 1, Write = 2, Exec = 4 }
}

// 생성된 클래스: Game.ItemGradeExtensions
string s    = ItemGrade.Rare.ToStringFast();          // "Rare" (할당 없음)
ItemGrade g = "Epic".ToItemGrade();                   // 정확히 일치. 실패하면 fallback(기본 default)
ItemGrade h = " epic ".ToItemGrade(ignoreCase: true); // 공백 무시 · 대소문자 무시 · "2" 같은 숫자도 허용
bool ok     = ItemGradeExtensions.TryParseFast("Rare", out var r);
int  i      = ItemGrade.Epic.ToInt();                 // 2
ItemGrade k = 1.ToItemGrade();                        // 정의 검사 후 변환
bool def    = ItemGradeExtensions.IsDefined(5);       // false
foreach (var v in ItemGradeExtensions.AllValues) { }  // 선언 순서

// [Flags]
string p  = (Perm.Read | Perm.Exec).ToStringFast();   // "Read, Exec" (값마다 첫 호출에서만 할당 후 캐시)
Perm   q  = "Read, Exec".ToPerm();                    // 조합 파싱
bool   vf = PermExtensions.IsValidFlags(5);           // true
Perm   w  = 5.ToPerm();                               // Read | Exec
```

## 생성되는 멤버

클래스 이름은 `{바깥타입_}{Enum}Extensions`입니다. enum과 같은 namespace에 생성되며, 파일 이름은 `{Namespace}.{클래스 이름}.g.cs`입니다.
클래스 접근성은 enum을 따릅니다. enum과 바깥 타입이 모두 `public`이면 `public`이고, 그 외(`internal` 등)에는 `internal`입니다.

| 멤버 | 설명 |
|---|---|
| `const int Count` | 멤버 개수 (별칭 포함) |
| `string[] Names` / `IReadOnlyList<string> AllNames` | 식별자 이름 (선언 순서, `@` 없이) |
| `T[] Values` / `IReadOnlyList<T> AllValues` | 값 (선언 순서) |
| `string ToStringFast(this T, string? fallback = null)` | switch로 문자열 반환. 정의 밖 값이면 `fallback ?? value.ToString()` |
| `T To{Enum}(this string?, T fallback = default)` | 문자열 → enum. 대소문자 구분, 정확히 일치 |
| `static bool TryParseFast(string?, out T, T fallback = default)` | 위와 같은 규칙의 안전 파싱 (확장 메서드 아님) |
| `T To{Enum}(this string?, bool ignoreCase, T fallback = default)` | 유연한 파싱: 앞뒤 공백 무시, `ignoreCase`, 정의된 값의 숫자 문자열 |
| `static bool TryParse(string?, bool ignoreCase, out T)` | 위와 같은 규칙의 안전 파싱 |
| `int ToInt(this T)` / `int ToIntFast(this T)` | enum → int (명시적 캐스트) |
| `T To{Enum}(this int, T fallback = default)` | 정의 검사 후 변환 (`[Flags]`는 `IsValidFlags` 검사) |
| `T To{Enum}Fast(this int)` | 검사 없이 바로 변환 |
| `static bool IsDefined(T)` / `static bool IsDefined(int)` | 이름이 정의된 값인지 검사 |
| `static bool IsValidFlags(T)` / `static bool IsValidFlags(int)` | `[Flags]` 전용. 정의된 플래그들의 조합인지 검사 (0 포함) |

### 동작 규칙

- **기본 형식**: `byte` · `sbyte` · `short` · `ushort` · `int` · `uint` · `long` · `ulong` 모두 지원합니다. switch는 기본 형식의 값으로 비교하므로 잘림이나 잘못된 메모리 읽기가 없습니다. `IsDefined(int)`는 값을 enum으로 자르지 않고 그대로 비교합니다(예: `byte` enum에 256은 false).
- **같은 값의 별칭**(`A = 1, B = 1`): 컴파일 에러 없이 생성됩니다. `ToStringFast`는 먼저 선언된 이름(`"A"`)을 반환하고, 문자열 파싱은 두 이름 모두 받습니다.
- **`[Flags]`**: 조합 값은 `ToStringFast`에서 `Enum.ToString()`과 같은 `"A, B"` 형식이 됩니다. 값마다 첫 호출에서 한 번 할당한 뒤 캐시하고, 정의된 플래그로 표현할 수 없는 값만 `fallback ?? value.ToString()`으로 떨어집니다. `To{Enum}(string)` · `TryParseFast` · `TryParse`는 `"A, B"`를 받습니다. `IsDefined`는 여전히 이름이 있는 값만 true이며, 조합 검사는 `IsValidFlags`를 씁니다.
- **문자열**: `To{Enum}(string)` · `TryParseFast`는 가장 빠른 경로로, 대소문자를 구분하고 정확히 일치해야 합니다. 공백 · 대소문자 · 숫자 문자열을 허용하려면 `ignoreCase` 오버로드나 `TryParse`를 쓰세요. 숫자 문자열은 정의된 값(`[Flags]`는 유효한 조합)일 때만 성공하며, 할당이 없습니다.
- **어트리뷰트**: 의미 분석으로 판별합니다. 어트리뷰트 타입 이름이 정확히 `E2StringAttribute`(또는 `E2String`)인 것만 대상이며, namespace는 상관없습니다. `using X = E2StringAttribute;` 같은 별칭도 인식합니다.
- **중첩 enum**: class · struct · record · interface 안의 enum을 지원합니다. `@class` 같은 키워드 멤버와 `[Obsolete]` 멤버도 지원합니다(생성 코드는 멤버 이름 대신 상수 값을 씁니다).

## 진단

| ID | 상황 | 결과 |
|---|---|---|
| `E2S001` | 제네릭 타입(`Outer<T>`) 안의 enum | 생성하지 않음 |
| `E2S002` | enum 또는 바깥 타입이 `private` · `protected` | 생성하지 않음 |
| `E2S003` | 멤버 값이 int 범위를 벗어남 (`uint` · `long` · `ulong`) | 생성함. `ToInt`는 값을 자르고 int API는 그 멤버를 인식하지 않음 |
| `E2S004` | 생성 클래스 이름이 같은 namespace에서 겹침 (예: 최상위 `Outer_E`와 중첩 `Outer.E`) | 뒤에 나온 enum은 생성하지 않음 |

## Unity에 넣기

1. `dotnet build Script/EString.csproj -c Release`
2. `EString.dll`을 Unity 프로젝트의 `Assets/` 아래에 복사합니다. `Editor` 폴더 밖에 두세요.
3. dll의 Plugin Inspector에서 다음을 설정합니다.
   - **Select platforms for plugin**: 모두 해제 (Any Platform, Editor, Standalone)
   - 에셋 라벨: **`RoslynAnalyzer`** 추가
4. 어트리뷰트는 생성기가 만들지 않습니다. Unity 쪽에 직접 정의합니다.

   ```csharp
   [System.AttributeUsage(System.AttributeTargets.Enum)]
   public sealed class E2StringAttribute : System.Attribute { }
   ```

   이 dll을 감싼 UPM 패키지(`com.hb.e2string`, `E2StringManagerWindow` 포함)를 쓰고 있다면, 어트리뷰트와 dll 배치는 패키지가 처리합니다. 단, dll 이름이 `EString.Script.Generator.dll`에서 `EString.dll`로 바뀌었으므로 패키지 안의 dll도 교체해야 합니다.
5. 스크립트 재컴파일 후 Rider에서 생성 코드를 확인할 수 있습니다: Solution 탐색기 → 어셈블리 → Source Generators.

## 주의 · 한계

- `uint` · `long` · `ulong` enum에서 int 범위를 벗어난 값은 int API(`ToInt`, `To{Enum}(int)`, `IsDefined(int)`)로 다룰 수 없습니다(`E2S003`). 이런 enum은 `ToStringFast` · 문자열 파싱 · `IsDefined(T)`를 쓰세요.
- `[Flags]` 조합 문자열 캐시는 프로세스가 끝날 때까지 유지됩니다. 서로 다른 조합 값이 아주 많이 생기는 용도라면 메모리를 확인하세요.
- `Names` · `Values`는 배열이라 외부에서 내용을 바꿀 수 있습니다. 읽기 전용이 필요하면 `AllNames` · `AllValues`를 쓰세요.
- 저장소 루트의 `bin/Release/netstandard2.0/EString.dll`은 예전 구조의 산출물입니다. Unity에 넣을 dll은 `Script/bin/Release/netstandard2.0/EString.dll`입니다.
