using System.Security.Cryptography;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;

namespace Nexa.Xsr.Runtime.Tests;

internal static partial class Program
{
    private static async ValueTask ExtensionsCommitAtomicallyAndRetireWithSession()
    {
        foreach (bool corrupt in new[] { false, true })
        {
            var (hostStream, pluginStream) = SidecarLoopbackStream.CreatePair();
            using var host = new SidecarConnection(hostStream);
            using var plugin = new SidecarConnection(pluginStream);
            using var session = new SidecarHostSession(host, "ExtensionFixture");
            await CompleteHandshake(session, plugin);
            Task<SidecarStateMirror> accepting = session.AcceptRegistrationAsync().AsTask();
            await SupervisorFrameAsync(plugin, SidecarMessageType.RegisterBegin, SidecarRegistration.EncodeBegin(6), default);
            byte[] payload = [1, 2, 3];
            foreach (var kind in Enum.GetValues<SidecarRegistrationKind>().Where(SidecarRegistration.IsExtension))
            {
                byte[] hash = SHA256.HashData(payload);
                if (corrupt && kind == SidecarRegistrationKind.FunctionPatch) hash[0] ^= 1;
                await SupervisorFrameAsync(plugin, SidecarMessageType.RegisterItem,
                    SidecarRegistration.EncodeItem(new(kind, "extension." + (uint)kind, 0, 0, payload, hash, TargetSemanticId: "host.target")), default);
            }
            await SupervisorFrameAsync(plugin, SidecarMessageType.RegisterEnd, [], default);
            if (corrupt)
            {
                await AssertThrowsAsync<SidecarProtocolException>(() => accepting);
                AssertEqual(0, session.Extensions.Entries.Count);
                AssertTrue(session.Registration is null);
                AssertEqual(SidecarSessionState.Failed, session.State);
            }
            else
            {
                await accepting;
                AssertEqual(6, session.Extensions.Entries.Count);
                session.Dispose();
                AssertEqual(0, session.Extensions.Entries.Count);
            }
        }
    }

    private static async ValueTask RenamedExecutableConnectsAndRegistersExtensions()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nexa-supervisor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string nativeHost = Path.Combine(AppContext.BaseDirectory,
                OperatingSystem.IsWindows() ? "Nexa.Xsr.Runtime.Tests.exe" : "Nexa.Xsr.Runtime.Tests");
            // NativeAOT has no DLL dependencies; CoreCLR apphost uses adjacent test assemblies.
            foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory))
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            string path = Path.Combine(directory, "provider.nsc");
            File.Copy(nativeHost, path);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await File.WriteAllBytesAsync(path + ".asc", SHA256.HashData(await File.ReadAllBytesAsync(path)));
            File.Copy(nativeHost, Path.Combine(directory, "untrusted.nsc")); // Missing signature must never start.
            var reports = new List<string>();
            await using var supervisor = new SidecarSupervisor(async (image, signature, token) =>
            {
                byte[] actual = await SHA256.HashDataAsync(image, token);
                byte[] expected = new byte[32];
                await signature.ReadExactlyAsync(expected, token);
                if (!actual.SequenceEqual(expected)) throw new InvalidDataException("Fixture signature mismatch.");
            }, (_, status) => { lock (reports) reports.Add(status); });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await supervisor.StartAsync(directory, deadline.Token);
            SidecarHostSession session = supervisor.Sessions.Single();
            AssertEqual(6, session.Extensions.Entries.Count);
            AssertEqual(1, session.Registration!.UiModules.Count());
            AssertTrue(session.Cache.TryOpenUiModule(XsrSemanticId.Parse("plugin.page"), out _));
            foreach (var extension in session.Extensions.Entries)
                AssertEqual(XsrSemanticId.Parse("host.target"), extension.Target);
            AssertTrue(reports.Any(status => status.StartsWith("failed:", StringComparison.Ordinal)));
            await supervisor.DisposeAsync();
            AssertEqual(0, supervisor.Sessions.Count);
            AssertEqual(0, session.Extensions.Entries.Count);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task<int> RunSupervisorChildAsync(string endpoint)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using Stream stream = await SidecarBootstrap.ConnectAsync(endpoint, Console.OpenStandardInput(), deadline.Token);
        using var connection = new SidecarConnection(stream);
        SidecarFrame hello = await connection.ReceiveAsync(deadline.Token);
        AssertEqual(SidecarMessageType.Hello, hello.MessageType);
        await connection.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.Welcome, SidecarFrameTraits.None,
            hello.CorrelationId, SidecarHandshake.EncodeWelcome(SidecarProtocol.Version, Guid.NewGuid())), deadline.Token);
        await SupervisorFrameAsync(connection, SidecarMessageType.RegisterBegin, SidecarRegistration.EncodeBegin(7), deadline.Token);
        byte[] payload = [1, 2, 3];
        byte[] hash = SHA256.HashData(payload);
        foreach (var kind in Enum.GetValues<SidecarRegistrationKind>().Where(SidecarRegistration.IsExtension))
        {
            var item = new SidecarRegistrationItem(kind, "plugin.extension." + (uint)kind, 0, 0, payload, hash, TargetSemanticId: "host.target");
            await SupervisorFrameAsync(connection, SidecarMessageType.RegisterItem, SidecarRegistration.EncodeItem(item), deadline.Token);
        }
        await SupervisorFrameAsync(connection, SidecarMessageType.RegisterItem,
            SidecarRegistration.EncodeItem(new(SidecarRegistrationKind.UiModule, "plugin.page", 0, 0, payload, hash)), deadline.Token);
        await SupervisorFrameAsync(connection, SidecarMessageType.RegisterEnd, [], deadline.Token);
        await SupervisorFrameAsync(connection, SidecarMessageType.StateSnapshotBegin, SidecarStateSnapshot.EncodeBegin(0), deadline.Token);
        await SupervisorFrameAsync(connection, SidecarMessageType.StateSnapshotEnd, [], deadline.Token);
        AssertEqual(SidecarMessageType.Ready, (await connection.ReceiveAsync(deadline.Token)).MessageType);
        AssertEqual(SidecarMessageType.Activate, (await connection.ReceiveAsync(deadline.Token)).MessageType);
        try { await connection.ReceiveAsync(deadline.Token); }
        catch (IOException) { }
        return 0;
    }

    private static ValueTask SupervisorFrameAsync(SidecarConnection connection, SidecarMessageType type, byte[] payload, CancellationToken token) =>
        connection.SendAsync(new(SidecarProtocol.Version, type, SidecarFrameTraits.None, SidecarCorrelationId.Create(), payload), token);
}
