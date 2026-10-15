using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EString.Script
{
    [Generator]
    public class E2StringGenerator : ISourceGenerator
    {
        private const string Category = "E2String";

        private static readonly DiagnosticDescriptor GenericContainerRule = new DiagnosticDescriptor(
            "E2S001",
            "제네릭 타입 안의 enum은 지원하지 않습니다",
            "'{0}'은(는) 제네릭 타입 안에 선언되어 있어 확장 메서드를 생성하지 않습니다",
            Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor InaccessibleRule = new DiagnosticDescriptor(
            "E2S002",
            "외부에서 접근할 수 없는 enum입니다",
            "'{0}'(또는 바깥 타입)이 private/protected라 namespace 수준 확장 클래스에서 접근할 수 없어 생성하지 않습니다",
            Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor OutOfIntRangeRule = new DiagnosticDescriptor(
            "E2S003",
            "int 범위를 벗어난 멤버가 있습니다",
            "'{0}'의 멤버 '{1}' 값이 int 범위를 벗어납니다. ToInt는 값을 잘라내고 IsDefined(int)/To{2}(int)는 이 멤버를 인식하지 않습니다.",
            Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor ClassNameCollisionRule = new DiagnosticDescriptor(
            "E2S004",
            "생성 클래스 이름이 겹칩니다",
            "'{0}'의 확장 클래스 이름 '{1}'이(가) 같은 namespace의 다른 enum과 겹쳐 생성하지 않습니다",
            Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new EnumSyntaxReceiver());
        }

        public void Execute(GeneratorExecutionContext context)
        {
            if (!(context.SyntaxReceiver is EnumSyntaxReceiver receiver))
                return;

            var generatedClasses = new HashSet<string>(StringComparer.Ordinal);

            foreach (var enumDeclaration in receiver.CandidateEnums)
            {
                var model = context.Compilation.GetSemanticModel(enumDeclaration.SyntaxTree);
                if (!(model.GetDeclaredSymbol(enumDeclaration, context.CancellationToken) is INamedTypeSymbol symbol))
                    continue;

                if (!HasTargetAttribute(symbol))
                    continue;

                var location = enumDeclaration.Identifier.GetLocation();

                if (HasGenericContainingType(symbol))
                {
                    context.ReportDiagnostic(Diagnostic.Create(GenericContainerRule, location, symbol.Name));
                    continue;
                }

                if (!TryGetAccessibility(symbol, out var accessibility))
                {
                    context.ReportDiagnostic(Diagnostic.Create(InaccessibleRule, location, symbol.Name));
                    continue;
                }

                var info = EnumInfo.Create(symbol);

                var classKey = $"{info.Namespace}.{info.ClassName}";
                if (!generatedClasses.Add(classKey))
                {
                    context.ReportDiagnostic(Diagnostic.Create(ClassNameCollisionRule, location, symbol.Name, info.ClassName));
                    continue;
                }

                foreach (var member in info.Members.Where(m => !m.FitsInt))
                {
                    context.ReportDiagnostic(Diagnostic.Create(OutOfIntRangeRule, location, symbol.Name, member.Name, info.EnumName));
                }

                var hintName = string.IsNullOrEmpty(info.HintNamespace)
                    ? $"{info.ClassName}.g.cs"
                    : $"{info.HintNamespace}.{info.ClassName}.g.cs";

                context.AddSource(hintName, SourceText.From(GenerateExtensionClass(info, accessibility), Encoding.UTF8));
            }
        }

        private static bool HasTargetAttribute(INamedTypeSymbol symbol)
        {
            foreach (var attribute in symbol.GetAttributes())
            {
                var attributeClass = attribute.AttributeClass;
                if (attributeClass == null || attributeClass.TypeKind == TypeKind.Error)
                    continue;

                if (attributeClass.Name == "E2StringAttribute" || attributeClass.Name == "E2String")
                    return true;
            }
            return false;
        }

        private static bool HasGenericContainingType(INamedTypeSymbol symbol)
        {
            for (var type = symbol.ContainingType; type != null; type = type.ContainingType)
            {
                if (type.TypeParameters.Length > 0)
                    return true;
            }
            return false;
        }

        /// <summary>enum과 바깥 타입의 접근성으로 생성 클래스의 접근성을 정합니다. 접근 불가면 false.</summary>
        private static bool TryGetAccessibility(INamedTypeSymbol symbol, out string accessibility)
        {
            var isPublic = true;
            for (INamedTypeSymbol? type = symbol; type != null; type = type.ContainingType)
            {
                switch (type.DeclaredAccessibility)
                {
                    case Accessibility.Public:
                        break;
                    case Accessibility.Internal:
                    case Accessibility.ProtectedOrInternal:
                        isPublic = false;
                        break;
                    default:
                        accessibility = "";
                        return false;
                }
            }

            accessibility = isPublic ? "public" : "internal";
            return true;
        }

        private static string GenerateExtensionClass(EnumInfo info, string accessibility)
        {
            var w = new CodeWriter();
            var t = info.FullTypeName;
            var u = info.UnderlyingKeyword;
            var distinct = info.DistinctMembers;

            w.Line("// <auto-generated/>");
            w.Line("#nullable enable");
            w.Line("#pragma warning disable");
            w.Line("using System;");
            w.Line("using System.Runtime.CompilerServices;");
            w.Line();

            var hasNamespace = !string.IsNullOrEmpty(info.Namespace);
            if (hasNamespace)
            {
                w.Line($"namespace {info.Namespace}");
                w.Open();
            }

            w.Line($"{accessibility} static class {info.ClassName}");
            w.Open();

            // 1. Count
            w.Line("/// <summary>전체 Enum 항목 개수 (별칭 포함)</summary>");
            w.Line($"public const int Count = {info.Members.Count};");
            w.Line();

            // 2. Names
            w.Line("/// <summary>모든 Enum 식별자 이름 배열 (선언 순서)</summary>");
            w.Line("public static readonly string[] Names = new string[]");
            w.Open();
            foreach (var member in info.Members)
                w.Line($"\"{member.Name}\",");
            w.Close("};");
            w.Line();
            w.Line("/// <summary>모든 Enum 식별자 이름 읽기 전용 컬렉션</summary>");
            w.Line("public static System.Collections.Generic.IReadOnlyList<string> AllNames => Names;");
            w.Line();

            // 3. Values
            w.Line("/// <summary>모든 Enum 값 배열 (선언 순서)</summary>");
            w.Line($"public static readonly {t}[] Values = new {t}[]");
            w.Open();
            foreach (var member in info.Members)
                w.Line($"{info.Cast(member)},");
            w.Close("};");
            w.Line();
            w.Line("/// <summary>모든 Enum 값 읽기 전용 컬렉션</summary>");
            w.Line($"public static System.Collections.Generic.IReadOnlyList<{t}> AllValues => Values;");
            w.Line();

            // 4. Enum -> String
            w.Line("#region Enum -> String");
            w.Line("/// <summary>Enum 값을 무할당(Zero-Allocation) 문자열로 고속 변환합니다. 같은 값의 별칭은 먼저 선언된 이름을 반환합니다.</summary>");
            w.Line("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
            w.Line($"public static string ToStringFast(this {t} value, string? fallback = null)");
            w.Open();
            w.Line($"switch (({u})value)");
            w.Open();
            foreach (var member in distinct)
                w.Line($"case {member.Literal}: return \"{member.Name}\";");
            w.Close();
            w.Line(info.IsFlags ? "return FormatFlags(value, fallback);" : "return fallback ?? value.ToString();");
            w.Close();

            if (info.IsFlags)
                WriteFlagsFormatting(w, info);

            w.Line("#endregion");
            w.Line();

            // 5. String -> Enum
            w.Line("#region String -> Enum");
            w.Line("/// <summary>문자열을 Enum으로 고속 변환합니다 (대소문자 구분, 정확히 일치). 일치하지 않으면 fallback을 반환합니다.</summary>");
            w.Line("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
            w.Line($"public static {t} To{info.EnumName}(this string? value, {t} fallback = default)");
            w.Open();
            w.Line("TryParseFast(value, out var result, fallback);");
            w.Line("return result;");
            w.Close();
            w.Line();

            w.Line("/// <summary>문자열을 Enum으로 안전하게 파싱합니다 (대소문자 구분, 정확히 일치).</summary>");
            w.Line($"public static bool TryParseFast(string? value, out {t} result, {t} fallback = default)");
            w.Open();
            w.Line("switch (value)");
            w.Open();
            foreach (var member in info.Members)
            {
                w.Line($"case \"{member.Name}\":");
                w.Line($"    result = {info.Cast(member)};");
                w.Line("    return true;");
            }
            w.Close();
            if (info.IsFlags)
            {
                w.Line("if (value != null && value.IndexOf(',') >= 0 && TryParseFlagList(value, 0, value.Length, false, out result))");
                w.Line("    return true;");
            }
            w.Line("result = fallback;");
            w.Line("return false;");
            w.Close();
            w.Line();

            w.Line("/// <summary>문자열을 Enum으로 변환합니다. 앞뒤 공백을 무시하고, ignoreCase와 숫자 문자열을 지원합니다. 실패하면 fallback을 반환합니다.</summary>");
            w.Line($"public static {t} To{info.EnumName}(this string? value, bool ignoreCase, {t} fallback = default)");
            w.Open();
            w.Line("return TryParse(value, ignoreCase, out var result) ? result : fallback;");
            w.Close();
            w.Line();

            w.Line("/// <summary>문자열을 Enum으로 파싱합니다. 앞뒤 공백 무시, ignoreCase, 정의된 값의 숫자 문자열" + (info.IsFlags ? ", \"A, B\" 형식의 플래그 조합" : "") + "을 지원합니다.</summary>");
            w.Line($"public static bool TryParse(string? value, bool ignoreCase, out {t} result)");
            w.Open();
            w.Line("result = default;");
            w.Line("if (value == null) return false;");
            w.Line("int start = 0, end = value.Length;");
            w.Line("while (start < end && char.IsWhiteSpace(value[start])) start++;");
            w.Line("while (end > start && char.IsWhiteSpace(value[end - 1])) end--;");
            w.Line("if (start == end) return false;");
            w.Line("char first = value[start];");
            w.Line("if ((first >= '0' && first <= '9') || first == '-' || first == '+')");
            w.Line("    return TryParseNumber(value, out result);");
            w.Line(info.IsFlags
                ? "return TryParseFlagList(value, start, end, ignoreCase, out result);"
                : "return TryMatchName(value, start, end - start, ignoreCase, out result);");
            w.Close();
            w.Line();

            WriteTryParseNumber(w, info);
            WriteTryMatchName(w, info);
            if (info.IsFlags)
                WriteTryParseFlagList(w, info);

            w.Line("#endregion");
            w.Line();

            // 6. Enum <-> Int
            var intCheck = info.IsFlags ? "IsValidFlags" : "IsDefined";
            w.Line("#region Enum <-> Int");
            w.Line("/// <summary>명시적 캐스트로 박싱 없이 int로 변환합니다. int 범위를 벗어난 값은 잘립니다.</summary>");
            w.Line("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
            w.Line($"public static int ToInt(this {t} value) => unchecked((int)value);");
            w.Line();
            w.Line("/// <summary>ToInt와 동일합니다.</summary>");
            w.Line("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
            w.Line($"public static int ToIntFast(this {t} value) => unchecked((int)value);");
            w.Line();
            w.Line($"/// <summary>int 값을 {intCheck} 검사 후 Enum으로 변환합니다. 유효하지 않으면 fallback을 반환합니다.</summary>");
            w.Line("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
            w.Line($"public static {t} To{info.EnumName}(this int value, {t} fallback = default)");
            w.Line($"    => {intCheck}(value) ? unchecked(({t})value) : fallback;");
            w.Line();
            w.Line("/// <summary>검사 없이 명시적 캐스트로 int를 Enum으로 변환합니다.</summary>");
            w.Line("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
            w.Line($"public static {t} To{info.EnumName}Fast(this int value) => unchecked(({t})value);");
            w.Line("#endregion");
            w.Line();

            // 7. IsDefined
            w.Line("#region IsDefined Fast");
            w.Line("/// <summary>Enum 값이 이름이 정의된 값인지 고속 검사합니다. [Flags] 조합 값은 IsValidFlags를 사용하세요.</summary>");
            w.Line("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
            w.Line($"public static bool IsDefined({t} value)");
            w.Open();
            WriteCaseCheck(w, $"({u})value", distinct.Select(m => m.Literal));
            w.Close();
            w.Line();
            w.Line("/// <summary>정수 값이 이름이 정의된 Enum 값인지 고속 검사합니다 (잘림 없이 값 그대로 비교).</summary>");
            w.Line("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
            w.Line("public static bool IsDefined(int value)");
            w.Open();
            WriteCaseCheck(w, "value", distinct.Where(m => m.FitsInt).Select(m => m.IntLiteral));
            w.Close();

            if (info.IsFlags)
                WriteFlagsValidation(w, info);

            w.Line("#endregion");

            w.Close();
            if (hasNamespace)
                w.Close();

            return w.ToString();
        }

        private static void WriteCaseCheck(CodeWriter w, string switchExpression, IEnumerable<string> literals)
        {
            var list = literals.ToList();
            if (list.Count > 0)
            {
                w.Line($"switch ({switchExpression})");
                w.Open();
                foreach (var literal in list)
                    w.Line($"case {literal}:");
                w.Line("    return true;");
                w.Close();
            }
            w.Line("return false;");
        }

        private static void WriteFlagsFormatting(CodeWriter w, EnumInfo info)
        {
            var t = info.FullTypeName;
            var flagMembers = info.FlagMembers;

            w.Line();
            w.Line("private static readonly System.Collections.Generic.Dictionary<ulong, string> s_flagNameCache = new System.Collections.Generic.Dictionary<ulong, string>();");
            w.Line();
            w.Line("/// <summary>[Flags] 조합 값을 Enum.ToString()과 같은 \"A, B\" 형식으로 만듭니다. 처음 한 번만 할당하고 캐시합니다.</summary>");
            w.Line($"private static string FormatFlags({t} value, string? fallback)");
            w.Open();
            w.Line("ulong bits = ToBits(value);");
            w.Line("lock (s_flagNameCache)");
            w.Open();
            w.Line("if (s_flagNameCache.TryGetValue(bits, out var cached)) return cached;");
            w.Close();
            w.Line("var composed = ComposeFlags(bits);");
            w.Line("if (composed == null) return fallback ?? value.ToString();");
            w.Line("lock (s_flagNameCache)");
            w.Open();
            w.Line("s_flagNameCache[bits] = composed;");
            w.Close();
            w.Line("return composed;");
            w.Close();
            w.Line();

            w.Line("private static string? ComposeFlags(ulong bits)");
            w.Open();
            if (flagMembers.Count == 0)
            {
                w.Line("return null;");
            }
            else
            {
                w.Line("if (bits == 0UL) return null;");
                w.Line($"var picked = new bool[{flagMembers.Count}];");
                w.Line("ulong remaining = bits;");
                // 큰 값부터 채워 나가고, 출력은 작은 값부터 (Enum.ToString과 같은 규칙)
                for (var i = flagMembers.Count - 1; i >= 0; i--)
                {
                    var hex = flagMembers[i].BitsLiteral;
                    w.Line($"if ((remaining & {hex}) == {hex}) {{ remaining &= ~{hex}; picked[{i}] = true; }}");
                }
                w.Line("if (remaining != 0UL) return null;");
                w.Line("var sb = new System.Text.StringBuilder();");
                for (var i = 0; i < flagMembers.Count; i++)
                    w.Line($"if (picked[{i}]) {{ if (sb.Length > 0) sb.Append(\", \"); sb.Append(\"{flagMembers[i].Name}\"); }}");
                w.Line("return sb.ToString();");
            }
            w.Close();
        }

        private static void WriteTryParseNumber(CodeWriter w, EnumInfo info)
        {
            var t = info.FullTypeName;
            var u = info.UnderlyingKeyword;

            w.Line($"private static bool TryParseNumber(string value, out {t} result)");
            w.Open();
            w.Line("result = default;");
            if (info.UnderlyingType == SpecialType.System_UInt64)
            {
                w.Line("if (!ulong.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number)) return false;");
            }
            else
            {
                w.Line("if (!long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number)) return false;");
                if (info.UnderlyingType != SpecialType.System_Int64)
                    w.Line($"if (number < {u}.MinValue || number > {u}.MaxValue) return false;");
            }
            w.Line($"var candidate = unchecked(({t})({u})number);");
            w.Line($"if (!{(info.IsFlags ? "IsValidFlags" : "IsDefined")}(candidate)) return false;");
            w.Line("result = candidate;");
            w.Line("return true;");
            w.Close();
            w.Line();
        }

        private static void WriteTryMatchName(CodeWriter w, EnumInfo info)
        {
            var t = info.FullTypeName;

            w.Line($"private static bool TryMatchName(string s, int start, int length, bool ignoreCase, out {t} result)");
            w.Open();
            if (info.Members.Count > 0)
            {
                w.Line("var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;");
                w.Line("switch (length)");
                w.Open();
                foreach (var group in info.Members.GroupBy(m => m.Name.Length).OrderBy(g => g.Key))
                {
                    w.Line($"case {group.Key}:");
                    foreach (var member in group)
                    {
                        w.Line($"    if (string.Compare(s, start, \"{member.Name}\", 0, {group.Key}, comparison) == 0) {{ result = {info.Cast(member)}; return true; }}");
                    }
                    w.Line("    break;");
                }
                w.Close();
            }
            w.Line("result = default;");
            w.Line("return false;");
            w.Close();
            w.Line();
        }

        private static void WriteTryParseFlagList(CodeWriter w, EnumInfo info)
        {
            var t = info.FullTypeName;
            var u = info.UnderlyingKeyword;

            w.Line("/// <summary>\"A, B\" 형식의 이름 목록을 OR로 합칩니다. 각 이름 앞뒤 공백은 무시합니다.</summary>");
            w.Line($"private static bool TryParseFlagList(string s, int start, int end, bool ignoreCase, out {t} result)");
            w.Open();
            w.Line("result = default;");
            w.Line("ulong bits = 0UL;");
            w.Line("int i = start;");
            w.Line("while (true)");
            w.Open();
            w.Line("int comma = s.IndexOf(',', i, end - i);");
            w.Line("int tokenStart = i, tokenEnd = comma < 0 ? end : comma;");
            w.Line("while (tokenStart < tokenEnd && char.IsWhiteSpace(s[tokenStart])) tokenStart++;");
            w.Line("while (tokenEnd > tokenStart && char.IsWhiteSpace(s[tokenEnd - 1])) tokenEnd--;");
            w.Line("if (tokenStart == tokenEnd) return false;");
            w.Line("if (!TryMatchName(s, tokenStart, tokenEnd - tokenStart, ignoreCase, out var part)) return false;");
            w.Line("bits |= ToBits(part);");
            w.Line("if (comma < 0) break;");
            w.Line("i = comma + 1;");
            w.Close();
            w.Line($"result = unchecked(({t})({u})bits);");
            w.Line("return true;");
            w.Close();
            w.Line();
        }

        private static void WriteFlagsValidation(CodeWriter w, EnumInfo info)
        {
            var t = info.FullTypeName;
            var u = info.UnderlyingKeyword;

            w.Line();
            w.Line("/// <summary>정의된 모든 플래그의 비트 OR</summary>");
            w.Line($"private const ulong FlagMask = {info.FlagMaskLiteral};");
            w.Line();
            w.Line("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
            w.Line($"private static ulong ToBits({t} value) => unchecked((ulong)({u})value);");
            w.Line();
            w.Line("/// <summary>[Flags] 값이 정의된 플래그들의 조합으로만 이루어져 있는지 검사합니다 (0 포함).</summary>");
            w.Line("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
            w.Line($"public static bool IsValidFlags({t} value) => (ToBits(value) & ~FlagMask) == 0UL;");
            w.Line();
            w.Line("/// <summary>정수 값이 정의된 플래그들의 조합인지 검사합니다 (기본 형식 범위 밖이면 false).</summary>");
            w.Line("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
            w.Line("public static bool IsValidFlags(int value)");
            w.Open();
            switch (info.UnderlyingType)
            {
                case SpecialType.System_Byte:
                case SpecialType.System_SByte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                    w.Line($"if (value < {u}.MinValue || value > {u}.MaxValue) return false;");
                    break;
                case SpecialType.System_UInt32:
                case SpecialType.System_UInt64:
                    w.Line("if (value < 0) return false;");
                    break;
            }
            w.Line($"return IsValidFlags(unchecked(({t})value));");
            w.Close();
        }
    }

    internal sealed class EnumMemberInfo
    {
        public string Name = "";
        public string Literal = "";
        public string IntLiteral = "";
        public string BitsLiteral = "";
        public ulong Bits;
        public bool FitsInt;
    }

    internal sealed class EnumInfo
    {
        public string EnumName = "";
        public string FullTypeName = "";
        public string Namespace = "";
        public string HintNamespace = "";
        public string ClassName = "";
        public string UnderlyingKeyword = "int";
        public SpecialType UnderlyingType;
        public bool IsFlags;
        public string FlagMaskLiteral = "0x0UL";
        public List<EnumMemberInfo> Members = new List<EnumMemberInfo>();

        /// <summary>값이 같은 별칭을 제거한 멤버 (먼저 선언된 것 우선)</summary>
        public List<EnumMemberInfo> DistinctMembers = new List<EnumMemberInfo>();

        /// <summary>0이 아닌 고유 값 멤버, 비트 값 오름차순</summary>
        public List<EnumMemberInfo> FlagMembers = new List<EnumMemberInfo>();

        public string Cast(EnumMemberInfo member) => $"({FullTypeName})({member.Literal})";

        public static EnumInfo Create(INamedTypeSymbol symbol)
        {
            var underlying = symbol.EnumUnderlyingType!;
            var info = new EnumInfo
            {
                EnumName = symbol.Name,
                FullTypeName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                UnderlyingType = underlying.SpecialType,
                UnderlyingKeyword = underlying.ToDisplayString(),
                IsFlags = symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.FlagsAttribute"),
            };

            if (!symbol.ContainingNamespace.IsGlobalNamespace)
            {
                info.Namespace = symbol.ContainingNamespace.ToDisplayString(NamespaceFormatHolder.Format);
                info.HintNamespace = symbol.ContainingNamespace.ToDisplayString();
            }

            var containers = new List<string>();
            for (var type = symbol.ContainingType; type != null; type = type.ContainingType)
                containers.Insert(0, type.Name);
            containers.Add(symbol.Name);
            info.ClassName = string.Join("_", containers) + "Extensions";

            var seenBits = new HashSet<ulong>();
            ulong mask = 0;
            foreach (var field in symbol.GetMembers().OfType<IFieldSymbol>())
            {
                if (!field.HasConstantValue || field.ConstantValue == null)
                    continue;

                var member = CreateMember(field.Name, field.ConstantValue, info.UnderlyingType);
                info.Members.Add(member);

                if (seenBits.Add(member.Bits))
                {
                    info.DistinctMembers.Add(member);
                    if (member.Bits != 0)
                        info.FlagMembers.Add(member);
                }
                mask |= member.Bits;
            }

            info.FlagMembers.Sort((a, b) => a.Bits.CompareTo(b.Bits));
            info.FlagMaskLiteral = $"0x{mask:X}UL";
            return info;
        }

        private static EnumMemberInfo CreateMember(string name, object value, SpecialType underlying)
        {
            var isUnsigned = underlying == SpecialType.System_Byte
                             || underlying == SpecialType.System_UInt16
                             || underlying == SpecialType.System_UInt32
                             || underlying == SpecialType.System_UInt64;

            var number = Convert.ToString(value, CultureInfo.InvariantCulture)!;
            ulong bits;
            bool fitsInt;
            if (isUnsigned)
            {
                var unsigned = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
                bits = unsigned;
                fitsInt = unsigned <= int.MaxValue;
            }
            else
            {
                var signed = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                bits = unchecked((ulong)signed);
                fitsInt = signed >= int.MinValue && signed <= int.MaxValue;
            }

            string suffix;
            switch (underlying)
            {
                case SpecialType.System_UInt32: suffix = "U"; break;
                case SpecialType.System_Int64: suffix = "L"; break;
                case SpecialType.System_UInt64: suffix = "UL"; break;
                default: suffix = ""; break;
            }

            return new EnumMemberInfo
            {
                Name = name,
                Literal = number + suffix,
                IntLiteral = number,
                Bits = bits,
                BitsLiteral = $"0x{bits:X}UL",
                FitsInt = fitsInt,
            };
        }
    }

    internal static class NamespaceFormatHolder
    {
        public static readonly SymbolDisplayFormat Format = new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);
    }

    internal sealed class CodeWriter
    {
        private readonly StringBuilder _sb = new StringBuilder();
        private int _indent;

        public void Line(string text = "")
        {
            if (text.Length > 0)
                _sb.Append(' ', _indent * 4);
            _sb.AppendLine(text);
        }

        public void Open()
        {
            Line("{");
            _indent++;
        }

        public void Close(string text = "}")
        {
            _indent--;
            Line(text);
        }

        public override string ToString() => _sb.ToString();
    }

    internal class EnumSyntaxReceiver : ISyntaxReceiver
    {
        public List<EnumDeclarationSyntax> CandidateEnums { get; } = new List<EnumDeclarationSyntax>();

        public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
        {
            if (syntaxNode is EnumDeclarationSyntax enumDeclaration &&
                enumDeclaration.AttributeLists.Count > 0)
            {
                CandidateEnums.Add(enumDeclaration);
            }
        }
    }
}
