using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Nexa.Platform.Updates;
using Nexa.Services.Updates;

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

    private static async ValueTask WindowsUpdateProtectedHighWater()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("NEXA_TEST_PROTECTED_UPDATES") != "1")
        {
            Console.WriteLine("SKIP: protected high-water writes require the dedicated Windows CI fixture.");
            return;
        }
        await WindowsUpdateProtectedHighWaterCore();
    }

    [SupportedOSPlatform("windows")]
    private static async ValueTask WindowsUpdateProtectedHighWaterCore()
    {
        using var identity = WindowsIdentity.GetCurrent();
        AssertTrue(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
        using var root = WindowsUpdateDirectory.Open(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        using var stage = root.CreateStagingDirectory();
        var store = new WindowsUpdateHighWaterStore(stage);
        try
        {
            AssertNull(store.Read());
            store.Advance("2.0.0.alpha.5");
            AssertEqual("2.0.0.alpha.5", new WindowsUpdateHighWaterStore(stage).Read());
            AssertThrows<InvalidOperationException>(() => store.Advance("2.0.0.alpha.5"));
            using (FileStream state = stage.OpenExclusiveStateFile(WindowsUpdateHighWaterStore.StateName))
            {
                WindowsUpdateRejectsSameAccountWrites(identity, state.SafeFileHandle);
                ExpectUpdateFailure<IOException>(() => stage.OpenExclusiveStateFile(WindowsUpdateHighWaterStore.StateName));
                state.Position = state.Length;
                byte[] interrupted = UpdateHighWaterJournal.Encode("2.0.0.alpha.6");
                state.Write(interrupted.AsSpan(0, interrupted.Length / 2));
                state.Flush(true);
            }
            AssertEqual("2.0.0.alpha.5", store.Read());
            store.Advance("2.0.0.alpha.7");
            AssertEqual("2.0.0.alpha.7", store.Read());
            await Task.WhenAll(Enumerable.Range(8, 16).Select(sequence => Task.Run(() =>
            {
                try { new WindowsUpdateHighWaterStore(stage).Advance($"2.0.0.alpha.{sequence}"); }
                catch (InvalidOperationException) { }
            })));
            AssertEqual("2.0.0.alpha.23", store.Read());
            using (FileStream state = stage.OpenExclusiveStateFile(WindowsUpdateHighWaterStore.StateName))
            {
                state.Position = state.Length - 5;
                int original = state.ReadByte();
                state.Position--;
                state.WriteByte((byte)(original ^ 0xff));
                state.Flush(true);
            }
            AssertThrows<InvalidDataException>(() => store.Read());
            AssertThrows<InvalidDataException>(() => store.Advance("2.0.0.beta.1"));
        }
        finally
        {
            using (FileStream state = stage.OpenExclusiveStateFile(WindowsUpdateHighWaterStore.StateName))
                WindowsUpdateDirectory.MarkDelete(state.SafeFileHandle);
            stage.DeleteEmptyStagingDirectory();
        }
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
            WindowsUpdateRejectsSameAccountWrites(identity, file.Stream.SafeFileHandle);
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

    [SupportedOSPlatform("windows")]
    private static unsafe void WindowsUpdateRejectsSameAccountWrites(WindowsIdentity identity, SafeFileHandle file)
    {
        char* buffer = stackalloc char[32768];
        uint length = UpdateFixtureNative.GetFinalPathNameByHandle(file, buffer, 32768, 0);
        if (length is 0 or >= 32768) throw new Win32Exception(Marshal.GetLastPInvokeError());
        string path = new(buffer, 0, (int)length);
        string directory = Path.GetDirectoryName(path)!;
        var administrator = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        byte[] sid = new byte[administrator.BinaryLength];
        administrator.GetBinaryForm(sid, 0);
        SafeAccessTokenHandle token;
        fixed (byte* pointer = sid)
        {
            var disabled = new UpdateFixtureNative.SidAndAttributes { Sid = (nint)pointer };
            if (!UpdateFixtureNative.CreateRestrictedToken(identity.AccessToken, 1, 1, &disabled, 0, 0, 0, 0, out token))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        using (token)
        {
            WindowsIdentity.RunImpersonated(token, () =>
            {
                using var restricted = WindowsIdentity.GetCurrent();
                AssertEqual(identity.User!.Value, restricted.User!.Value);
                AssertFalse(new WindowsPrincipal(restricted).IsInRole(WindowsBuiltInRole.Administrator));
                // No privileged handle is passed to the simulated attacker. These name-based
                // attempts must be denied by the kernel, rather than by adapter policy.
                ExpectUpdateFailure<UnauthorizedAccessException>(() => Directory.CreateDirectory(Path.Combine(directory, "attacker-child")));
                ExpectUpdateFailure<UnauthorizedAccessException>(() => File.WriteAllText(Path.Combine(directory, "attacker-file"), "changed"));
                ExpectUpdateFailure<UnauthorizedAccessException>(() => { using var write = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); });
            });
        }
    }

    private static void ExpectUpdateFailure<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

[SupportedOSPlatform("windows")]
internal static partial class UpdateFixtureNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct SidAndAttributes { internal nint Sid; internal uint Attributes; }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    internal static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* buffer, uint size, uint flags);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool CreateRestrictedToken(SafeAccessTokenHandle existing, uint flags,
        uint disabledCount, SidAndAttributes* disabled, uint deletedCount, nint deleted,
        uint restrictedCount, nint restricted, out SafeAccessTokenHandle token);
}
