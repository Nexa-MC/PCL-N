using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Nexa.Xsr.Patch.Compiler;

internal static partial class Program
{
    private const string ValidSource = """
        using Nexa.Xsr.Runtime;
        public static class Demo
        {
            [XsrFunctionPatch("ui.resource.project-title.v1")]
            public static string Read(XsrFunctionPatchRuntime runtime, XsrFunctionPatchPoint point, string text)
            {
                if (text.Length == 0) return "(empty)";
                return text;
            }
        }
        """;
    private const string Shim = """
        namespace Nexa.Xsr.Runtime
        {
            public sealed class XsrFunctionPatchPoint { }
            public sealed class XsrFunctionPatchRuntime
            {
                public string Invoke(XsrFunctionPatchPoint point, string value, System.Func<string, string> original)
                    => original(value) + ":patched";
            }
        }
        """;

    private static int SelfTest()
    {
        string rewritten = Rewrite(ValidSource, "Fixture.cs");
        Require(rewritten.Contains("#line 6 \"Fixture.cs\"", StringComparison.Ordinal), "original debug body location");
        Require(!rewritten.Contains("[XsrFunctionPatch", StringComparison.Ordinal), "marker removed");
        Require(rewritten.Contains("static readonly global::System.Func", StringComparison.Ordinal), "cached delegate");
        var trees = new[] { CSharpSyntaxTree.ParseText(rewritten), CSharpSyntaxTree.ParseText(Shim) };
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("RewrittenFixture", trees, references, new(OutputKind.DynamicallyLinkedLibrary));
        using MemoryStream emitted = new();
        var emit = compilation.Emit(emitted);
        Require(emit.Success, "rewritten compilation: " + string.Join("; ", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        emitted.Position = 0;
        AssemblyLoadContext load = new("patch-compiler-self-test", isCollectible: true);
        try
        {
            var assembly = load.LoadFromStream(emitted);
            var demo = assembly.GetType("Demo")!;
            var runtime = Activator.CreateInstance(assembly.GetType("Nexa.Xsr.Runtime.XsrFunctionPatchRuntime")!);
            var point = Activator.CreateInstance(assembly.GetType("Nexa.Xsr.Runtime.XsrFunctionPatchPoint")!);
            var read = demo.GetMethod("Read")!;
            Require((string)read.Invoke(null, [runtime, point, "value"])! == "value:patched", "wrapper executes interpreter");
            Require((string)read.Invoke(null, [runtime, point, ""])! == "(empty):patched", "original branching/return retained");
        }
        finally { load.Unload(); }
        const string expression = "using Nexa.Xsr.Runtime; public static class Expression { [XsrFunctionPatch(\"ui.expression.v1\")] public static string Read(XsrFunctionPatchRuntime runtime, XsrFunctionPatchPoint point, string text) => text; }";
        var expressionTree = CSharpSyntaxTree.ParseText(Rewrite(expression, "Expression.cs"));
        Require(!expressionTree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "expression body parses");
        string[] invalid =
        [
            ValidSource.Replace("static string Read", "static async string Read", StringComparison.Ordinal),
            ValidSource.Replace("static string Read", "string Read", StringComparison.Ordinal),
            ValidSource.Replace("Read(", "Read<T>(", StringComparison.Ordinal),
            ValidSource.Replace("string text)", "ref string text)", StringComparison.Ordinal),
            ValidSource.Replace("string text)", "string text = \"default\")", StringComparison.Ordinal),
            ValidSource.Replace("return text;", "return text.Trim();", StringComparison.Ordinal),
            ValidSource.Replace("return text;", "return nameof(Read);", StringComparison.Ordinal),
            ValidSource.Replace("return text;", "return runtime.ToString();", StringComparison.Ordinal),
            ValidSource.Replace("return text;", "return new string('x', 1);", StringComparison.Ordinal),
            ValidSource.Replace("return text;", "return text[0].ToString();", StringComparison.Ordinal),
            ValidSource.Replace("return text;", "while (true) { }", StringComparison.Ordinal),
            ValidSource.Replace("[XsrFunctionPatch", "[System.Obsolete][XsrFunctionPatch", StringComparison.Ordinal),
            ValidSource.Replace("ui.resource.project-title.v1", "invalid target", StringComparison.Ordinal),
            ValidSource.Replace("return text;", "return __NexaOriginal_Read;", StringComparison.Ordinal),
            "#nullable enable\n" + ValidSource,
            "public static class Empty { }",
            ValidSource + ValidSource.Replace("Demo", "Another", StringComparison.Ordinal).Replace("using Nexa.Xsr.Runtime;", "", StringComparison.Ordinal),
            rewritten,
        ];
        foreach (string input in invalid)
        {
            bool rejected = false;
            try { _ = Rewrite(input, "Invalid.cs"); }
            catch (InvalidDataException error) when (error.Message.StartsWith("NXRP001:", StringComparison.Ordinal)) { rejected = true; }
            Require(rejected, "unsupported input rejected");
        }
        Console.WriteLine($"Function Patch compiler self-tests passed: compilation/execution, expression body, debug mapping, cached delegate and {invalid.Length} rejection cases.");
        return 0;
    }
    private static void Require(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException("Compiler self-test failed: " + reason); }
}
