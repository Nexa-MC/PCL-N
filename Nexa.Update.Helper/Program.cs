using System.Reflection;
using Nexa.Platform.Updates;
using Nexa.Services.Updates;

namespace Nexa.Update.Helper;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--validate-helper"])
        {
            using Stream embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream("Nexa.Update.ReleaseKey.asc")!;
            using var reader = new StreamReader(embedded);
            _ = new UpdateGpgVerifier(reader.ReadToEnd());
            return 0;
        }
        bool validateInstallation = args is ["--validate-installation"];
        if (args.Length != 2 && !validateInstallation) return 2;
        try
        {
            if (!string.Equals(Environment.ProcessPath, UpdateInstallation.Helper,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new UnauthorizedAccessException("只能运行预装的系统更新 helper。");
            using IUpdateDirectory installation = ProtectedUpdateDirectory.Open(UpdateInstallation.Root);
            using FileStream image = installation.OpenRead(Path.GetFileName(UpdateInstallation.Helper));
            using Stream embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream("Nexa.Update.ReleaseKey.asc")!;
            using var reader = new StreamReader(embedded);
            var verifier = new UpdateGpgVerifier(reader.ReadToEnd());
            if (validateInstallation) return 0;
            string version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
            string rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = true };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(20) };
            var transaction = new AutomaticUpdateTransaction(installation, new(version, rid, "nativeaot-self-contained", "Release"),
                verifier, new GitHubUpdateReleaseSource(client));
            if (args[1] == "rollback") transaction.Rollback();
            else await transaction.InstallAsync(args[0], args[1]).ConfigureAwait(false);
            return 0;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not AccessViolationException)
        {
            // No user log path, plugins, accounts or GUI are opened with administrator rights.
            Console.Error.WriteLine("Nexa update failed: " + failure.Message);
            return 1;
        }
    }
}
