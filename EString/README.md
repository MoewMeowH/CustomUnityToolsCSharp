# EString — E2String 소스 생성기

`[E2String]`이 붙은 enum마다 **박싱 · 리플렉션 · 문자열 할당 없이** 동작하는 변환 확장 메서드를 컴파일 시점에 생성하는 Roslyn 소스 생성기입니다.
`Enum.ToString()` · `Enum.Parse` · `Enum.IsDefined`를 런타임에 부르지 않으려는 용도입니다.

- 대상: `netstandard2.0`, `Microsoft.CodeAnalysis.CSharp` 4.3.0 (`IIncrementalGenerator`)
- 호환: **Unity 6.3 이상**. Unity 6은 생성기에 Roslyn 4.3을 요구합니다. Unity 2022 이하에서는 dll이 로드되지 않습니다. 생성 코드는 C# 8 문법만 쓰며 Unity API에 의존하지 않습니다.
- 증분 생성: enum마다 분석 결과를 캐시해 바뀐 enum만 다시 생성합니다 (IDE에서 입력할 때 가벼움). 같은 파일 안의 enum은 위치가 바뀌므로 함께 다시 생성됩니다.
- 산출물: `Script/bin/Release/netstandard2.0/EString.dll`

## 옵션 (`E2StringOptions`)

어트리뷰트에 `E2StringOptions`를 넘겨 생성할 멤버를 고릅니다. 어트리뷰트와 옵션 enum은 생성기가 만들지 않으며, 클라 · 서버 공용 코드에 정의합니다(아래 "Unity에 넣기" 참고).

```csharp
[Flags] public enum E2StringOptions
{
    ParseExact    = 0,       // ToStringFast, To{Enum}Fast(string) — 항상 생성
    IntConversion = 1 << 1,  // ToIntFast, To{Enum}Fast(int), IsDefinedFast (+ [Flags]면 IsValidFlagsFast)
    Collections   = 1 << 2,  // Values
    All           = ~0,
}
```

- 옵션을 생략하면(`[E2String]`) **`ParseExact`**로 처리합니다. 문자열 변환만 생성되며 가장 흔한 용도입니다.
- 생성자 인자(`[E2String(E2StringOptions.Collections)]`)와 이름 있는 인자(`Options = ...`) 둘 다 읽습니다. 값만 같으면 되므로 옵션 enum의 namespace는 상관없습니다.
- `ParseExact`는 0이라 항상 포함됩니다. 문자열 파싱은 대소문자까지 정확히 일치해야 하며, 대소문자 무시 · 공백 무시 · 숫자 문자열은 받지 않습니다.

## 사용 예

```csharp
namespace Game
{
    [E2String(E2StringOptions.All)]
    public enum ItemGrade { Common, Rare, Epic }

    [E2String]                                    // ParseExact: 문자열 변환만
    public enum Region { Kr, Us }

    [E2String(E2StringOptions.All), System.Flags]
    public enum Perm { None = 0, Read = 1, Write = 2, Exec = 4 }
}

// 생성된 클래스: Game.ItemGradeExtensions
string s    = ItemGrade.Rare.ToStringFast();             // "Rare" (할당 없음)
string n    = ((ItemGrade)9).ToStringFast();             // "not defined ItemGrade"
ItemGrade g = "Epic".ToItemGradeFast();                  // 정확히 일치. 실패하면 fallback(기본 default)
int  i      = ItemGrade.Epic.ToIntFast();                // 2           (IntConversion)
ItemGrade k = 1.ToItemGradeFast();                       // 정의 검사 후 변환 (IntConversion)
bool def    = ItemGradeExtensions.IsDefinedFast(5);      // false       (IntConversion)
foreach (var v in ItemGradeExtensions.Values) { }        // 선언 순서   (Collections)

// [Flags]
string p  = (Perm.Read | Perm.Exec).ToStringFast();      // "Read, Exec" (값마다 첫 호출에서만 할당 후 캐시)
Perm   q  = "Read, Exec".ToPermFast();                   // 조합 파싱
bool   vf = PermExtensions.IsValidFlagsFast(5);          // true        (IntConversion)
Perm   w  = 5.ToPermFast();                              // Read | Exec (IntConversion)
```

## 생성되는 멤버

클래스 이름은 `{바깥타입_}{Enum}Extensions`입니다. enum과 같은 namespace에 생성되며, 파일 이름은 `{Namespace}.{클래스 이름}.g.cs`입니다.
클래스 접근성은 enum을 따릅니다. enum과 바깥 타입이 모두 `public`이면 `public`이고, 그 외(`internal` 등)에는 `internal`입니다.
메서드 이름에는 모두 `Fast`를 붙여 .NET 기본 API(`ToString`, `Enum.IsDefined` 등)와 구분합니다.

| 옵션 | 멤버 | 설명 |
|---|---|---|
| ParseExact | `string ToStringFast(this T, string fallback = "not defined {Enum}")` | switch로 문자열 반환. 정의 밖 값이면 `fallback` |
| ParseExact | `T To{Enum}Fast(this string?, T fallback = default)` | 문자열 → enum. 이름이 대소문자까지 정확히 일치해야 함(공백 · 숫자 문자열 불허) |
| IntConversion | `int ToIntFast(this T)` | enum → int (명시적 캐스트) |
| IntConversion | `T To{Enum}Fast(this int, T fallback = default)` | 정의 검사 후 변환 (`[Flags]`는 `IsValidFlagsFast` 검사) |
| IntConversion | `static bool IsDefinedFast(T)` / `static bool IsDefinedFast(int)` | 이름이 정의된 값인지 검사 |
| IntConversion | `static bool IsValidFlagsFast(T)` / `static bool IsValidFlagsFast(int)` | `[Flags]` 전용. 정의된 플래그들의 조합인지 검사 (0 포함) |
| Collections | `static readonly T[] Values` | 값 (선언 순서, 별칭 포함) |

### 동작 규칙

- **기본 형식**: `byte` · `sbyte` · `short` · `ushort` · `int` · `uint` · `long` · `ulong` 모두 지원합니다. switch는 기본 형식의 값으로 비교하므로 잘림이나 잘못된 메모리 읽기가 없습니다. `IsDefinedFast(int)`는 값을 enum으로 자르지 않고 그대로 비교합니다(예: `byte` enum에 256은 false).
- **같은 값의 별칭**(`A = 1, B = 1`): 컴파일 에러 없이 생성됩니다. `ToStringFast`는 먼저 선언된 이름(`"A"`)을 반환하고, 문자열 파싱은 두 이름 모두 받습니다.
- **`[Flags]`**: 조합 값은 `ToStringFast`에서 `Enum.ToString()`과 같은 `"A, B"` 형식이 됩니다. 값마다 첫 호출에서 한 번 할당한 뒤 캐시하고, 정의된 플래그로 표현할 수 없는 값만 `fallback`으로 떨어집니다. `To{Enum}Fast(string)`은 `"A, B"`를 받으며, 쉼표 주변 공백은 무시합니다. `IsDefinedFast`는 여전히 이름이 있는 값만 true이며, 조합 검사는 `IsValidFlagsFast`를 씁니다.
- **문자열**: string switch로 비교하며 할당이 없습니다. 대소문자 무시 옵션은 Unity 벤치마크에서 오히려 느려 제거했습니다.
- **어트리뷰트**: 의미 분석으로 판별합니다. 어트리뷰트 타입 이름이 정확히 `E2StringAttribute`(또는 `E2String`)인 것만 대상이며, namespace는 상관없습니다. `using X = E2StringAttribute;` 같은 별칭도 인식합니다.
- **중첩 enum**: class · struct · record · interface 안의 enum을 지원합니다. `@class` 같은 키워드 멤버와 `[Obsolete]` 멤버도 지원합니다(생성 코드는 멤버 이름 대신 상수 값을 씁니다).

## 이전 버전에서 바뀐 점

| 이전 | 현재 |
|---|---|
| 옵션 없음 (모든 멤버 생성) | `E2StringOptions`로 선택. 생략하면 `ParseExact` (문자열 변환만) |
| `ToStringFast(fallback = null)` → `fallback ?? value.ToString()` | `ToStringFast(fallback = "not defined {Enum}")` → `fallback` |
| `To{Enum}(string, T fallback)` / `To{Enum}(string, bool ignoreCase, T fallback)` | `To{Enum}Fast(string, T fallback = default)` 하나로 통합 |
| `To{Enum}Fast(string, ignoreCase: true)`의 대소문자 무시 | 제거 (Unity 벤치마크에서 더 느림) |
| `To{Enum}(string, ignoreCase)`의 앞뒤 공백 무시 · 숫자 문자열 허용 | 제거 (정확히 일치만) |
| `TryParseFast` / `TryParse` | 제거 |
| `ToInt` / `ToIntFast` | `ToIntFast`만 유지 |
| `To{Enum}(int, T fallback)` (검사) / `To{Enum}Fast(int)` (검사 없음) | `To{Enum}Fast(int, T fallback)` (검사)만 유지 |
| `IsDefined` / `IsValidFlags` | `IsDefinedFast` / `IsValidFlagsFast` |
| `Count`, `Names`, `AllNames`, `AllValues` | 제거 (`Values`만 유지) |
| `E2S003` 항상 보고 | `IntConversion`일 때만 보고 |

## 진단

| ID | 상황 | 결과 |
|---|---|---|
| `E2S001` | 제네릭 타입(`Outer<T>`) 안의 enum | 생성하지 않음 |
| `E2S002` | enum 또는 바깥 타입이 `private` · `protected` | 생성하지 않음 |
| `E2S003` | `IntConversion`인데 멤버 값이 int 범위를 벗어남 (`uint` · `long` · `ulong`) | 생성함. `ToIntFast`는 값을 자르고 int API는 그 멤버를 인식하지 않음 |
| `E2S004` | 생성 클래스 이름이 같은 namespace에서 겹침 (예: 최상위 `Outer_E`와 중첩 `Outer.E`) | 뒤에 나온 enum은 생성하지 않음 |

## Unity에 넣기

1. `dotnet build Script/EString.csproj -c Release`
2. `EString.dll`을 Unity 프로젝트의 `Assets/` 아래에 복사합니다. `Editor` 폴더 밖에 두세요.
3. dll의 Plugin Inspector에서 다음을 설정합니다.
   - **Select platforms for plugin**: 모두 해제 (Any Platform, Editor, Standalone)
   - 에셋 라벨: **`RoslynAnalyzer`** 추가
4. 어트리뷰트와 `E2StringOptions`는 생성기가 만들지 않습니다. 클라 · 서버 공용 코드에 정의합니다. 생성기는 생성자 첫 인자 또는 `Options` 이름 인자를 읽습니다.

   ```csharp
   [System.AttributeUsage(System.AttributeTargets.Enum)]
   public sealed class E2StringAttribute : System.Attribute
   {
       public E2StringAttribute(E2StringOptions options = E2StringOptions.ParseExact) { Options = options; }
       public E2StringOptions Options { get; }
   }
   ```

   이 dll을 감싼 UPM 패키지(`com.hb.e2string`, `E2StringManagerWindow` 포함)를 쓰고 있다면, 어트리뷰트와 dll 배치는 패키지가 처리합니다. 단, dll 이름이 `EString.Script.Generator.dll`에서 `EString.dll`로 바뀌었으므로 패키지 안의 dll도 교체해야 합니다.
5. 스크립트 재컴파일 후 Rider에서 생성 코드를 확인할 수 있습니다: Solution 탐색기 → 어셈블리 → Source Generators.

## 주의 · 한계

- `uint` · `long` · `ulong` enum에서 int 범위를 벗어난 값은 int API(`ToIntFast`, `To{Enum}Fast(int)`, `IsDefinedFast(int)`)로 다룰 수 없습니다(`E2S003`). 이런 enum은 `ToStringFast` · 문자열 파싱 · `IsDefinedFast(T)`를 쓰세요.
- `[Flags]` 조합 문자열 캐시는 프로세스가 끝날 때까지 유지됩니다. 서로 다른 조합 값이 아주 많이 생기는 용도라면 메모리를 확인하세요.
- `Values`는 배열이라 외부에서 내용을 바꿀 수 있습니다. 수정하지 마세요.
- 저장소 루트의 `bin/Release/netstandard2.0/EString.dll`은 예전 구조의 산출물입니다. Unity에 넣을 dll은 `Script/bin/Release/netstandard2.0/EString.dll`입니다.
