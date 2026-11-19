using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection.Metadata;
using Microsoft.CodeAnalysis;

namespace HBLint.Analyzers
{
    /// <summary>
    /// 규칙 어셈블리의 어셈블리 어트리뷰트를 메타데이터에서 직접 디코드한다. 검사 중인 컴파일이 HBLint.Attributes를
    /// 참조하지 않아 Roslyn이 어트리뷰트 인자를 비워 줄 때만 쓴다 (Unity에서 규칙 asmdef만 참조한 asmdef).
    /// enum 인자는 int 기반이라고 보고 읽는다 — 규칙 어트리뷰트의 enum은 int 기반만 쓴다 (생성기 카탈로그도 같은 제약).
    /// </summary>
    internal static class MetadataRuleReader
    {
        public static List<LintRule> Read(Compilation compilation, IAssemblySymbol assembly, string attributeFullName)
        {
            var result = new List<LintRule>();
            if (!(compilation.GetMetadataReference(assembly) is PortableExecutableReference reference)
                || !(reference.GetMetadata() is AssemblyMetadata metadata))
                return result;

            var separator = attributeFullName.LastIndexOf('.');
            var ns = attributeFullName.Substring(0, separator);
            var name = attributeFullName.Substring(separator + 1);

            var reader = metadata.GetModules()[0].GetMetadataReader();
            foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(handle);
                if (!DeclaringTypeIs(reader, attribute.Constructor, ns, name))
                    continue;

                CustomAttributeValue<string> value;
                try
                {
                    value = attribute.DecodeValue(TypeNameProvider.Instance);
                }
                catch (BadImageFormatException)
                {
                    continue; // 읽을 수 없는 어트리뷰트 블롭은 건너뛴다 (메타데이터 경계)
                }

                result.Add(new LintRule(
                    name,
                    assembly,
                    value.FixedArguments.Select(a => Convert(a.Value)).ToArray(),
                    value.NamedArguments.ToDictionary(a => a.Name ?? "", a => Convert(a.Value))));
            }
            return result;
        }

        private static bool DeclaringTypeIs(MetadataReader reader, EntityHandle constructor, string ns, string name)
        {
            switch (constructor.Kind)
            {
                case HandleKind.MemberReference:
                    var parent = reader.GetMemberReference((MemberReferenceHandle)constructor).Parent;
                    if (parent.Kind != HandleKind.TypeReference)
                        return false;
                    var typeReference = reader.GetTypeReference((TypeReferenceHandle)parent);
                    return reader.StringComparer.Equals(typeReference.Namespace, ns) && reader.StringComparer.Equals(typeReference.Name, name);
                case HandleKind.MethodDefinition:
                    var typeDefinition = reader.GetTypeDefinition(reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType());
                    return reader.StringComparer.Equals(typeDefinition.Namespace, ns) && reader.StringComparer.Equals(typeDefinition.Name, name);
                default:
                    return false;
            }
        }

        /// <summary>LintRuleReader와 같은 모양으로: 문자열 · 정수 · 배열(object?[]).</summary>
        private static object? Convert(object? value)
        {
            return value is ImmutableArray<CustomAttributeTypedArgument<string>> array
                ? array.Select(a => Convert(a.Value)).ToArray()
                : value;
        }

        /// <summary>타입을 이름 문자열로만 다루는 디코드 도우미.</summary>
        private sealed class TypeNameProvider : ICustomAttributeTypeProvider<string>
        {
            public static readonly TypeNameProvider Instance = new TypeNameProvider();

            public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

            public string GetSystemType() => "System.Type";

            public string GetSZArrayType(string elementType) => elementType + "[]";

            public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            {
                var definition = reader.GetTypeDefinition(handle);
                return reader.GetString(definition.Namespace) + "." + reader.GetString(definition.Name);
            }

            public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            {
                var typeReference = reader.GetTypeReference(handle);
                return reader.GetString(typeReference.Namespace) + "." + reader.GetString(typeReference.Name);
            }

            public string GetTypeFromSerializedName(string name) => name;

            public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;

            public bool IsSystemType(string type) => type == "System.Type";
        }
    }
}
