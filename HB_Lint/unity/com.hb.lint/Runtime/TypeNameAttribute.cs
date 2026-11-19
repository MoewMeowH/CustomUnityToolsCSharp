using System;

namespace HBLint
{
    /// <summary>
    /// 규칙 어트리뷰트 생성자 매개변수에 붙인다: 이 값은 타입 메타데이터 이름이다.
    /// Unity 생성 단계가 그 타입이 불려 있는 어셈블리에 있는지 확인하고, 없으면 rules.json 줄과 함께 오류로 막는다.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class TypeNameAttribute : Attribute
    {
    }
}
