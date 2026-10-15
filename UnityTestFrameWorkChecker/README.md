# UnityTestFrameWorkChecker — HB_TestFrameworkHelper

인스펙터 슬롯 필드에 붙이는 `[NeedSlotNullCheck]`가 **실제로 널 검사가 가능한 필드에만 쓰였는지** 컴파일 단계에서 확인하는 Roslyn 분석기와 코드 수정(Code Fix)입니다.
런타임 널 검사나 테스트를 돌리기 전에, 직렬화되지 않거나 참조형 슬롯이 아닌 필드를 미리 걸러 냅니다.

- 대상: `netstandard2.0`, `Microsoft.CodeAnalysis.CSharp` + `Workspaces` 3.8.0
- 소스: `HB_TestFrameworkHelper/Analyzers`, `HB_TestFrameworkHelper/CodeFixes`
- 산출물: `HB_TestFrameworkHelper/bin/Release/netstandard2.0/HB_TestFrameworkHelper.dll`

## 진단 규칙 (분류 `HB_CodingConvention`)

| ID | 심각도 | 조건 | 코드 수정 |
|---|---|---|---|
| `HB_NeedSlotNullCheckInvalidType` | **Error** | `[NeedSlotNullCheck]` 필드의 타입이 `UnityEngine.Object` 계열이 아님 | 없음 |
| `HB_NeedSlotNullCheckSerializeField` | Warning | `[NeedSlotNullCheck]`가 붙은 **private** 필드에 `[SerializeField]`가 없음 | `[SerializeField]` 추가 |

타입 검사에서 허용하는 것:
- `UnityEngine.Object`를 상속하는 타입: `Component`, `MonoBehaviour`, `GameObject`, `ScriptableObject`, `Transform` 등
- 위 타입의 배열: `GameObject[]`
- 위 타입의 제네릭 컬렉션: `List<Transform>`

타입 검사에서 걸리면 `[SerializeField]` 검사는 건너뜁니다. public 필드는 이미 직렬화되므로 `[SerializeField]` 검사 대상이 아닙니다.

```csharp
public class Slots : MonoBehaviour
{
    [NeedSlotNullCheck] private Transform _root;            // ⚠ HB_NeedSlotNullCheckSerializeField → 빠른 수정
    [NeedSlotNullCheck, SerializeField] private Image _icon; // OK
    [NeedSlotNullCheck] public List<GameObject> Items;       // OK
    [NeedSlotNullCheck, SerializeField] private int _count;  // ⛔ HB_NeedSlotNullCheckInvalidType
}
```

**코드 수정** (Rider `Alt+Enter`): 필드 맨 앞에 `[SerializeField]`를 별도 어트리뷰트 목록으로 넣습니다. 파일에 `using UnityEngine;`이 없으면 그것도 추가합니다. Fix All(파일 · 프로젝트 · 솔루션 단위)을 지원합니다.

## 어트리뷰트

`[NeedSlotNullCheck]` 어트리뷰트와, 그 필드들을 실제로 널 검사하는 쪽(테스트 · 에디터 도구)은 **Unity 프로젝트에 있습니다.** 이 저장소에는 분석기만 있습니다.
분석기는 이름(`NeedSlotNullCheck` / `NeedSlotNullCheckAttribute`)으로만 판별하므로 namespace는 자유입니다.

```csharp
[System.AttributeUsage(System.AttributeTargets.Field)]
public sealed class NeedSlotNullCheckAttribute : System.Attribute { }
```

## 빌드 · Unity에 넣기

1. `dotnet build HB_TestFrameworkHelper/HB_TestFrameworkHelper.csproj -c Release`
   또는 Rider에서 `HB_TestFrameworkHelper.sln`을 열고 Release로 빌드합니다.
2. `HB_TestFrameworkHelper.dll`을 Unity 프로젝트의 `Assets/` 아래(`Editor` 폴더 밖)에 복사합니다.
3. dll의 Plugin Inspector에서 다음을 설정합니다.
   - 플랫폼: 모두 해제
   - 에셋 라벨: **`RoslynAnalyzer`** 추가
4. Unity가 재컴파일하면 Console에 경고/에러가 뜨고, Rider에서도 같은 진단이 밑줄로 표시됩니다.
   - 코드 수정은 IDE(Rider/VS)에서만 동작합니다. Unity Console에는 진단만 나옵니다.
   - `bin/` 안의 언어별 폴더(`ko`, `ja`, …)는 Roslyn 리소스이므로 Unity에 복사하지 않아도 됩니다.

특정 어셈블리에만 적용하려면 dll을 해당 `.asmdef` 폴더 아래에 둡니다. 그러면 그 어셈블리와, 그 어셈블리를 참조하는 어셈블리에만 적용됩니다 (Unity 2020.2 이상).

## 주의 · 한계

- `[NonSerialized]` public 필드는 검사하지 않습니다. 직렬화되지 않아도 경고가 나오지 않습니다.
- 제네릭 판별은 "`List` 또는 `IEnumerable` 구현 + 첫 번째 타입 인자"로 합니다. 그래서 `Dictionary<GameObject, int>`처럼 Unity가 직렬화하지 않는 타입도 통과합니다.
- 진단 메시지는 한국어 고정입니다. 지역화 리소스는 쓰지 않으며, `RS2008`(릴리스 추적)은 `NoWarn`으로 꺼 두었습니다.
