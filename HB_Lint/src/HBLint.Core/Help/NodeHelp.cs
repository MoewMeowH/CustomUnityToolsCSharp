#nullable enable
using System.Collections.Generic;
using HBLint.Core.Ir;

namespace HBLint.Core.Help
{
    /// <summary>노드 · 소켓 · 용어의 쉬운 설명 (처음 보는 사람용). 편집 앱과 스키마가 쓴다 — 분석기 DLL에는 넣지 않으려고 Ir/ 밖에 둔다.</summary>
    internal sealed class HelpText
    {
        public HelpText(string help, string example)
        {
            Help = help;
            Example = example;
        }

        /// <summary>무엇을 하나 · 언제 쓰나 (2~3문장).</summary>
        public string Help { get; }

        /// <summary>한 줄 쓰임 예.</summary>
        public string Example { get; }
    }

    internal static class NodeHelp
    {
        /// <summary>규칙 시작(머리 칸) 노드.</summary>
        public static readonly HelpText Scope = new HelpText(
            "규칙 하나의 출발점입니다. 여기서 프로젝트 코드 전체를 받아 오른쪽 노드로 넘기고, 오른쪽으로 갈수록 범위가 좁아집니다. id · 심각도와, 이 규칙에서 뺄 어셈블리 · 경로(공통 설정에서 뺀 것을 다시 넣기도)를 여기서 정합니다.",
            "규칙 시작 → 선언(enum) → 이름(E로 시작 안 함) → 보고");

        /// <summary>노드 kind → 설명. 모든 노드가 있어야 한다 (시험이 확인).</summary>
        public static readonly IReadOnlyDictionary<string, HelpText> Nodes = new Dictionary<string, HelpText>
        {
            ["types.derived"] = new HelpText(
                "어떤 클래스와 그 자식 클래스만 보겠다고 범위를 좁힙니다. 인터페이스를 넣으면 그 인터페이스를 구현한 클래스가 범위입니다.",
                "UnityEngine.MonoBehaviour → MonoBehaviour를 상속한 스크립트만 검사"),
            ["methods.named"] = new HelpText(
                "범위 안 클래스에서 이름이 같은 메서드만 고릅니다. 그 메서드 몸통 안 코드만 검사하고 싶을 때 씁니다.",
                "MonoBehaviour → 메서드 이름(Update, LateUpdate) → Update · LateUpdate 안만 검사"),
            ["methods.coroutine"] = new HelpText(
                "범위 안 클래스에서 코루틴(IEnumerator · IEnumerator<T>를 돌려주는 일반 메서드)만 고릅니다. 반환형만 보므로 코루틴이 아닌 GetEnumerator도 들어갑니다.",
                "MonoBehaviour → 코루틴 → 코루틴 안의 new WaitForSeconds 찾기"),
            ["methods.follow"] = new HelpText(
                "고른 메서드가 부르는 같은 클래스의 메서드까지 따라가서 범위에 넣습니다. depth만큼 몇 단계 따라갈지 정합니다.",
                "Update → 호출 따라가기(1) → Update가 부르는 Move() 안도 검사"),
            ["sites.member"] = new HelpText(
                "특정 함수 · 속성 · 필드를 쓰는 곳을 찾습니다. 부르기 · 읽기 · 쓰기 · 비교 중 어떤 쓰임만 볼지도 고를 수 있습니다.",
                "UnityEngine.Debug.Log → Debug.Log(...)를 부른 줄마다 걸림"),
            ["sites.namespace"] = new HelpText(
                "어떤 네임스페이스(와 그 아래)의 타입 · 멤버 · using을 쓰는 곳을 찾습니다.",
                "UnityEditor → 런타임 코드에서 UnityEditor를 쓴 곳 찾기"),
            ["sites.declaredInDefine"] = new HelpText(
                "#if 디파인 안에만 선언된 것을 디파인 밖에서 쓰는 곳을 찾습니다. 빌드에서 그 디파인이 꺼지면 컴파일이 깨지는 코드를 미리 잡습니다.",
                "UNITY_EDITOR → #if UNITY_EDITOR 안에만 있는 메서드를 밖에서 부른 곳"),
            ["sites.nullOperator"] = new HelpText(
                "?. · ?? · ??= · is null을 쓴 곳을 찾습니다. Unity 오브젝트는 파괴돼도 이 연산자로는 null이 아니라서 자주 잡는 규칙입니다.",
                "null 연산자 → 대상 타입(UnityEngine.Object) → Unity 오브젝트에 쓴 ?. 찾기"),
            ["sites.new"] = new HelpText(
                "new로 객체 · 배열을 만드는 곳을 찾습니다. 매 프레임 돌면 가비지가 쌓이는 코드를 찾을 때 씁니다.",
                "Update → 객체 생성 → 대상 타입(참조 형식) → Update 안 new List 찾기"),
            ["sites.boxing"] = new HelpText(
                "값 형식(int · struct)이 object · 인터페이스로 바뀌며 힙에 복사되는(박싱) 곳을 찾습니다.",
                "Update → 박싱 변환 → Update 안 박싱 찾기"),
            ["sites.stringConcat"] = new HelpText(
                "문자열을 + · += · $\"…\"로 이어 붙이는 곳을 찾습니다. 매번 새 문자열이 만들어집니다.",
                "Update → 문자열 연결 → Update 안 \"HP: \" + hp 찾기"),
            ["sites.lambda"] = new HelpText(
                "람다(() => …)를 만드는 곳을 찾습니다. 기본은 바깥 변수를 붙잡는(캡처) 람다만 — 그런 람다는 만들 때마다 가비지가 생깁니다.",
                "Update → 람다 → Update 안 캡처 람다 찾기"),
            ["sites.declaration"] = new HelpText(
                "클래스 · enum · 메서드 같은 선언 자체를 찾습니다. static · 확장 메서드 · 접근 수준으로 좁힐 수 있고, 이름 · 자리 필터와 이어 이름 규칙 · 선언 위치 규칙을 만듭니다.",
                "선언(method, 확장 메서드) → 이름(E_로 시작, notMatching) → E_ 없는 확장 메서드 찾기"),
            ["filter.name"] = new HelpText(
                "선언의 이름으로 거릅니다. notMatching을 고르면 규칙을 어긴 이름만 남습니다. 선언(sites.declaration) 뒤에만 뜻이 있습니다.",
                "선언(enum) → 이름(정규식 ^E[A-Z], notMatching) → E로 시작 안 하는 enum"),
            ["filter.location"] = new HelpText(
                "코드가 있는 파일 · 감싸는 클래스로 거릅니다. 파일 이름이나 폴더, 클래스 이름이 바뀌면 이 칸만 고치면 됩니다. 어느 사이트 뒤에나 이을 수 있습니다.",
                "선언(enum) → 코드 자리(CommonEnum.cs · CommonEnum 클래스, outside) → 밖에 선언된 enum"),
            ["filter.targetType"] = new HelpText(
                "찾은 곳이 다루는 값의 타입으로 거릅니다. 그 타입 계열만, 또는 참조 형식(class) · 값 형식(struct)만 남깁니다.",
                "객체 생성 → 대상 타입(참조 형식) → struct 생성은 빼고 class 생성만"),
            ["filter.context"] = new HelpText(
                "반복문 안 · 람다 안에 있는 것만(또는 밖만) 남깁니다.",
                "코루틴 → 객체 생성 → 맥락(loop, inside) → 코루틴 반복문 안 new만"),
            ["filter.define"] = new HelpText(
                "#if 디파인 안에 있는 코드만(또는 밖만) 남깁니다.",
                "UNITY_EDITOR · outside → 에디터 전용 구간의 코드는 빼고 봄"),
            ["sites.union"] = new HelpText(
                "여러 노드에서 찾은 곳을 하나로 합칩니다. 같은 자리는 한 번만 알립니다.",
                "멤버 사용 A + 멤버 사용 B → 합치기 → 보고 하나"),
            ["groups.by"] = new HelpText(
                "찾은 곳을 메서드마다(또는 메서드 안 같은 대상마다) 묶습니다. 개수 노드와 함께 \"한 메서드에 N번 이상\" 규칙을 만듭니다.",
                "transform 읽기 → 묶기(methodAndTarget) → 개수(4) → 같은 transform을 4번 이상 읽는 메서드"),
            ["groups.pair"] = new HelpText(
                "두 흐름(a · b)이 같은 메서드 안에 둘 다 있는 묶음만 남깁니다.",
                "position 쓰기(a) + rotation 쓰기(b) → 짝 묶기 → SetPositionAndRotation으로 합칠 곳"),
            ["groups.count"] = new HelpText(
                "묶음 안에 찾은 곳이 min개 이상인 묶음만 남깁니다.",
                "묶기 → 개수(4) → 4번 이상인 메서드만"),
            ["report"] = new HelpText(
                "규칙의 끝입니다. 여기까지 온 곳마다 IDE · Unity 콘솔에 경고(또는 오류)를 띄웁니다. 메시지에는 어떻게 고칠지를 적습니다.",
                "\"Debug 대신 HLog를 쓰세요\""),
        };

        /// <summary>소켓(선 색) 설명.</summary>
        public static readonly IReadOnlyDictionary<Socket, string> Sockets = new Dictionary<Socket, string>
        {
            [Socket.Scope] = "규칙 시작 — 프로젝트 코드 전체",
            [Socket.Types] = "타입(클래스) 범위 — 이 클래스들 안만 본다",
            [Socket.Methods] = "메서드 범위 — 이 메서드들 안만 본다",
            [Socket.Sites] = "찾은 곳(사이트) — 코드 위치 하나하나. 필터로 거르고 보고로 알린다",
            [Socket.Groups] = "묶음 — 찾은 곳을 메서드마다 모은 것",
        };

        /// <summary>처음 보는 사람이 막히는 용어 (용어, 풀이).</summary>
        public static readonly IReadOnlyList<KeyValuePair<string, string>> Glossary = new[]
        {
            Term("읽는 법", "왼쪽(규칙 시작)에서 오른쪽(보고)으로 선을 따라가며 범위를 좁힙니다. 범위(타입 · 메서드) → 찾을 것(사이트) → 거르기(필터) → 보고."),
            Term("사이트", "규칙이 걸 수 있는 코드 위치 하나 — 함수를 부른 자리, new를 쓴 자리, 선언 이름 자리 등. 경고는 이 위치에 뜹니다."),
            Term("타입 전체 이름", "네임스페이스까지 쓴 이름 (예 UnityEngine.MonoBehaviour). 중첩 클래스는 Outer+Inner, 제네릭은 List`1."),
            Term("멤버", "클래스 안의 메서드 · 속성 · 필드. \"타입 전체 이름.멤버 이름\"으로 씁니다 (예 UnityEngine.Debug.Log)."),
            Term("디파인", "#if 뒤에 쓰는 기호 (예 UNITY_EDITOR · DEVELOPMENT_BUILD). 빌드마다 켜지고 꺼집니다."),
            Term("필터", "Sites를 받아 조건에 맞는 것만 다시 Sites로 내보내는 노드. 여러 개를 이어 붙일 수 있습니다."),
            Term("inside · outside", "필터가 그 조건 안에 있는 것만 남길지(inside), 밖에 있는 것만 남길지(outside)."),
            Term("제외 · 다시 넣기", "공통 설정(도구줄 \"공통 설정\")에서 뺀 어셈블리 · 경로는 모든 규칙에서 검사하지 않습니다. 한 규칙에서만 다시 보려면 규칙 시작의 includeAssemblies · includePaths에 적습니다."),
        };

        private static KeyValuePair<string, string> Term(string term, string meaning) => new KeyValuePair<string, string>(term, meaning);

        public static HelpText? Find(string kind) => Nodes.TryGetValue(kind, out var help) ? help : null;
    }
}
