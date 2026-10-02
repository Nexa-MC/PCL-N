using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nexa.Xsr.ArchitectureTests;

internal static partial class Program
{
    private static bool IsServiceImplementation(string name) => name == "Nexa.Services"
        || (name.StartsWith("Nexa.Services.", StringComparison.Ordinal)
            && !name.EndsWith(".Contracts", StringComparison.Ordinal) && name != "Nexa.Services.Composition");

    private static void ValidateSemanticBoundaries(string root, Dictionary<string, string> projects, List<string> failures)
    {
        // Resolve the compiler's assembly owners, including aliases and type forwards.
        // No source-text spelling can bypass a forbidden API or a contract boundary.
        var libraries = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat(projects.Values.SelectMany(p => Directory.EnumerateFiles(Path.Combine(Path.GetDirectoryName(p)!, "bin"), "*.dll", SearchOption.AllDirectories)))
            .GroupBy(Path.GetFileName, StringComparer.Ordinal).Select(g => g.First()).ToArray();
        foreach ((string projectName, string projectPath) in projects)
        {
            bool renderer = projectName.StartsWith("Nexa.UI.Next", StringComparison.Ordinal);
            if (projectName.EndsWith(".Tests", StringComparison.Ordinal) || (!projectName.StartsWith("Nexa.Services", StringComparison.Ordinal) && projectName != "Nexa.Desktop" && !renderer)) continue;
            string directory = Path.GetDirectoryName(projectPath)!;
            var trees = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories).Where(p => !IsBuildOutput(p))
                .Select(p => CSharpSyntaxTree.ParseText(File.ReadAllText(p), path: p)).ToList();
            trees.Add(CSharpSyntaxTree.ParseText("global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; global using System.Net.Http; global using System.Threading; global using System.Threading.Tasks;"));
            var references = libraries.Where(p => Path.GetFileNameWithoutExtension(p) != projectName)
                .Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create(projectName, trees, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            foreach (var tree in trees.Where(t => t.FilePath.Length > 0))
            {
                var model = compilation.GetSemanticModel(tree);
                bool presentation = projectName == "Nexa.Desktop" && tree.FilePath.Contains(Path.DirectorySeparatorChar + "Ui" + Path.DirectorySeparatorChar, StringComparison.Ordinal);
                foreach (var node in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
                {
                    ISymbol? symbol = model.GetSymbolInfo(node).Symbol;
                    INamedTypeSymbol? type = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
                    string? owner = type?.ContainingAssembly.Name;
                    string location = $"{Path.GetRelativePath(root, tree.FilePath)}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";
                    if (renderer && type is not null && (owner?.StartsWith("Nexa.Sidecar.", StringComparison.Ordinal) == true
                        || (owner == "Nexa.Xsr.Runtime" && (type.Name.StartsWith("Sidecar", StringComparison.Ordinal)
                            || type.Name.StartsWith("XsrSignal", StringComparison.Ordinal)
                            || type.Name.StartsWith("XsrUiPatchRuntime", StringComparison.Ordinal)
                            || type.Name.StartsWith("XsrUiPatchAdmission", StringComparison.Ordinal)
                            || type.Name.StartsWith("XsrUiModuleRuntime", StringComparison.Ordinal)
                            || type.Name.StartsWith("XsrUiModuleAdmission", StringComparison.Ordinal)
                            || type.Name.StartsWith("XsrFunctionPatch", StringComparison.Ordinal)))))
                        failures.Add($"Renderer must emit host intent rather than access Sidecar execution: {symbol} at {location}.");
                    if (presentation && owner is not null && IsServiceImplementation(owner))
                        failures.Add($"UI must consume contracts: {symbol} belongs to {owner} at {location}.");
                    if (projectName.StartsWith("Nexa.Services", StringComparison.Ordinal)
                        && owner is not null && (owner.StartsWith("Nexa.UI.", StringComparison.Ordinal) || owner == "Nexa.Desktop" || owner.StartsWith("Avalonia", StringComparison.Ordinal)))
                        failures.Add($"Service references renderer {owner} at {location}.");
                    if (symbol is IMethodSymbol method)
                    {
                        string fullType = method.ContainingType.ToDisplayString();
                        bool blocking = (method.Name == "Wait" && fullType.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal))
                            || (method.Name == "GetResult" && fullType.StartsWith("System.Runtime.CompilerServices.", StringComparison.Ordinal) && fullType.Contains("Awaiter", StringComparison.Ordinal))
                            || (method.Name == "WaitForExit" && fullType == "System.Diagnostics.Process");
                        bool nativeStart = fullType == "System.Diagnostics.Process" && method.Name is "Start" or "GetProcessById";
                        if ((presentation || projectName.StartsWith("Nexa.Services", StringComparison.Ordinal)) && (blocking || nativeStart))
                            failures.Add($"Forbidden {(blocking ? "blocking" : "platform")} API {method} at {location}.");
                    }
                }
            }
        }
    }
}
