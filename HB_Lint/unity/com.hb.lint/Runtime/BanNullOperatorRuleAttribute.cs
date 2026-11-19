using System;

namespace HBLint
{
    /// <summary>
    /// 규칙 CLL0001: 정적 타입이 <see cref="Type"/>이거나 그 자식인 식에 ?. · ?? · ??= · is null · is not null을 쓰면 진단한다.
    /// 규칙 어셈블리(이 어트리뷰트를 붙인 어셈블리)를 참조하는 어셈블리와 그 어셈블리 자신을 검사한다.
    /// </summary>
    /// <example>
    /// [assembly: HBLint.BanNullOperatorRule("UnityEngine.Object", "'== null'로 확인하세요", Requires = new[] { "UnityEngine.CoreModule" })]
    /// </example>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class BanNullOperatorRuleAttribute : Attribute
    {
        /// <param name="type">대상 타입 메타데이터 이름 (예 "UnityEngine.Object", 중첩은 "Outer+Inner"). typeof 대신 문자열이라 규칙 어셈블리가 그 타입의 어셈블리를 참조하지 않아도 된다.</param>
        /// <param name="message">진단 메시지 그대로.</param>
        public BanNullOperatorRuleAttribute([TypeName] string type, string message)
        {
            Type = type;
            Message = message;
        }

        public string Type { get; }

        public string Message { get; }

        /// <summary>검사하는 어셈블리가 이 이름의 어셈블리들을 모두 참조할 때만 적용한다. 하나라도 없으면 조용히 건너뛴다.</summary>
        public string[] Requires { get; set; } = Array.Empty<string>();

        public LintSeverity Severity { get; set; } = LintSeverity.Warning;
    }
}
