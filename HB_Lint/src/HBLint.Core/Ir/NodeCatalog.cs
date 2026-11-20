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
        public NodeSpec(string kind, string title, Socket? output, InputSpec[] inputs, FieldSpec[] fields)
        {
            Kind = kind;
            Title = title;
            Output = output;
            Inputs = inputs;
            Fields = fields;
        }

        public string Kind { get; }

        public string Title { get; }

        /// <summary>null이면 끝 노드 (report).</summary>
        public Socket? Output { get; }

        public InputSpec[] Inputs { get; }

        public FieldSpec[] Fields { get; }
    }

    /// <summary>
    /// 노드 종류의 유일한 원본. 검증기 · 스키마 · 분석기 실행 계획이 모두 이 표를 본다.
    /// 노드 입력에서 "scope"는 규칙 머리(시작 노드)를 가리킨다.
    /// </summary>
    internal static class NodeCatalog
    {
        public const string ScopeName = "scope";
        public const string ReportKind = "report";

        public static readonly string[] MemberUses = { "call", "read", "write" };
        public static readonly string[] NullOperators = { "?.", "??", "??=", "is null" };
        public static readonly string[] GuardPositions = { "inside", "outside" };

        private static readonly Socket[] SiteSources = { Socket.Scope, Socket.Types, Socket.Methods };

        public static readonly IReadOnlyList<NodeSpec> All = new[]
        {
            new NodeSpec("types.derived", "타입 계열", Socket.Types,
                new[] { new InputSpec("from", false, Socket.Scope, Socket.Types) },
                new[] { new FieldSpec("type", FieldType.String, true, "기준 타입 메타데이터 이름 (그 타입과 자식, 인터페이스면 구현 타입)") }),
            new NodeSpec("methods.named", "메서드 이름", Socket.Methods,
                new[] { new InputSpec("from", false, Socket.Types) },
                new[] { new FieldSpec("names", FieldType.StringArray, true, "메서드 이름 (그 타입에 선언된 것)") }),
            new NodeSpec("methods.follow", "호출 따라가기", Socket.Methods,
                new[] { new InputSpec("from", false, Socket.Methods) },
                new[] { new FieldSpec("depth", FieldType.Int, true, "같은 클래스 일반 메서드를 몇 단계까지 따라갈지") { Min = 0, Max = 2 } }),
            new NodeSpec("sites.member", "멤버 사용", Socket.Sites,
                new[] { new InputSpec("from", false, SiteSources) },
                new[]
                {
                    new FieldSpec("members", FieldType.StringArray, true, "\"타입 메타데이터 이름.멤버 이름\" (오버로드 · 오버라이드 포함)"),
                    new FieldSpec("uses", FieldType.ChoiceArray, false, "쓰임 (없으면 전부)") { Choices = MemberUses },
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
            new NodeSpec("filter.targetType", "대상 타입", Socket.Sites,
                new[] { new InputSpec("from", false, Socket.Sites) },
                new[] { new FieldSpec("type", FieldType.String, true, "사이트가 다루는 식의 정적 타입이 이 타입 계열인 것만 (null 연산자 · 멤버 사용 사이트에만 대상 타입이 있음)") }),
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
            new NodeSpec(ReportKind, "보고", null,
                new[] { new InputSpec("from", false, Socket.Sites) },
                new[]
                {
                    new FieldSpec("message", FieldType.String, true, "진단 메시지"),
                    new FieldSpec("appendPath", FieldType.Bool, false, "따라가서 닿은 곳이면 메시지 끝에 (Update → A) 경로를 붙임 (기본 true)"),
                }),
        };

        private static readonly Dictionary<string, NodeSpec> ByKind = All.ToDictionary(n => n.Kind);

        public static NodeSpec? Find(string kind) => ByKind.TryGetValue(kind, out var spec) ? spec : null;
    }
}
