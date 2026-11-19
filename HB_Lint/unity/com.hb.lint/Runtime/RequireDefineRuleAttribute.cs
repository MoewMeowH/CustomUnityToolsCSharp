using System;

namespace HBLint
{
    /// <summary>
    /// 규칙 CLL0003: <see cref="Define"/>(예 UNITY_EDITOR)로 감싸지 않은 코드에서 <see cref="Namespaces"/>의 타입 · 멤버 · using을 쓰거나,
    /// 같은 어셈블리에서 그 디파인 안에 선언된 것(<see cref="DeclaredInDefine"/>)을 쓰면 진단한다 — 그 디파인이 없는 빌드가 깨지므로.
    /// <see cref="IgnoreAssemblies"/>에 맞는 어셈블리는 예외. 같은 #if 묶음의 #else 쪽에 같은 이름이 있으면 디파인 안에만 선언된 것으로 보지 않는다.
    /// </summary>
    /// <example>
    /// [assembly: HBLint.RequireDefineRule("UNITY_EDITOR", "#if UNITY_EDITOR 안에서만 쓰세요", Namespaces = new[] { "UnityEditor" }, DeclaredInDefine = true)]
    /// </example>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class RequireDefineRuleAttribute : Attribute
    {
        /// <param name="define">전처리 기호 (예 UNITY_EDITOR).</param>
        /// <param name="message">진단 메시지 그대로.</param>
        public RequireDefineRuleAttribute(string define, string message)
        {
            Define = define;
            Message = message;
        }

        public string Define { get; }

        public string Message { get; }

        /// <summary>이 네임스페이스와 그 아래의 타입 · 멤버를 디파인 밖에서 쓰면 진단 (예 "UnityEditor").</summary>
        public string[] Namespaces { get; set; } = Array.Empty<string>();

        /// <summary>같은 어셈블리에서 #if define 안에 선언된 메서드 · 필드 · 속성 · 타입을 디파인 밖에서 쓰면 진단.</summary>
        public bool DeclaredInDefine { get; set; }

        /// <summary>검사하지 않는 어셈블리 이름 패턴 (* 와일드카드, 예 "*.Editor").</summary>
        public string[] IgnoreAssemblies { get; set; } = Array.Empty<string>();

        /// <summary>검사하는 어셈블리가 이 이름의 어셈블리들을 모두 참조할 때만 적용한다.</summary>
        public string[] Requires { get; set; } = Array.Empty<string>();

        public LintSeverity Severity { get; set; } = LintSeverity.Warning;
    }
}
