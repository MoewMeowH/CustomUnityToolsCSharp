# EString — E2String 소스 생성기

`[E2String]`이 붙은 enum마다 **박싱 · 리플렉션 · 문자열 할당 없이** 동작하는 변환 확장 메서드를 컴파일 시점에 생성하는 Roslyn 소스 생성기입니다.
`Enum.ToString()` · `Enum.Parse` · `Enum.IsDefined`를 런타임에 부르지 않으려는 용도입니다.

- 대상: `netstandard2.0`, `Microsoft.CodeAnalysis.CSharp` 3.8.0 (`ISourceGenerator`)
- 호환: Unity 2020.2 이상 (2021.3 이상에서는 `UnsafeUtility` 경로 사용, Unity 6 포함)
- 산출물: `Script/bin/Release/netstandard2.0/EString.Script.Generator.dll`

## 사용 예

```csharp
namespace Game
{
    [E2String]
    public enum ItemGrade { Common, Rare, Epic }
}

// 생성된 클래스: Game.ItemGradeExtensions
string s   = ItemGrade.Rare.ToStringFast();          // "Rare" (할당 없음)
ItemGrade g = "Epic".ToItemGrade();                  // 실패하면 fallback(기본 default)
bool ok    = ItemGradeExtensions.TryParseFast("Rare", out var r);
int  i     = ItemGrade.Epic.ToInt();                 // 2
ItemGrade h = 1.ToItemGrade();                       // 정의 검사 후 변환
bool def   = ItemGradeExtensions.IsDefined(5);       // false
foreach (var v in ItemGradeExtensions.AllValues) { } // 선언 순서
```

## 생성되는 멤버

클래스 이름은 `{바깥타입_}{Enum}Extensions`입니다. enum과 같은 namespace에 생성되며, 파일 이름은 `{Full_Name}_Extensions.g.cs`입니다.

| 멤버 | 설명 |
|---|---|
| `const int Count` | 멤버 개수 |
| `string[] Names` / `IReadOnlyList<string> AllNames` | 식별자 이름 (선언 순서) |
| `T[] Values` / `IReadOnlyList<T> AllValues` | 값 (선언 순서) |
| `string ToStringFast(this T, string? fallback = null)` | switch로 문자열 반환. 정의 밖 값이면 `fallback ?? value.ToString()` |
| `T To{Enum}(this string?, T fallback = default)` | 문자열 → enum (대소문자 구분, 정확히 일치) |
| `static bool TryParseFast(string?, out T, T fallback = default)` | 안전 파싱 (확장 메서드 아님) |
| `int ToInt(this T)` / `int ToIntFast(this T)` | enum → int |
| `T To{Enum}(this int, T fallback = default)` | `IsDefined` 검사 후 변환 |
| `T To{Enum}Fast(this int)` | 검사 없이 바로 변환 |
| `static bool IsDefined(T)` / `static bool IsDefined(int)` | 정의된 값인지 검사 |

`UNITY_2021_3_OR_NEWER`이면 int 변환에 `UnsafeUtility.As`를 쓰고, 그 외에는 명시적 캐스트를 씁니다.

## Unity에 넣기

1. `dotnet build Script/EString.csproj -c Release`
2. `EString.Script.Generator.dll`을 Unity 프로젝트의 `Assets/` 아래에 복사합니다. `Editor` 폴더 밖에 두세요.
3. dll의 Plugin Inspector에서 다음을 설정합니다.
   - **Select platforms for plugin**: 모두 해제 (Any Platform, Editor, Standalone)
   - 에셋 라벨: **`RoslynAnalyzer`** 추가
4. 어트리뷰트는 생성기가 만들지 않습니다. Unity 쪽에 직접 정의합니다. 이름만 맞으면 되며, 끝이 `E2String` 또는 `E2StringAttribute`인 어트리뷰트를 인식합니다.

   ```csharp
   [System.AttributeUsage(System.AttributeTargets.Enum)]
   public sealed class E2StringAttribute : System.Attribute { }
   ```

   이 dll을 감싼 UPM 패키지(`com.hb.e2string`, `E2StringManagerWindow` 포함)를 쓰고 있다면, 어트리뷰트와 dll 배치는 패키지가 처리합니다.
5. 스크립트 재컴파일 후 Rider에서 생성 코드를 확인할 수 있습니다: Solution 탐색기 → 어셈블리 → Source Generators.

## 주의 · 한계

- **기본 형식은 `int`로 두세요.** `ToInt` · `To{Enum}(int)`는 `UnsafeUtility.As<T, int>`로 재해석합니다. `enum X : byte`, `: long` 같은 enum에서는 2021.3 이상 경로가 잘못된 값을 읽을 수 있습니다.
- **같은 값의 별칭 멤버**(`A = 1, B = 1`)가 있으면 생성된 switch에서 컴파일 에러가 납니다(중복 case).
- **`[Flags]` 조합 값**은 `ToStringFast`에서 정의 밖 값으로 취급되어 `value.ToString()`(할당 발생)으로 떨어집니다. `IsDefined`도 false입니다.
- 문자열 비교는 대소문자를 구분하고, 앞뒤 공백을 허용하지 않습니다.
- 어트리뷰트는 문법(이름)으로만 판별합니다. 이름 끝이 `E2String`인 다른 어트리뷰트도 대상으로 잡힙니다.
- `EString/bin/Release/netstandard2.0/EString.dll`은 어셈블리 이름을 바꾸기 전의 산출물입니다. 현재 Unity에 넣을 dll은 `Script/bin/...`의 `EString.Script.Generator.dll`입니다.
