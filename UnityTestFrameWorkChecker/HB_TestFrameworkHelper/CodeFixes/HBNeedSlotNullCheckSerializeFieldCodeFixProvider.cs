using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using HB_TestFrameworkHelper.Analyzers;

namespace HB_TestFrameworkHelper.CodeFixes
{
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(HBNeedSlotNullCheckSerializeFieldCodeFixProvider)), Shared]
    public class HBNeedSlotNullCheckSerializeFieldCodeFixProvider : CodeFixProvider
    {
        public sealed override ImmutableArray<string> FixableDiagnosticIds =>
            ImmutableArray.Create(HBNeedSlotNullCheckSerializeFieldAnalyzer.DiagnosticIdSerializeField);

        public sealed override FixAllProvider GetFixAllProvider() =>
            WellKnownFixAllProviders.BatchFixer;

        public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null) return;

            var diagnostic = context.Diagnostics.First();
            var diagnosticSpan = diagnostic.Location.SourceSpan;

            var fieldDeclaration = root.FindToken(diagnosticSpan.Start).Parent?.AncestorsAndSelf()
                .OfType<FieldDeclarationSyntax>()
                .FirstOrDefault();

            if (fieldDeclaration == null) return;

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: "[SerializeField] 어트리뷰트 추가",
                    createChangedDocument: c => AddSerializeFieldAsync(context.Document, fieldDeclaration, c),
                    equivalenceKey: "Add_SerializeField"),
                diagnostic);
        }

        private static async Task<Document> AddSerializeFieldAsync(
            Document document,
            FieldDeclarationSyntax fieldDeclaration,
            CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root == null) return document;

            var attributeSyntax = SyntaxFactory.Attribute(SyntaxFactory.ParseName("SerializeField"));
            var attributeList = SyntaxFactory.AttributeList(
                SyntaxFactory.SingletonSeparatedList(attributeSyntax))
                .WithLeadingTrivia(fieldDeclaration.GetLeadingTrivia());

            var cleanedField = fieldDeclaration.WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
            var newField = cleanedField.WithAttributeLists(cleanedField.AttributeLists.Insert(0, attributeList));

            var newRoot = root.ReplaceNode(fieldDeclaration, newField);

            if (newRoot is CompilationUnitSyntax compilationUnit)
            {
                // namespace 안의 using UnityEngine;도 있는 것으로 본다 (파일 맨 위에 중복으로 넣지 않게)
                bool hasUnityEngineUsing = compilationUnit.DescendantNodes(n => !(n is TypeDeclarationSyntax))
                    .OfType<UsingDirectiveSyntax>()
                    .Any(u => u.Alias == null && u.Name.ToString() == "UnityEngine");
                if (!hasUnityEngineUsing)
                {
                    // 기존 using이 없으면 아래 코드와 한 줄 띄운다 (줄바꿈 없이 넣으면 "using UnityEngine;public class …"가 된다)
                    var trailing = compilationUnit.Usings.Count == 0
                        ? SyntaxFactory.TriviaList(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed)
                        : SyntaxFactory.TriviaList(SyntaxFactory.CarriageReturnLineFeed);
                    var usingDirective = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("UnityEngine"))
                        .NormalizeWhitespace()
                        .WithTrailingTrivia(trailing);
                    newRoot = compilationUnit.AddUsings(usingDirective);
                }
            }

            return document.WithSyntaxRoot(newRoot);
        }
    }
}
