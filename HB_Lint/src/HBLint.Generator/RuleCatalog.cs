using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace HBLint.Generator
{
    public enum RuleValueKind
    {
        String,
        StringArray,
        Enum,
        Bool,
        Int,
    }

    /// <summary>규칙 종류의 칸 하나 (생성자 매개변수 = 필수, 설정 가능한 속성 = 선택).</summary>
    public sealed class RuleField
    {
        internal RuleField(string clrName, RuleValueKind kind, bool required, Type? enumType)
        {
            ClrName = clrName;
            JsonName = char.ToLowerInvariant(clrName[0]) + clrName.Substring(1);
            Kind = kind;
            Required = required;
            if (enumType != null)
            {
                EnumTypeName = enumType.FullName!.Replace('+', '.');
                EnumNames = Enum.GetNames(enumType);
            }
        }

        /// <summary>생성자 매개변수 이름 또는 속성 이름.</summary>
        public string ClrName { get; }

        /// <summary>rules.json 칸 이름 (camelCase).</summary>
        public string JsonName { get; }

        public RuleValueKind Kind { get; }

        public bool Required { get; }

        public string? EnumTypeName { get; }

        public IReadOnlyList<string> EnumNames { get; } = Array.Empty<string>();
    }

    /// <summary>규칙 종류 하나 = HBLint.{Name}RuleAttribute.</summary>
    public sealed class RuleKind
    {
        internal RuleKind(string name, string attributeFullName, bool allowMultiple, IReadOnlyList<RuleField> parameters, IReadOnlyList<RuleField> properties)
        {
            Name = name;
            AttributeFullName = attributeFullName;
            AllowMultiple = allowMultiple;
            Parameters = parameters;
            Properties = properties;
        }

        /// <summary>rules.json의 kind 값 (어트리뷰트 이름에서 "RuleAttribute"를 뗀 것).</summary>
        public string Name { get; }

        /// <summary>예 "HBLint.BanNullOperatorRuleAttribute".</summary>
        public string AttributeFullName { get; }

        /// <summary>AttributeUsage.AllowMultiple. false면 rules.json에 이 종류를 한 번만 쓸 수 있다.</summary>
        public bool AllowMultiple { get; }

        /// <summary>생성자 매개변수 순서 그대로.</summary>
        public IReadOnlyList<RuleField> Parameters { get; }

        /// <summary>이름 순.</summary>
        public IReadOnlyList<RuleField> Properties { get; }

        public IEnumerable<RuleField> Fields => Parameters.Concat(Properties);
    }

    public sealed class RuleCatalogException : Exception
    {
        public RuleCatalogException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// 어트리뷰트 어셈블리에서 규칙 종류를 읽는다: HBLint 네임스페이스의 중첩되지 않은 public 타입 중
    /// 이름이 "RuleAttribute"로 끝나고 AttributeUsage가 Assembly인 것. 새 규칙 종류를 넣어도 생성기는 고치지 않는다.
    /// </summary>
    public sealed class RuleCatalog
    {
        public const string Namespace = "HBLint";
        private const string Suffix = "RuleAttribute";

        private RuleCatalog(IReadOnlyList<RuleKind> kinds) => Kinds = kinds;

        /// <summary>이름 순.</summary>
        public IReadOnlyList<RuleKind> Kinds { get; }

        public RuleKind? Find(string name) => Kinds.FirstOrDefault(k => k.Name == name);

        public static RuleCatalog FromAssembly(Assembly attributesAssembly)
        {
            var kinds = new List<RuleKind>();
            foreach (var type in attributesAssembly.GetExportedTypes())
            {
                if (type.Namespace != Namespace || type.IsNested || type.Name.Length <= Suffix.Length || !type.Name.EndsWith(Suffix, StringComparison.Ordinal)
                    || !typeof(Attribute).IsAssignableFrom(type) || type.IsAbstract)
                    continue;
                var usage = (AttributeUsageAttribute?)Attribute.GetCustomAttribute(type, typeof(AttributeUsageAttribute));
                if (usage == null || (usage.ValidOn & AttributeTargets.Assembly) == 0)
                    continue;
                kinds.Add(ReadKind(type, usage.AllowMultiple));
            }
            if (kinds.Count == 0)
                throw new RuleCatalogException($"{attributesAssembly.GetName().Name}에 규칙 어트리뷰트({Namespace}.…{Suffix})가 없습니다");
            kinds.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return new RuleCatalog(kinds);
        }

        private static RuleKind ReadKind(Type type, bool allowMultiple)
        {
            var constructors = type.GetConstructors();
            if (constructors.Length != 1)
                throw new RuleCatalogException($"{type.FullName}: public 생성자가 하나여야 합니다 (지금 {constructors.Length}개)");

            var parameters = constructors[0].GetParameters()
                .Select(p => new RuleField(p.Name!, ValueKind(type, p.Name!, p.ParameterType), required: true, EnumOf(p.ParameterType)))
                .ToList();
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.CanWrite && p.SetMethod!.IsPublic)
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => new RuleField(p.Name, ValueKind(type, p.Name, p.PropertyType), required: false, EnumOf(p.PropertyType)))
                .ToList();

            var reserved = new HashSet<string>(RulesJson.ReservedRuleKeys, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in parameters.Concat(properties))
            {
                if (reserved.Contains(field.JsonName))
                    throw new RuleCatalogException($"{type.FullName}: '{field.JsonName}'은(는) rules.json 예약 칸이라 매개변수 · 속성 이름으로 쓸 수 없습니다");
                if (!seen.Add(field.JsonName))
                    throw new RuleCatalogException($"{type.FullName}: rules.json 칸 이름 '{field.JsonName}'이(가) 겹칩니다");
            }

            var name = type.Name.Substring(0, type.Name.Length - Suffix.Length);
            return new RuleKind(name, type.FullName!, allowMultiple, parameters, properties);
        }

        private static RuleValueKind ValueKind(Type owner, string member, Type type)
        {
            if (type == typeof(string)) return RuleValueKind.String;
            if (type == typeof(string[])) return RuleValueKind.StringArray;
            if (type == typeof(bool)) return RuleValueKind.Bool;
            if (type == typeof(int)) return RuleValueKind.Int;
            if (type.IsEnum && Enum.GetUnderlyingType(type) == typeof(int)) return RuleValueKind.Enum;
            throw new RuleCatalogException($"{owner.FullName}.{member}: 생성기가 다루지 않는 타입 {type.FullName} (string · string[] · bool · int · int 기반 enum만)");
        }

        private static Type? EnumOf(Type type) => type.IsEnum ? type : null;
    }
}
