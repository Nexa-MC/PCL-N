using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Nexa.Platform.Updates;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void WindowsUpdateDescriptorPolicy()
    {
        if (!OperatingSystem.IsWindows()) return;
        WindowsUpdateDescriptorPolicyCore();
    }

    [SupportedOSPlatform("windows")]
    private static void WindowsUpdateDescriptorPolicyCore()
    {
        AssertTrue(WindowsUpdateDirectory.IsProtected(new RawSecurityDescriptor("O:BAG:BAD:P(A;;FA;;;BA)(A;;FR;;;BU)"), false));
        AssertFalse(WindowsUpdateDirectory.IsProtected(new RawSecurityDescriptor("O:BUG:BUD:P(A;;FA;;;BA)"), false));
        AssertFalse(WindowsUpdateDirectory.IsProtected(new RawSecurityDescriptor(ControlFlags.None, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), null, null, null), false));
        foreach (string right in new[] { "FW", "GA", "WD", "WO", "SD", "0x40", "0x2", "0x4", "0x10", "0x100" })
        {
            // Deny ACEs do not turn an unsafe grant into accepted authority.
            AssertFalse(WindowsUpdateDirectory.IsProtected(new RawSecurityDescriptor($"O:BAG:BAD:P(D;;FA;;;BU)(A;;{right};;;BU)"), false));
        }
        var createOnly = new RawSecurityDescriptor("O:BAG:BAD:P(A;;0x6;;;AU)(A;IOCI;FA;;;AU)(A;;FA;;;BA)");
        AssertTrue(WindowsUpdateDirectory.IsProtected(createOnly, true));
        AssertFalse(WindowsUpdateDirectory.IsProtected(createOnly, false));
        var callback = new RawSecurityDescriptor("O:BAG:BAD:P(A;;FA;;;BA)");
        callback.DiscretionaryAcl!.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 1,
            new SecurityIdentifier(WellKnownSidType.WorldSid, null), true, null));
        AssertFalse(WindowsUpdateDirectory.IsProtected(callback, false));
        foreach (string name in new[] { "..", "a/b", "a\\b", "a:stream", "CON.txt", "LPT1", "a.", "a " })
            ExpectUpdateFailure<ArgumentException>(() => WindowsUpdateDirectory.ValidateLeaf(name));
    }

    private static void WindowsUpdateReadAdmission()
    {
        if (!OperatingSystem.IsWindows()) return;
        WindowsUpdateReadAdmissionCore();
    }

    [SupportedOSPlatform("windows")]
    private static void WindowsUpdateReadAdmissionCore()
    {
        using var directory = WindowsUpdateDirectory.Open(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        ExpectUpdateFailure<InvalidOperationException>(() => directory.CreateFile("not-created"));
        ExpectUpdateFailure<InvalidOperationException>(directory.DeleteEmptyStagingDirectory);
        ExpectUpdateFailure<UnauthorizedAccessException>(() => { using var rejected = WindowsUpdateDirectory.Open(Path.GetTempPath()); });
        directory.Dispose();
        ExpectUpdateFailure<ObjectDisposedException>(() => directory.OpenReadFile("not-opened"));
    }

    private static async ValueTask WindowsUpdateElevatedObjects()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("NEXA_TEST_PROTECTED_UPDATES") != "1")
        {
            Console.WriteLine("SKIP: elevated update writes require the dedicated Windows CI fixture.");
            return;
        }
        await WindowsUpdateElevatedObjectsCore();
    }

    [SupportedOSPlatform("windows")]
    private static async ValueTask WindowsUpdateElevatedObjectsCore()
    {
        using var identity = WindowsIdentity.GetCurrent();
        AssertTrue(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
        using var parent = WindowsUpdateDirectory.Open(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        using var stage = parent.CreateStagingDirectory();
        parent.Dispose(); // The stage must retain its own ancestor leases.
        WindowsUpdateFile? file = null;
        try
        {
            file = stage.CreateFile("package.bin");
            var release = await ReceptionReleaseAsync();
            await release.CopyVerifiedPackageAsync(new MemoryStream(ReceptionPackage), file.Stream);
            file.Stream.Flush(true);
            ExpectUpdateFailure<IOException>(() => stage.CreateFile("package.bin"));
            ExpectUpdateFailure<IOException>(() => stage.OpenReadFile("package.bin"));
            byte[] received = new byte[ReceptionPackage.Length];
            await file.Stream.ReadExactlyAsync(received);
            AssertTrue(received.SequenceEqual(ReceptionPackage));
            ExpectUpdateFailure<ArgumentException>(() => stage.CreateFile("../outside"));
            ExpectUpdateFailure<IOException>(stage.DeleteEmptyStagingDirectory);
        }
        finally
        {
            file?.Delete();
            stage.DeleteEmptyStagingDirectory();
        }
    }

    private static void ExpectUpdateFailure<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
