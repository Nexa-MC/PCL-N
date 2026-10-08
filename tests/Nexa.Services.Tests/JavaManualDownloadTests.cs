using System.Text.Json.Nodes;
using Nexa.Services.Composition;
using Nexa.Services.Minecraft.Java;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask ManualJavaRequiresReviewedLicenseAndRegistersActualFacts()
    {
        string root = Path.Combine(Path.GetTempPath(), "nexacl-manual-java-" + Guid.NewGuid().ToString("N"));
        string executable = Path.Combine(root, "java-runtime-test", "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!); await File.WriteAllTextAsync(executable, "fixture");
        try
        {
            using var locator = new RegistrationLocator(new(new(Path.GetDirectoryName(executable)!, executable, null, new Version(21, 0, 2), JavaBrand.Microsoft, JavaArchitecture.X64, true, false)));
            var registry = new ManualRegistry(); var metadata = new ManualMetadata(JavaRuntimeInstaller.DetectPlatform().ToMojangKey());
            var handler = new ManualRuntimeDownloadHandler(); using var transport = new HttpClient(handler);
            using var installer = new JavaRuntimeInstaller(new JavaRuntimeDownloadPlanService(metadata), transport);
            using var service = new JavaManualDownloadService(metadata, installer, locator, registry, root, JavaRuntimeInstaller.DetectPlatform());
            var queries = new XsrQueryRouterBuilder(); var commands = new XsrCommandRouterBuilder(); JavaManualDownloadRuntime.Register(queries, commands, service);
            var queryRouter = queries.Build(new RecordingDispatchObserver()); var commandRouter = commands.Build(new RecordingDispatchObserver());
            AssertTrue(queryRouter.TryResolve(JavaManualDownloadContract.Preview, out var previewRoute)); AssertTrue(commandRouter.TryResolve(JavaManualDownloadContract.Install, out var installRoute));
            AssertFalse((await service.PreviewAsync(new(16, 0))).IsSuccess); AssertFalse((await service.PreviewAsync(new(21, 1))).IsSuccess);
            var preview = (await queryRouter.QueryAsync<JavaManualPreviewQuery, JavaManualPreview>(previewRoute, new(21, 0))).Value;
            AssertEqual(2, preview.Files); AssertEqual(10L, preview.Bytes); AssertEqual(64, preview.PlanFingerprint.Length); AssertEqual(1, preview.LicenseUrls.Count);
            AssertFalse((await commandRouter.Dispatch(installRoute, new JavaManualInstallCommand(preview.Id, false)).Completion).IsSuccess); AssertEqual(0, handler.Requests);
            AssertTrue((await commandRouter.Dispatch(installRoute, new JavaManualInstallCommand(preview.Id, true)).Completion).IsSuccess);
            var receipt = (await service.StatusAsync(new(preview.Id))).Value;
            AssertEqual(JavaManualInstallStatus.Installed, receipt.Status); AssertEqual("21.0.2", receipt.ActualVersion); AssertEqual(JavaBrand.Microsoft, receipt.ActualBrand!.Value);
            AssertEqual(JavaArchitecture.X64, receipt.ActualArchitecture!.Value); AssertEqual(2, handler.Requests);
            AssertEqual(executable, receipt.Executable); AssertFalse(registry.Snapshot.Registrations.Single().Custom); AssertTrue(registry.Snapshot.Registrations.Single().Enabled);
            AssertEqual("hello", await File.ReadAllTextAsync(executable)); AssertEqual("hello", await File.ReadAllTextAsync(Path.Combine(root, "java-runtime-test", "legal", "LICENSE")));
            AssertEqual(executable, (await new JavaRuntimeManagedStore([root]).ReadAsync(CancellationToken.None)).Single().Executable);
            AssertFalse((await service.InstallAsync(new(preview.Id, true))).IsSuccess); AssertEqual(2, handler.Requests);
            metadata.WithLicense = false; AssertFalse((await service.PreviewAsync(new(21, registry.Snapshot.Revision))).IsSuccess);
            metadata.WithLicense = true; metadata.Untrusted = true; AssertFalse((await service.PreviewAsync(new(21, registry.Snapshot.Revision))).IsSuccess);
            metadata.Untrusted = false; metadata.Legacy = true;
            AssertEqual("8u202", (await service.PreviewAsync(new(8, registry.Snapshot.Revision))).Value.Version);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask ManualJavaChangedPlanCannotCreateRecoveryIntentOrDownload()
    {
        string root = Path.Combine(Path.GetTempPath(), "nexacl-manual-plan-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var metadata = new ManualMetadata(JavaRuntimeInstaller.DetectPlatform().ToMojangKey()); var handler = new ManualNoDownloadHandler(); using var http = new HttpClient(handler);
            using var installer = new JavaRuntimeInstaller(new JavaRuntimeDownloadPlanService(metadata), http); using var locator = new RegistrationLocator(null); var registry = new ManualRegistry();
            using var service = new JavaManualDownloadService(metadata, installer, locator, registry, root, JavaRuntimeInstaller.DetectPlatform());
            var preview = (await service.PreviewAsync(new(21, 0))).Value; metadata.Changed = true;
            AssertFalse((await service.InstallAsync(new(preview.Id, true))).IsSuccess); AssertEqual(0, handler.Requests);
            AssertEqual(JavaManualInstallStatus.Failed, (await service.StatusAsync(new(preview.Id))).Value.Status);
            AssertFalse(Directory.Exists(Path.Combine(root, "java-runtime-test")));
            AssertFalse(Directory.Exists(Path.Combine(root, ".nexa-java-jobs")) && Directory.EnumerateDirectories(Path.Combine(root, ".nexa-java-jobs")).Any());
            AssertEqual(0, registry.Snapshot.Registrations.Count);
            using var transport = new HttpClient(new ManualOversizedMetadataHandler()); using var bounded = new HttpJavaRuntimeMetadataProvider(transport);
            bool rejected = false; try { _ = await bounded.GetRuntimeIndexAsync(); } catch (InvalidDataException) { rejected = true; }
            AssertTrue(rejected);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask ManualJavaCancellationAndRegistrationRacesKeepDistinctReceipts()
    {
        string root = Path.Combine(Path.GetTempPath(), "nexacl-manual-cancel-" + Guid.NewGuid().ToString("N"));
        string executable = Path.Combine(root, "java-runtime-test", "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"); Directory.CreateDirectory(Path.GetDirectoryName(executable)!); await File.WriteAllTextAsync(executable, "fixture");
        try
        {
            using var locator = new RegistrationLocator(new(new(Path.GetDirectoryName(executable)!, executable, null, new Version(21, 0, 2), JavaBrand.OpenJdk, JavaArchitecture.X64, true, false)));
            var registry = new ManualRegistry(); var installer = new ManualConfirmedInstaller(executable) { Work = async token => { await Task.Delay(Timeout.Infinite, token); return executable; } };
            using var service = new JavaManualDownloadService(new ManualMetadata(JavaRuntimeInstaller.DetectPlatform().ToMojangKey()), installer, locator, registry, root, JavaRuntimeInstaller.DetectPlatform());
            var first = (await service.PreviewAsync(new(21, 0))).Value; var second = (await service.PreviewAsync(new(21, 0))).Value;
            var pending = service.InstallAsync(new(first.Id, true)).AsTask(); AssertFalse((await service.InstallAsync(new(second.Id, true))).IsSuccess);
            AssertEqual(JavaManualInstallStatus.Downloading, (await service.StatusAsync(new(first.Id))).Value.Status);
            AssertTrue((await service.CancelAsync(new(first.Id))).IsSuccess); AssertFalse((await pending.WaitAsync(TimeSpan.FromSeconds(3))).IsSuccess);
            AssertEqual(JavaManualInstallStatus.Canceled, (await service.StatusAsync(new(first.Id))).Value.Status); AssertEqual(0, registry.Snapshot.Registrations.Count);
            installer.Work = _ => { registry.Snapshot = registry.Snapshot with { Revision = 1 }; return Task.FromResult(executable); };
            AssertFalse((await service.InstallAsync(new(second.Id, true))).IsSuccess);
            var receipt = (await service.StatusAsync(new(second.Id))).Value; AssertEqual(JavaManualInstallStatus.InstalledUnregistered, receipt.Status); AssertEqual("21.0.2", receipt.ActualVersion);
            AssertEqual(0, registry.Snapshot.Registrations.Count);
            installer.Work = null; locator.Candidate = locator.Candidate! with { Installation = new(Path.GetDirectoryName(executable)!, executable, null, new Version(17, 0), JavaBrand.OpenJdk, JavaArchitecture.X64, true, false) };
            var mismatch = (await service.PreviewAsync(new(21, 1))).Value; AssertFalse((await service.InstallAsync(new(mismatch.Id, true))).IsSuccess);
            AssertEqual(JavaManualInstallStatus.InstalledUnverified, (await service.StatusAsync(new(mismatch.Id))).Value.Status);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class ManualRegistry : IJavaRuntimeRegistrationStore
    {
        public JavaRuntimeRegistrySnapshot Snapshot = new(0, []);
        public JavaRuntimeRegistrySnapshot Read() => Snapshot;
        public XsrResult Write(long expectedRevision, IReadOnlyList<JavaRuntimeRegistration> registrations)
        { if (expectedRevision != Snapshot.Revision) return XsrResult.Failure(new(XsrErrorKind.Rejected, JavaManualDownloadContract.Install, "race")); Snapshot = new(expectedRevision + 1, registrations); return XsrResult.Success(); }
    }
    private sealed class ManualConfirmedInstaller(string executable) : IConfirmedJavaRuntimeInstaller
    {
        public int Calls; public string? Fingerprint; public Func<CancellationToken, Task<string>>? Work;
        public Task<string> InstallAsync(string requestedComponent, string runtimeRootDirectory, IProgress<JavaRuntimeInstallProgress>? progress = null, CancellationToken cancellationToken = default) => InstallConfirmedAsync(requestedComponent, runtimeRootDirectory, "", progress, cancellationToken);
        public Task<string> InstallConfirmedAsync(string component, string runtimeRootDirectory, string expectedPlanFingerprint, IProgress<JavaRuntimeInstallProgress>? progress = null, CancellationToken cancellationToken = default)
        { Calls++; Fingerprint = expectedPlanFingerprint; progress?.Report(new("download", 1, 2, 2)); return Work?.Invoke(cancellationToken) ?? Task.FromResult(executable); }
    }
    private sealed class ManualMetadata(string platform) : IJavaRuntimeMetadataProvider
    {
        public bool WithLicense = true, Changed, Untrusted, Legacy;
        public ValueTask<string> GetRuntimeIndexAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new JsonObject { [platform] = new JsonObject { [Legacy ? "jre-legacy" : "java-runtime-test"] = new JsonArray(new JsonObject { ["version"] = new JsonObject { ["name"] = Legacy ? "8u202" : "21.0.2" }, ["manifest"] = new JsonObject { ["url"] = "https://piston-data.mojang.com/runtime.json" } }) } }.ToJsonString());
        }
        public ValueTask<string> GetManifestAsync(string manifestUrl, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonObject File(string name, bool executable = false) => new() { ["executable"] = executable, ["downloads"] = new JsonObject { ["raw"] = new JsonObject { ["url"] = (Untrusted ? "https://example.invalid/" : "https://piston-data.mojang.com/") + name + (Changed ? "-changed" : ""), ["size"] = 5, ["sha1"] = "aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d" } } };
            var files = new JsonObject { [OperatingSystem.IsWindows() ? "bin/java.exe" : "bin/java"] = File("java", true) }; if (WithLicense) files["legal/LICENSE"] = File("license");
            return ValueTask.FromResult(new JsonObject { ["files"] = files }.ToJsonString());
        }
    }
    private sealed class ManualNoDownloadHandler : HttpMessageHandler
    { public int Requests; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Requests++; throw new InvalidOperationException("No bytes may be acquired for an unreviewed plan."); } }
    private sealed class ManualRuntimeDownloadHandler : HttpMessageHandler
    { public int Requests; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Requests++; return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("hello") }); } }
    private sealed class ManualOversizedMetadataHandler : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[4 * 1024 * 1024 + 1])) }); }
}
