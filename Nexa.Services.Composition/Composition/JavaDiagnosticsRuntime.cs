using Nexa.Platform;
using Nexa.Services.Foundation;
using Nexa.Services.Minecraft.Java;
using Nexa.Xsr.Runtime;

namespace Nexa.Services.Composition;

public static class JavaDiagnosticsRuntime
{
    public static void Register(XsrQueryRouterBuilder queries, FoundationHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var inventory = new JavaRuntimeInventoryService(host.JavaLocator, host.JavaRegistrations, host.JavaManagedRuntimeRoots);
        Register(queries, new JavaRuntimeDiagnosticsService(inventory, new PlatformJavaDiagnostics()));
    }
    public static void Register(XsrQueryRouterBuilder queries, JavaRuntimeDiagnosticsService diagnostics)
    {
        ArgumentNullException.ThrowIfNull(queries); ArgumentNullException.ThrowIfNull(diagnostics);
        queries.Register<JavaRuntimePropertiesQuery, JavaRuntimeDiagnosticsSnapshot>(JavaRuntimeDiagnosticsContract.Properties, diagnostics.ReadPropertiesAsync);
        queries.Register<JavaRuntimeModulesQuery, JavaRuntimeDiagnosticsSnapshot>(JavaRuntimeDiagnosticsContract.Modules, diagnostics.ReadModulesAsync);
    }
}
