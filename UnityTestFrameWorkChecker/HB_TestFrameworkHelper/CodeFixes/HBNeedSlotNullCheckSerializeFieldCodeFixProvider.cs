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
                bool hasUnityEngineUsing = compilationUnit.Usings.Any(u => u.Name.ToString() == "UnityEngine");
                if (!hasUnityEngineUsing)
                {
                    var usingDirective = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("UnityEngine"))
                        .NormalizeWhitespace();
                    newRoot = compilationUnit.AddUsings(usingDirective);
                }
            }

            return document.WithSyntaxRoot(newRoot);
        }
    }
}
