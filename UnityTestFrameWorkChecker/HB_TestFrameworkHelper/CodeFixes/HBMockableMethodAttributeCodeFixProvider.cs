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
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(HBMockableMethodAttributeCodeFixProvider)), Shared]
    public class HBMockableMethodAttributeCodeFixProvider : CodeFixProvider
    {
        public sealed override ImmutableArray<string> FixableDiagnosticIds =>
            ImmutableArray.Create(HBMockableMethodAttributeAnalyzer.DiagnosticId);

        public sealed override FixAllProvider GetFixAllProvider() =>
            WellKnownFixAllProviders.BatchFixer;

        public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null) return;

            var diagnostic = context.Diagnostics.First();
            var diagnosticSpan = diagnostic.Location.SourceSpan;

            var methodDeclaration = root.FindToken(diagnosticSpan.Start).Parent?.AncestorsAndSelf()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault();

            if (methodDeclaration == null) return;

            // 1. [CustomEditorOnly] 어트리뷰트 추가 액션
            context.RegisterCodeFix(
                CodeAction.Create(
                    title: "[CustomEditorOnly] 어트리뷰트 추가",
                    createChangedDocument: c => AddAttributeAsync(context.Document, methodDeclaration, "CustomEditorOnly", null, c),
                    equivalenceKey: "Add_CustomEditorOnly"),
                diagnostic);

            // 2. [System.Diagnostics.Conditional("UNITY_EDITOR")] 어트리뷰트 추가 액션
            context.RegisterCodeFix(
                CodeAction.Create(
                    title: "[System.Diagnostics.Conditional(\"UNITY_EDITOR\")] 어트리뷰트 추가",
                    createChangedDocument: c => AddAttributeAsync(context.Document, methodDeclaration, "System.Diagnostics.Conditional", "\"UNITY_EDITOR\"", c),
                    equivalenceKey: "Add_Conditional_UnityEditor"),
                diagnostic);
        }

        private static async Task<Document> AddAttributeAsync(
            Document document,
            MethodDeclarationSyntax methodDeclaration,
            string attributeName,
            string? attributeArgument,
            CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root == null) return document;

            AttributeSyntax attributeSyntax;
            if (string.IsNullOrEmpty(attributeArgument))
            {
                attributeSyntax = SyntaxFactory.Attribute(SyntaxFactory.ParseName(attributeName));
            }
            else
            {
                var argumentList = SyntaxFactory.AttributeArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.AttributeArgument(
                            SyntaxFactory.ParseExpression(attributeArgument!))));
                attributeSyntax = SyntaxFactory.Attribute(SyntaxFactory.ParseName(attributeName), argumentList);
            }

            var attributeList = SyntaxFactory.AttributeList(
                SyntaxFactory.SingletonSeparatedList(attributeSyntax))
                .WithLeadingTrivia(methodDeclaration.GetLeadingTrivia());

            var cleanedMethod = methodDeclaration.WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
            var newMethod = cleanedMethod.WithAttributeLists(cleanedMethod.AttributeLists.Insert(0, attributeList));

            var newRoot = root.ReplaceNode(methodDeclaration, newMethod);
            return document.WithSyntaxRoot(newRoot);
        }
    }
}
