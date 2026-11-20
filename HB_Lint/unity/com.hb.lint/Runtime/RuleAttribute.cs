using System;

namespace HBLint
{
    /// <summary>
    /// 규칙 하나 — 규칙 IR(JSON 노드 그래프) 원문. 손으로 쓰지 않고 hblint-gen이 *.hblint.json에서 만든 LintRules.g.cs에 들어간다.
    /// 규칙 어셈블리(이 어트리뷰트를 붙인 어셈블리)를 참조하는 어셈블리와 그 어셈블리 자신을 검사한다.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class RuleAttribute : Attribute
    {
        public RuleAttribute(string ir)
        {
            Ir = ir;
        }

        public string Ir { get; }
    }
}
