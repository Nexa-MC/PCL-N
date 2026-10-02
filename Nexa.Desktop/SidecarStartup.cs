using Nexa.Services.Logging;
using Nexa.Services.Updates;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop;

internal static class SidecarStartup
{
    public static SidecarSupervisor Create(LogService log, XsrFunctionPatchAdmission? functionPatches = null,
        XsrSignalAdmission? signals = null, XsrUiPatchAdmission? uiPatches = null, XsrUiModuleAdmission? uiModules = null)
    {
        using Stream key = typeof(SidecarStartup).Assembly.GetManifestResourceStream("Nexa.Desktop.SidecarReleaseKey.asc")
            ?? throw new InvalidDataException("The pinned Sidecar verification key is missing.");
        using var reader = new StreamReader(key);
        var verifier = new UpdateGpgVerifier(reader.ReadToEnd());
        return new(verifier.VerifyAsync, (name, status) => log.Info("Sidecar", $"{name}: {status}"))
        { FunctionPatchAdmission = functionPatches, SignalAdmission = signals, UiPatchAdmission = uiPatches, UiModuleAdmission = uiModules };
    }

    public static async Task StartAsync(SidecarSupervisor supervisor, LogService log)
    {
        try { await supervisor.StartAsync(AppContext.BaseDirectory).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { log.Warn("Sidecar", "Sidecar discovery failed: " + error.Message); }
    }
}
