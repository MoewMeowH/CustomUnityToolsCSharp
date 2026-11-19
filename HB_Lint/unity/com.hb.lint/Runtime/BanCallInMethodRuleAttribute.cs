using System;

namespace HBLint
{
    /// <summary>
    /// 규칙 CLL0002: <see cref="OwnerType"/> 계열 클래스의 <see cref="Methods"/>(예 Update) 안에서 <see cref="Members"/>를 쓰면 진단한다.
    /// 멤버는 그 이름의 오버로드 · 제네릭 전부와 그것을 오버라이드한 멤버까지 맞는다. 메서드 안 로컬 함수는 포함, 람다 안은 제외.
    /// </summary>
    /// <example>
    /// [assembly: HBLint.BanCallInMethodRule(new[] { "Update" }, new[] { "UnityEngine.Component.GetComponent" }, "UnityEngine.MonoBehaviour", "Update에서 GetComponent를 부르지 마세요")]
    /// </example>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class BanCallInMethodRuleAttribute : Attribute
    {
        /// <param name="methods">금지할 자리인 메서드 이름들 (예 Update · LateUpdate).</param>
        /// <param name="members">금지할 멤버들 — "타입 메타데이터 이름.멤버 이름" (메서드 · 속성 · 필드).</param>
        /// <param name="ownerType">이 타입이거나 자식인 클래스의 메서드만 본다.</param>
        /// <param name="message">진단 메시지 그대로.</param>
        public BanCallInMethodRuleAttribute(string[] methods, [MemberName] string[] members, [TypeName] string ownerType, string message)
        {
            Methods = methods;
            Members = members;
            OwnerType = ownerType;
            Message = message;
        }

        public string[] Methods { get; }

        public string[] Members { get; }

        public string OwnerType { get; }

        public string Message { get; }

        /// <summary>0 = 그 메서드 본문만, 1 · 2 = 거기서 부르는 같은 클래스 메서드를 그만큼 따라간다 (2를 넘으면 2).</summary>
        public int FollowDepth { get; set; }

        /// <summary>검사하는 어셈블리가 이 이름의 어셈블리들을 모두 참조할 때만 적용한다.</summary>
        public string[] Requires { get; set; } = Array.Empty<string>();

        public LintSeverity Severity { get; set; } = LintSeverity.Warning;
    }
}
