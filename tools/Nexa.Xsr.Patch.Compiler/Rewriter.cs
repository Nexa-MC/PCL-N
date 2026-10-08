using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nexa.Xsr.Patch.Compiler;

internal static partial class Program
{
    internal static string Rewrite(string source, string sourcePath)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), sourcePath);
        var root = tree.GetRoot();
        if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)
            || root.DescendantTrivia(descendIntoTrivia: true).Any(t => t.IsDirective)) throw Invalid("invalid syntax or directives");
        HashSet<string> targets = new(StringComparer.Ordinal);
        List<(int Start, int Length, string Replacement)> edits = [];
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var markers = method.AttributeLists.SelectMany(list => list.Attributes).Where(IsMarker).ToArray();
            if (markers.Length == 0) continue;
            if (markers.Length != 1 || method.AttributeLists.Sum(list => list.Attributes.Count) != 1
                || markers[0].ArgumentList?.Arguments is not { Count: 1 } arguments
                || arguments[0].Expression is not LiteralExpressionSyntax literal || !literal.IsKind(SyntaxKind.StringLiteralExpression))
                throw Invalid("marker must have exactly one literal target and no additional attributes");
            string target = literal.Token.ValueText;
            if (target.Length is < 1 or > 256 || target.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || !targets.Add(target))
                throw Invalid("invalid or duplicate target");
            if (edits.Count == 32) throw Invalid("source method budget exceeded");
            if (method.Parent is not ClassDeclarationSyntax owner || owner.TypeParameterList is not null
                || !owner.Modifiers.Any(SyntaxKind.StaticKeyword)
                || method.Ancestors().OfType<TypeDeclarationSyntax>().Any(type => type.TypeParameterList is not null || !type.Modifiers.Any(SyntaxKind.StaticKeyword))
                || !method.Modifiers.Any(SyntaxKind.StaticKeyword)
                || method.Modifiers.Any(token => token.Kind() is not (SyntaxKind.StaticKeyword or SyntaxKind.PrivateKeyword or SyntaxKind.InternalKeyword or SyntaxKind.PublicKeyword))
                || method.TypeParameterList is not null || !Scalar(method.ReturnType.ToString(), allowVoid: true)
                || method.ParameterList.Parameters.Count is < 2 or > 10 || (method.Body is null && method.ExpressionBody is null))
                throw Invalid("only nongeneric static primitive ABI methods are supported");
            var parameters = method.ParameterList.Parameters;
            if (!TypeIs(parameters[0].Type, "XsrFunctionPatchRuntime") || !TypeIs(parameters[1].Type, "XsrFunctionPatchPoint")
                || parameters.Skip(2).Any(p => !Scalar(p.Type?.ToString(), allowVoid: false))
                || parameters.Any(p => p.Modifiers.Count > 0 || p.Default is not null || p.AttributeLists.Count > 0))
                throw Invalid("method parameters must be runtime, point and up to eight primitive values without modifiers or defaults");
            SyntaxNode body = (SyntaxNode?)method.Body ?? method.ExpressionBody!;
            if (!IsSupportedBody(body, parameters.Skip(2).Select(p => p.Identifier.ValueText).ToArray()))
                throw Invalid("body uses unsupported call/caller-info semantics or patch context");
            string name = method.Identifier.ValueText;
            string originalName = "__NexaOriginal_" + name, delegateName = "__NexaOriginalDelegate_" + name;
            if (owner.Members.OfType<MethodDeclarationSyntax>().Count(m => m.Identifier.ValueText == name) != 1
                || owner.DescendantTokens().Any(t => t.IsKind(SyntaxKind.IdentifierToken) && (t.ValueText == originalName || t.ValueText == delegateName)))
                throw Invalid("overload or generated name collision");
            string runtime = parameters[0].Identifier.Text, point = parameters[1].Identifier.Text;
            string resultType = method.ReturnType.ToString();
            var values = parameters.Skip(2).ToArray();
            string invocationArguments = string.Join(", ", values.Select(p => p.Identifier.Text));
            string typedParameters = string.Join(", ", values.Select(p => p.Type + " " + p.Identifier.Text));
            bool legacy = resultType == "string" && values.Length == 1 && values[0].Type?.ToString() == "string";
            string invocation;
            string delegateDeclaration;
            if (legacy)
            {
                invocation = $"return {runtime}.Invoke({point}, {invocationArguments}, {delegateName});";
                delegateDeclaration = "private static readonly global::System.Func<string, string> " + delegateName + " = " + originalName + ";";
            }
            else
            {
                string call = originalName + "(" + invocationArguments + ")";
                string unpatched = resultType == "void" ? call + "; return;" : "return " + call + ";";
                string packed = "[" + string.Join(", ", values.Select(p => "global::Nexa.Xsr.Runtime.XsrFunctionValue.From(" + p.Identifier.Text + ")")) + "]";
                string dispatch = runtime + ".InvokeValues(" + point + ", " + packed + ", " + delegateName + ")";
                invocation = "if (!" + runtime + ".HasPatches(" + point + ")) { " + unpatched + " } "
                    + (resultType == "void" ? dispatch + ";" : "return " + dispatch + "." + Accessor(resultType) + "();");
                string originalCall = originalName + "(" + string.Join(", ", values.Select((p, i) => "__values[" + i + "]." + Accessor(p.Type!.ToString()) + "()")) + ")";
                string adapter = resultType == "void" ? "{ " + originalCall + "; return global::Nexa.Xsr.Runtime.XsrFunctionValue.Empty; }"
                    : "global::Nexa.Xsr.Runtime.XsrFunctionValue.From(" + originalCall + ")";
                delegateDeclaration = "private static readonly global::Nexa.Xsr.Runtime.XsrFunctionOriginal " + delegateName + " = static __values => " + adapter + ";";
            }
            var wrapper = method.WithAttributeLists(default).WithBody(SyntaxFactory.ParseStatement("{ " + invocation + " }") as BlockSyntax)
                .WithExpressionBody(null).WithSemicolonToken(default).NormalizeWhitespace().ToFullString();
            int line = body.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            string pathLiteral = SymbolDisplay.FormatLiteral(sourcePath.Replace('\\', '/'), quote: true);
            string originalBody = body.ToFullString() + (method.ExpressionBody is null ? "" : ";");
            string replacement = wrapper + "\n\n" + delegateDeclaration + "\n\nprivate static " + resultType + " " + originalName + "(" + typedParameters + ")\n#line " + line + " " + pathLiteral
                + "\n" + originalBody + "\n#line default\n";
            edits.Add((method.SpanStart, method.Span.Length, replacement));
        }
        if (edits.Count == 0) throw Invalid("source contains no explicit Function Patch method");
        StringBuilder result = new(source);
        foreach (var edit in edits.OrderByDescending(e => e.Start)) result.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Replacement);
        return "// <auto-generated/>\n" + result;
    }

    private static bool IsMarker(AttributeSyntax attribute) => attribute.Name.ToString() is
        "XsrFunctionPatch" or "XsrFunctionPatchAttribute" or "Nexa.Xsr.Runtime.XsrFunctionPatch"
        or "Nexa.Xsr.Runtime.XsrFunctionPatchAttribute" or "global::Nexa.Xsr.Runtime.XsrFunctionPatchAttribute";
    private static bool TypeIs(TypeSyntax? type, string name) => type?.ToString() is { } text
        && (text == name || text == "Nexa.Xsr.Runtime." + name || text == "global::Nexa.Xsr.Runtime." + name);
    private static bool Scalar(string? name, bool allowVoid) => name is "string" or "bool" or "int" or "long" or "double" || allowVoid && name == "void";
    private static string Accessor(string name) => name switch { "string" => "AsString", "bool" => "AsBoolean", "int" => "AsInt32", "long" => "AsInt64", "double" => "AsFloat64", _ => throw Invalid("unsupported scalar") };
    private static bool IsSupportedBody(SyntaxNode node, string[] value) => node switch
    {
        BlockSyntax block => block.Statements.All(statement => IsSupportedBody(statement, value)),
        ReturnStatementSyntax statement => statement.Expression is null || IsSupportedBody(statement.Expression, value),
        IfStatementSyntax statement => IsSupportedBody(statement.Condition, value) && IsSupportedBody(statement.Statement, value)
            && (statement.Else is null || IsSupportedBody(statement.Else.Statement, value)),
        ArrowExpressionClauseSyntax arrow => IsSupportedBody(arrow.Expression, value),
        LiteralExpressionSyntax => true,
        IdentifierNameSyntax name => value.Contains(name.Identifier.ValueText, StringComparer.Ordinal),
        MemberAccessExpressionSyntax member => member.Expression is IdentifierNameSyntax name && value.Contains(name.Identifier.ValueText, StringComparer.Ordinal)
            && member.Name.Identifier.ValueText == "Length",
        BinaryExpressionSyntax binary => IsSupportedBody(binary.Left, value) && IsSupportedBody(binary.Right, value),
        ConditionalExpressionSyntax conditional => IsSupportedBody(conditional.Condition, value)
            && IsSupportedBody(conditional.WhenTrue, value) && IsSupportedBody(conditional.WhenFalse, value),
        ParenthesizedExpressionSyntax parentheses => IsSupportedBody(parentheses.Expression, value),
        PrefixUnaryExpressionSyntax unary => IsSupportedBody(unary.Operand, value),
        _ => false,
    };
    private static InvalidDataException Invalid(string detail) => new("NXRP001: " + detail + ".");
}
