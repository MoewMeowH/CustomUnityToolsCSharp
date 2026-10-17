using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EString.Script
{
    [Generator]
    public class E2StringGenerator : IIncrementalGenerator
    {
        private const string Category = "E2String";

        // E2StringOptions 비트 (어트리뷰트 쪽 enum과 값이 같아야 함). ParseExact(0)은 항상 생성.
        internal const int OptionIntConversion = 1 << 1;
        internal const int OptionCollections = 1 << 2;

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
            "'{0}'의 멤버 '{1}' 값이 int 범위를 벗어납니다. ToIntFast는 값을 잘라내고 IsDefinedFast(int)/To{2}Fast(int)는 이 멤버를 인식하지 않습니다.",
            Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor ClassNameCollisionRule = new DiagnosticDescriptor(
            "E2S004",
            "생성 클래스 이름이 겹칩니다",
            "'{0}'의 확장 클래스 이름 '{1}'이(가) 같은 namespace의 다른 enum과 겹쳐 생성하지 않습니다",
            Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

        /// <summary>테스트에서 단계별 캐시 여부를 확인할 때 쓰는 이름.</summary>
        internal static class TrackingNames
        {
            public const string Enums = "E2String.Enums";
            public const string CollisionLosers = "E2String.CollisionLosers";
        }

        private const int RejectNone = 0;
        private const int RejectGenericContainer = 1;
        private const int RejectInaccessible = 2;

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // enum마다 의미 분석을 해서 값으로 비교되는 모델로 바꾼다. 내용이 같으면 다음 단계는 캐시를 쓴다 (바뀐 enum만 다시 생성).
            var enums = context.SyntaxProvider
                .CreateSyntaxProvider(
                    static (node, _) => node is EnumDeclarationSyntax e && e.AttributeLists.Count > 0,
                    static (ctx, ct) => Analyze((EnumDeclarationSyntax)ctx.Node, ctx.SemanticModel, ct))
                .Where(static m => m != null)
                .Select(static (m, _) => m!)
                .WithTrackingName(TrackingNames.Enums);

            // 생성 클래스 이름 충돌(E2S004)은 전체를 봐야 안다. 결과(생성하지 않을 enum 목록)가 같으면 enum별 출력은 캐시를 쓴다.
            var losers = enums.Collect()
                .Select(static (all, _) => FindCollisionLosers(all))
                .WithTrackingName(TrackingNames.CollisionLosers);

            context.RegisterSourceOutput(enums.Combine(losers), static (spc, pair) => Emit(spc, pair.Left, pair.Right));
        }

        private static EnumModel? Analyze(EnumDeclarationSyntax declaration, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            if (!(semanticModel.GetDeclaredSymbol(declaration, cancellationToken) is INamedTypeSymbol symbol))
                return null;

            if (!TryGetTargetAttribute(symbol, out var options))
                return null;

            var model = new EnumModel
            {
                Name = symbol.Name,
                FullTypeName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                Location = LocationInfo.From(declaration.Identifier.GetLocation()),
                Reject = RejectNone,
            };

            if (HasGenericContainingType(symbol))
                return model with { Reject = RejectGenericContainer };

            if (!TryGetAccessibility(symbol, out var accessibility))
                return model with { Reject = RejectInaccessible };

            var info = EnumInfo.Create(symbol, options);
            return model with { Info = info, Accessibility = accessibility, ClassKey = $"{info.Namespace}.{info.ClassName}" };
        }

        /// <summary>생성 클래스 이름이 겹치면 먼저 나온 enum만 생성한다. 생성하지 않을 enum의 FullTypeName 목록.</summary>
        private static EquatableArray<string> FindCollisionLosers(ImmutableArray<EnumModel> all)
        {
            var generatedClasses = new HashSet<string>(StringComparer.Ordinal);
            var losers = new List<string>();
            foreach (var model in all)
            {
                if (model.Info != null && !generatedClasses.Add(model.ClassKey))
                    losers.Add(model.FullTypeName);
            }
            return new EquatableArray<string>(losers.ToArray());
        }

        private static void Emit(SourceProductionContext context, EnumModel model, EquatableArray<string> losers)
        {
            var location = model.Location.ToLocation();

            switch (model.Reject)
            {
                case RejectGenericContainer:
                    context.ReportDiagnostic(Diagnostic.Create(GenericContainerRule, location, model.Name));
                    return;
                case RejectInaccessible:
                    context.ReportDiagnostic(Diagnostic.Create(InaccessibleRule, location, model.Name));
                    return;
            }

            var info = model.Info!;
            if (losers.Contains(model.FullTypeName))
            {
                context.ReportDiagnostic(Diagnostic.Create(ClassNameCollisionRule, location, model.Name, info.ClassName));
                return;
            }

            if (info.HasIntConversion)
            {
                foreach (var member in info.Members.Where(m => !m.FitsInt))
                {
                    context.ReportDiagnostic(Diagnostic.Create(OutOfIntRangeRule, location, model.Name, member.Name, info.EnumName));
                }
            }

            var hintName = string.IsNullOrEmpty(info.HintNamespace)
                ? $"{info.ClassName}.g.cs"
                : $"{info.HintNamespace}.{info.ClassName}.g.cs";

            context.AddSource(hintName, SourceText.From(GenerateExtensionClass(info, model.Accessibility), Encoding.UTF8));
        }

        /// <summary>[E2String] 어트리뷰트를 찾고 옵션을 읽습니다. 옵션 인자가 없으면 ParseExact(0).</summary>
        private static bool TryGetTargetAttribute(INamedTypeSymbol symbol, out int options)
        {
            foreach (var attribute in symbol.GetAttributes())
            {
                var attributeClass = attribute.AttributeClass;
                if (attributeClass == null || attributeClass.TypeKind == TypeKind.Error)
                    continue;

                if (attributeClass.Name != "E2StringAttribute" && attributeClass.Name != "E2String")
                    continue;

                options = 0;
                if (attribute.ConstructorArguments.Length > 0 && TryReadOptions(attribute.ConstructorArguments[0], out var fromConstructor))
                    options = fromConstructor;

                foreach (var named in attribute.NamedArguments)
                {
                    if (named.Key == "Options" && TryReadOptions(named.Value, out var fromNamed))
                        options = fromNamed;
                }
                return true;
            }

            options = 0;
            return false;
        }

        private static bool TryReadOptions(TypedConstant constant, out int options)
        {
            options = 0;
            if (constant.Kind != TypedConstantKind.Enum && constant.Kind != TypedConstantKind.Primitive)
                return false;
            if (constant.Value == null)
                return false;

            try
            {
                options = unchecked((int)Convert.ToInt64(constant.Value, CultureInfo.InvariantCulture));
                return true;
            }
            catch (Exception e) when (e is FormatException || e is InvalidCastException || e is OverflowException)
            {
                return false;
            }
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

            // 1. Collections
            if (info.HasCollections)
            {
                w.Line("#region Collections");
                w.Line("/// <summary>모든 Enum 값 배열 (선언 순서, 별칭 포함)</summary>");
                w.Line($"public static readonly {t}[] Values = new {t}[]");
                w.Open();
                foreach (var member in info.Members)
                    w.Line($"{info.Cast(member)},");
                w.Close("};");
                w.Line("#endregion");
                w.Line();
            }

            // 2. Enum -> String (ParseExact)
            w.Line("#region Enum -> String");
            w.Line("/// <summary>Enum 값을 무할당(Zero-Allocation) 문자열로 고속 변환합니다. 같은 값의 별칭은 먼저 선언된 이름을 반환합니다. 정의 밖 값이면 fallback을 반환합니다.</summary>");
            w.Line("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
            w.Line($"public static string ToStringFast(this {t} value, string fallback = \"{info.DefaultFallback}\")");
            w.Open();
            w.Line($"switch (({u})value)");
            w.Open();
            foreach (var member in distinct)
                w.Line($"case {member.Literal}: return \"{member.Name}\";");
            w.Close();
            w.Line(info.IsFlags ? "return FormatFlags(value, fallback);" : "return fallback;");
            w.Close();

            if (info.IsFlags)
                WriteFlagsFormatting(w, info);

            w.Line("#endregion");
            w.Line();

            // 3. String -> Enum (ParseExact)
            w.Line("#region String -> Enum");
            w.Line("/// <summary>문자열을 Enum으로 고속 변환합니다. 이름이 대소문자까지 정확히 일치해야 하며(공백 · 숫자 문자열 불허)"
                   + (info.IsFlags ? ", \"A, B\" 형식의 플래그 조합을 받습니다" : "")
                   + ". 실패하면 fallback을 반환합니다.</summary>");
            w.Line($"public static {t} To{info.EnumName}Fast(this string? value, {t} fallback = default)");
            w.Open();
            w.Line("if (value == null) return fallback;");
            w.Line("switch (value)");
            w.Open();
            foreach (var member in info.Members)
                w.Line($"case \"{member.Name}\": return {info.Cast(member)};");
            w.Close();
            if (info.IsFlags)
            {
                w.Line("if (value.IndexOf(',') >= 0 && TryParseFlagList(value, 0, value.Length, out var combined))");
                w.Line("    return combined;");
            }
            w.Line("return fallback;");
            w.Close();

            if (info.IsFlags)
            {
                w.Line();
                WriteTryMatchName(w, info);
                WriteTryParseFlagList(w, info);
            }

            w.Line("#endregion");

            // 4. Enum <-> Int, IsDefined (IntConversion)
            if (info.HasIntConversion)
            {
                var intCheck = info.IsFlags ? "IsValidFlagsFast" : "IsDefinedFast";
                w.Line();
                w.Line("#region Enum <-> Int");
                w.Line("/// <summary>명시적 캐스트로 박싱 없이 int로 변환합니다. int 범위를 벗어난 값은 잘립니다.</summary>");
                w.Line("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
                w.Line($"public static int ToIntFast(this {t} value) => unchecked((int)value);");
                w.Line();
                w.Line($"/// <summary>int 값을 {intCheck} 검사 후 Enum으로 변환합니다. 유효하지 않으면 fallback을 반환합니다.</summary>");
                w.Line("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
                w.Line($"public static {t} To{info.EnumName}Fast(this int value, {t} fallback = default)");
                w.Line($"    => {intCheck}(value) ? unchecked(({t})value) : fallback;");
                w.Line("#endregion");
                w.Line();

                w.Line("#region IsDefined");
                w.Line("/// <summary>Enum 값이 이름이 정의된 값인지 고속 검사합니다." + (info.IsFlags ? " 조합 값은 IsValidFlagsFast를 사용하세요." : "") + "</summary>");
                w.Line("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
                w.Line($"public static bool IsDefinedFast({t} value)");
                w.Open();
                WriteCaseCheck(w, $"({u})value", distinct.Select(m => m.Literal));
                w.Close();
                w.Line();
                w.Line("/// <summary>정수 값이 이름이 정의된 Enum 값인지 고속 검사합니다 (잘림 없이 값 그대로 비교).</summary>");
                w.Line("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
                w.Line("public static bool IsDefinedFast(int value)");
                w.Open();
                WriteCaseCheck(w, "value", distinct.Where(m => m.FitsInt).Select(m => m.IntLiteral));
                w.Close();

                if (info.IsFlags)
                    WriteFlagsValidation(w, info);

                w.Line("#endregion");
            }

            if (info.IsFlags)
            {
                w.Line();
                w.Line("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
                w.Line($"private static ulong ToBits({t} value) => unchecked((ulong)({u})value);");
            }

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
            w.Line($"private static string FormatFlags({t} value, string fallback)");
            w.Open();
            w.Line("ulong bits = ToBits(value);");
            w.Line("lock (s_flagNameCache)");
            w.Open();
            w.Line("if (s_flagNameCache.TryGetValue(bits, out var cached)) return cached;");
            w.Close();
            w.Line("var composed = ComposeFlags(bits);");
            w.Line("if (composed == null) return fallback;");
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

        private static void WriteTryMatchName(CodeWriter w, EnumInfo info)
        {
            var t = info.FullTypeName;

            w.Line($"private static bool TryMatchName(string s, int start, int length, out {t} result)");
            w.Open();
            if (info.Members.Count > 0)
            {
                w.Line("switch (length)");
                w.Open();
                foreach (var group in info.Members.GroupBy(m => m.Name.Length).OrderBy(g => g.Key))
                {
                    w.Line($"case {group.Key}:");
                    foreach (var member in group)
                    {
                        w.Line($"    if (string.Compare(s, start, \"{member.Name}\", 0, {group.Key}, StringComparison.Ordinal) == 0) {{ result = {info.Cast(member)}; return true; }}");
                    }
                    w.Line("    break;");
                }
                w.Close();
            }
            w.Line("result = default;");
            w.Line("return false;");
            w.Close();
        }

        private static void WriteTryParseFlagList(CodeWriter w, EnumInfo info)
        {
            var t = info.FullTypeName;
            var u = info.UnderlyingKeyword;

            w.Line();
            w.Line("/// <summary>\"A, B\" 형식의 이름 목록을 OR로 합칩니다. 각 이름 앞뒤 공백은 무시합니다.</summary>");
            w.Line($"private static bool TryParseFlagList(string s, int start, int end, out {t} result)");
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
            w.Line("if (!TryMatchName(s, tokenStart, tokenEnd - tokenStart, out var part)) return false;");
            w.Line("bits |= ToBits(part);");
            w.Line("if (comma < 0) break;");
            w.Line("i = comma + 1;");
            w.Close();
            w.Line($"result = unchecked(({t})({u})bits);");
            w.Line("return true;");
            w.Close();
        }

        private static void WriteFlagsValidation(CodeWriter w, EnumInfo info)
        {
            var t = info.FullTypeName;
            var u = info.UnderlyingKeyword;

            w.Line();
            w.Line("/// <summary>정의된 모든 플래그의 비트 OR</summary>");
            w.Line($"private const ulong FlagMask = {info.FlagMaskLiteral};");
            w.Line();
            w.Line("/// <summary>[Flags] 값이 정의된 플래그들의 조합으로만 이루어져 있는지 검사합니다 (0 포함).</summary>");
            w.Line("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
            w.Line($"public static bool IsValidFlagsFast({t} value) => (ToBits(value) & ~FlagMask) == 0UL;");
            w.Line();
            w.Line("/// <summary>정수 값이 정의된 플래그들의 조합인지 검사합니다 (기본 형식 범위 밖이면 false).</summary>");
            w.Line("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
            w.Line("public static bool IsValidFlagsFast(int value)");
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
            w.Line($"return IsValidFlagsFast(unchecked(({t})value));");
            w.Close();
        }
    }

    /// <summary>
    /// 증분 파이프라인 단계 사이에 넘기는 enum 하나. ISymbol · Location(SyntaxTree를 붙잡음)을 들고 있지 않고
    /// 값으로만 비교되어야 캐시가 걸린다.
    /// </summary>
    internal sealed record EnumModel
    {
        public string Name { get; init; } = "";
        public string FullTypeName { get; init; } = "";
        public LocationInfo Location { get; init; }
        public int Reject { get; init; }
        public EnumInfo? Info { get; init; }
        public string Accessibility { get; init; } = "";
        public string ClassKey { get; init; } = "";
    }

    /// <summary>진단 위치를 값으로 들고 있다가 출력 단계에서 Location으로 되돌린다.</summary>
    internal readonly struct LocationInfo : IEquatable<LocationInfo>
    {
        private readonly string? _filePath;
        private readonly TextSpan _span;
        private readonly LinePositionSpan _lineSpan;

        private LocationInfo(string filePath, TextSpan span, LinePositionSpan lineSpan)
        {
            _filePath = filePath;
            _span = span;
            _lineSpan = lineSpan;
        }

        public static LocationInfo From(Location location) =>
            new LocationInfo(location.SourceTree?.FilePath ?? "", location.SourceSpan, location.GetLineSpan().Span);

        public Location ToLocation() => Location.Create(_filePath ?? "", _span, _lineSpan);

        public bool Equals(LocationInfo other) =>
            string.Equals(_filePath, other._filePath, StringComparison.Ordinal) && _span.Equals(other._span) && _lineSpan.Equals(other._lineSpan);

        public override bool Equals(object? obj) => obj is LocationInfo other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(_filePath ?? "");
                hash = hash * 31 + _span.GetHashCode();
                return hash * 31 + _lineSpan.GetHashCode();
            }
        }
    }

    /// <summary>요소 값으로 비교되는 읽기 전용 배열 (record의 기본 Equals는 배열을 참조로 비교하므로).</summary>
    internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
        where T : IEquatable<T>
    {
        private readonly T[]? _items;

        public EquatableArray(T[] items)
        {
            _items = items;
        }

        private T[] Items => _items ?? Array.Empty<T>();

        public int Count => Items.Length;

        public T this[int index] => Items[index];

        public bool Equals(EquatableArray<T> other) => Items.SequenceEqual(other.Items);

        public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                foreach (var item in Items)
                    hash = hash * 31 + (item == null ? 0 : item.GetHashCode());
                return hash;
            }
        }

        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal sealed record EnumMemberInfo
    {
        public string Name { get; init; } = "";
        public string Literal { get; init; } = "";
        public string IntLiteral { get; init; } = "";
        public string BitsLiteral { get; init; } = "";
        public ulong Bits { get; init; }
        public bool FitsInt { get; init; }
    }

    internal sealed record EnumInfo
    {
        public string EnumName { get; init; } = "";
        public string FullTypeName { get; init; } = "";
        public string Namespace { get; init; } = "";
        public string HintNamespace { get; init; } = "";
        public string ClassName { get; init; } = "";
        public string UnderlyingKeyword { get; init; } = "int";
        public SpecialType UnderlyingType { get; init; }
        public bool IsFlags { get; init; }
        public bool HasIntConversion { get; init; }
        public bool HasCollections { get; init; }
        public string FlagMaskLiteral { get; init; } = "0x0UL";
        public EquatableArray<EnumMemberInfo> Members { get; init; }

        /// <summary>값이 같은 별칭을 제거한 멤버 (먼저 선언된 것 우선)</summary>
        public EquatableArray<EnumMemberInfo> DistinctMembers { get; init; }

        /// <summary>0이 아닌 고유 값 멤버, 비트 값 오름차순</summary>
        public EquatableArray<EnumMemberInfo> FlagMembers { get; init; }

        /// <summary>ToStringFast의 fallback 기본값</summary>
        public string DefaultFallback => $"not defined {EnumName}";

        public string Cast(EnumMemberInfo member) => $"({FullTypeName})({member.Literal})";

        public static EnumInfo Create(INamedTypeSymbol symbol, int options)
        {
            var underlying = symbol.EnumUnderlyingType!;
            var info = new EnumInfo
            {
                EnumName = symbol.Name,
                FullTypeName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                UnderlyingType = underlying.SpecialType,
                UnderlyingKeyword = underlying.ToDisplayString(),
                IsFlags = symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.FlagsAttribute"),
                HasIntConversion = (options & E2StringGenerator.OptionIntConversion) != 0,
                HasCollections = (options & E2StringGenerator.OptionCollections) != 0,
            };

            if (!symbol.ContainingNamespace.IsGlobalNamespace)
            {
                info = info with
                {
                    Namespace = symbol.ContainingNamespace.ToDisplayString(NamespaceFormatHolder.Format),
                    // 파일 이름용: 키워드 namespace(@event)의 '@'는 hintName에 쓸 수 없어 AddSource가 예외를 던진다 (생성기 전체 중단)
                    HintNamespace = symbol.ContainingNamespace.ToDisplayString().Replace("@", ""),
                };
            }

            var containers = new List<string>();
            for (var type = symbol.ContainingType; type != null; type = type.ContainingType)
                containers.Insert(0, type.Name);
            containers.Add(symbol.Name);

            var members = new List<EnumMemberInfo>();
            var distinctMembers = new List<EnumMemberInfo>();
            var flagMembers = new List<EnumMemberInfo>();
            var seenBits = new HashSet<ulong>();
            ulong mask = 0;
            foreach (var field in symbol.GetMembers().OfType<IFieldSymbol>())
            {
                if (!field.HasConstantValue || field.ConstantValue == null)
                    continue;

                var member = CreateMember(field.Name, field.ConstantValue, info.UnderlyingType);
                members.Add(member);

                if (seenBits.Add(member.Bits))
                {
                    distinctMembers.Add(member);
                    if (member.Bits != 0)
                        flagMembers.Add(member);
                }
                mask |= member.Bits;
            }

            flagMembers.Sort((a, b) => a.Bits.CompareTo(b.Bits));
            return info with
            {
                ClassName = string.Join("_", containers) + "Extensions",
                Members = new EquatableArray<EnumMemberInfo>(members.ToArray()),
                DistinctMembers = new EquatableArray<EnumMemberInfo>(distinctMembers.ToArray()),
                FlagMembers = new EquatableArray<EnumMemberInfo>(flagMembers.ToArray()),
                FlagMaskLiteral = $"0x{mask:X}UL",
            };
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
}
