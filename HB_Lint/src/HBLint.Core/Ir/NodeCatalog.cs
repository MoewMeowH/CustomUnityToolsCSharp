#nullable enable
using System.Collections.Generic;
using System.Linq;

namespace HBLint.Core.Ir
{
    /// <summary>소켓(노드 사이 선의 종류). 왼쪽에서 오른쪽으로 갈수록 범위가 좁아진다. Report는 끝이라 내보내는 소켓이 없다.</summary>
    internal enum Socket
    {
        Scope,
        Types,
        Methods,
        Sites,
        /// <summary>메서드(와 대상)마다 묶은 사이트들.</summary>
        Groups,
    }

    internal enum FieldType
    {
        String,
        StringArray,
        Int,
        Bool,
        /// <summary>Choices 중 하나.</summary>
        Choice,
        /// <summary>Choices의 부분집합 (배열).</summary>
        ChoiceArray,
    }

    /// <summary>노드 안 입력칸.</summary>
    internal sealed class FieldSpec
    {
        public FieldSpec(string name, FieldType type, bool required, string description)
        {
            Name = name;
            Type = type;
            Required = required;
            Description = description;
        }

        public string Name { get; }

        public FieldType Type { get; }

        public bool Required { get; }

        public string Description { get; }

        public string[] Choices { get; set; } = new string[0];

        public int Min { get; set; } = int.MinValue;

        public int Max { get; set; } = int.MaxValue;
    }

    /// <summary>입력 소켓. Many면 노드 이름 배열(둘 이상)을 받는다.</summary>
    internal sealed class InputSpec
    {
        public InputSpec(string name, bool many, params Socket[] accepts)
        {
            Name = name;
            Many = many;
            Accepts = accepts;
        }

        public string Name { get; }

        public bool Many { get; }

        public Socket[] Accepts { get; }
    }

    internal sealed class NodeSpec
    {
        public NodeSpec(string kind, string title, Socket? output, InputSpec[] inputs, FieldSpec[] fields, string[]? requireOneOf = null)
        {
            Kind = kind;
            Title = title;
            Output = output;
            Inputs = inputs;
            Fields = fields;
            RequireOneOf = requireOneOf ?? new string[0];
        }

        public string Kind { get; }

        public string Title { get; }

        /// <summary>null이면 끝 노드 (report).</summary>
        public Socket? Output { get; }

        public InputSpec[] Inputs { get; }

        public FieldSpec[] Fields { get; }

        /// <summary>이 칸들 가운데 하나 이상 있어야 한다 (칸마다는 선택).</summary>
        public string[] RequireOneOf { get; }
    }

    /// <summary>
    /// 노드 종류의 유일한 원본. 검증기 · 스키마 · 분석기 실행 계획이 모두 이 표를 본다.
    /// 노드 입력에서 "scope"는 규칙 머리(시작 노드)를 가리킨다.
    /// </summary>
    internal static class NodeCatalog
    {
        public const string ScopeName = "scope";
        public const string ReportKind = "report";

        public static readonly string[] MemberUses = { "call", "read", "write", "compare" };
        public static readonly string[] NullOperators = { "?.", "??", "??=", "is null" };
        public static readonly string[] GuardPositions = { "inside", "outside" };
        public static readonly string[] TypeCategories = { "reference", "value" };
        public static readonly string[] Contexts = { "loop", "lambda" };
        public static readonly string[] GroupKeys = { "method", "methodAndTarget" };

        /// <summary>sites.member의 members에서 "타입.*" = 그 타입에 선언된 멤버 전부.</summary>
        public const string AllMembers = "*";

        private static readonly Socket[] SiteSources = { Socket.Scope, Socket.Types, Socket.Methods };

        public static readonly IReadOnlyList<NodeSpec> All = new[]
        {
            new NodeSpec("types.derived", "타입 계열", Socket.Types,
                new[] { new InputSpec("from", false, Socket.Scope, Socket.Types) },
                new[] { new FieldSpec("type", FieldType.String, true, "기준 타입 메타데이터 이름 (그 타입과 자식, 인터페이스면 구현 타입)") }),
            new NodeSpec("methods.named", "메서드 이름", Socket.Methods,
                new[] { new InputSpec("from", false, Socket.Types) },
                new[] { new FieldSpec("names", FieldType.StringArray, true, "메서드 이름 (그 타입에 선언된 것)") }),
            // 반환형만 본다 — IEnumerable 구현의 GetEnumerator 같은 코루틴 아닌 메서드도 들어간다
            new NodeSpec("methods.coroutine", "코루틴 (IEnumerator · IEnumerator<T> 반환 일반 메서드)", Socket.Methods,
                new[] { new InputSpec("from", false, Socket.Types) },
                new FieldSpec[0]),
            new NodeSpec("methods.follow", "호출 따라가기", Socket.Methods,
                new[] { new InputSpec("from", false, Socket.Methods) },
                new[] { new FieldSpec("depth", FieldType.Int, true, "같은 클래스 일반 메서드를 몇 단계까지 따라갈지") { Min = 0, Max = 2 } }),
            new NodeSpec("sites.member", "멤버 사용", Socket.Sites,
                new[] { new InputSpec("from", false, SiteSources) },
                new[]
                {
                    new FieldSpec("members", FieldType.StringArray, true, "\"타입 메타데이터 이름.멤버 이름\" (오버로드 · 오버라이드 포함) 또는 \"타입 메타데이터 이름.*\" (그 타입에 선언된 메서드 · 속성 · 필드 전부)"),
                    new FieldSpec("uses", FieldType.ChoiceArray, false, "쓰임 (없으면 전부). compare = == · != 의 한쪽 (비교도 읽기라서 read를 고르면 비교도 들어감)") { Choices = MemberUses },
                }),
            new NodeSpec("sites.namespace", "네임스페이스 사용", Socket.Sites,
                new[] { new InputSpec("from", false, SiteSources) },
                new[]
                {
                    new FieldSpec("namespaces", FieldType.StringArray, true, "네임스페이스 (하위 포함)"),
                    new FieldSpec("usings", FieldType.Bool, false, "using 지시문도 (기본 true)"),
                }),
            new NodeSpec("sites.declaredInDefine", "디파인 안 선언 사용", Socket.Sites,
                new[] { new InputSpec("from", false, SiteSources) },
                new[] { new FieldSpec("define", FieldType.String, true, "이 디파인 안에만 선언된 것(같은 어셈블리)을 쓰는 곳") }),
            new NodeSpec("sites.nullOperator", "null 연산자", Socket.Sites,
                new[] { new InputSpec("from", false, SiteSources) },
                new[] { new FieldSpec("operators", FieldType.ChoiceArray, false, "연산자 (없으면 전부, is null은 is not null · 복합 패턴 포함)") { Choices = NullOperators } }),
            new NodeSpec("sites.new", "객체 생성", Socket.Sites,
                new[] { new InputSpec("from", false, SiteSources) },
                new FieldSpec[0]),
            new NodeSpec("sites.boxing", "박싱 변환", Socket.Sites,
                new[] { new InputSpec("from", false, SiteSources) },
                new FieldSpec[0]),
            new NodeSpec("sites.stringConcat", "문자열 연결 · 보간", Socket.Sites,
                new[] { new InputSpec("from", false, SiteSources) },
                new FieldSpec[0]),
            new NodeSpec("sites.lambda", "람다", Socket.Sites,
                new[] { new InputSpec("from", false, SiteSources) },
                new[] { new FieldSpec("capturingOnly", FieldType.Bool, false, "지역 · 매개변수 · this를 캡처하는 람다만 (기본 true)") }),
            new NodeSpec("filter.targetType", "대상 타입", Socket.Sites,
                new[] { new InputSpec("from", false, Socket.Sites) },
                new[]
                {
                    new FieldSpec("type", FieldType.String, false, "사이트가 다루는 식의 정적 타입이 이 타입 계열인 것만 (대상 타입이 있는 사이트: null 연산자 · 멤버 사용 · 객체 생성 · 박싱)"),
                    new FieldSpec("category", FieldType.Choice, false, "대상 타입이 참조 형식(reference) · 값 형식(value)인 것만 (타입 매개변수는 class · struct 제약으로)") { Choices = TypeCategories },
                },
                requireOneOf: new[] { "type", "category" }),
            new NodeSpec("filter.context", "맥락", Socket.Sites,
                new[] { new InputSpec("from", false, Socket.Sites) },
                new[]
                {
                    new FieldSpec("context", FieldType.Choice, true, "loop = 반복문 본문 · 조건 · 증감식, lambda = 람다 · 무명 메서드 몸통") { Choices = Contexts },
                    new FieldSpec("where", FieldType.Choice, true, "그 맥락 안(inside) · 밖(outside)") { Choices = GuardPositions },
                }),
            new NodeSpec("filter.define", "디파인 가드", Socket.Sites,
                new[] { new InputSpec("from", false, Socket.Sites) },
                new[]
                {
                    new FieldSpec("define", FieldType.String, true, "디파인"),
                    new FieldSpec("where", FieldType.Choice, true, "그 디파인이 없으면 꺼지는 구간 안(inside) · 밖(outside)") { Choices = GuardPositions },
                }),
            new NodeSpec("sites.union", "합치기", Socket.Sites,
                new[] { new InputSpec("from", true, Socket.Sites) },
                new FieldSpec[0]),
            new NodeSpec("groups.by", "묶기", Socket.Groups,
                new[] { new InputSpec("from", false, Socket.Sites) },
                new[] { new FieldSpec("key", FieldType.Choice, true, "method = 같은 메서드 · 접근자 · 람다 몸통, methodAndTarget = 그 안에서 같은 대상(멤버 사용의 인스턴스)") { Choices = GroupKeys } }),
            new NodeSpec("groups.pair", "짝 묶기", Socket.Groups,
                new[] { new InputSpec("a", false, Socket.Sites), new InputSpec("b", false, Socket.Sites) },
                new[] { new FieldSpec("key", FieldType.Choice, true, "groups.by와 같은 기준. a · b가 둘 다 있는 묶음만") { Choices = GroupKeys } }),
            new NodeSpec("groups.count", "개수", Socket.Groups,
                new[] { new InputSpec("from", false, Socket.Groups) },
                new[] { new FieldSpec("min", FieldType.Int, true, "사이트가 이 수 이상인 묶음만") { Min = 2, Max = 100 } }),
            new NodeSpec(ReportKind, "보고", null,
                new[] { new InputSpec("from", false, Socket.Sites, Socket.Groups) },
                new[]
                {
                    new FieldSpec("message", FieldType.String, true, "진단 메시지 (Groups를 받으면 묶음마다 하나 — 첫 사이트 위치, 나머지는 추가 위치)"),
                    new FieldSpec("appendPath", FieldType.Bool, false, "따라가서 닿은 곳이면 메시지 끝에 (Update → A) 경로를 붙임 (기본 true)"),
                }),
        };

        private static readonly Dictionary<string, NodeSpec> ByKind = All.ToDictionary(n => n.Kind);

        public static NodeSpec? Find(string kind) => ByKind.TryGetValue(kind, out var spec) ? spec : null;
    }
}
