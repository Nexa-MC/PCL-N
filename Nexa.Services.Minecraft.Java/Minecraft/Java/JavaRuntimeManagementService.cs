using Nexa.Core;
using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Java;

public sealed class JavaRuntimeManagementService(IJavaRuntimeLocator locator, IJavaRuntimeRegistrationStore registry,
    IReadOnlyList<string>? managedRuntimeRoots)
{
    public JavaRuntimeManagementService(IJavaRuntimeLocator locator, IJavaRuntimeRegistrationStore registry)
        : this(locator, registry, null) { }

    public ValueTask<XsrResult> ManageAsync(JavaRuntimeManageCommand command, CancellationToken cancellationToken = default)
        => new(Task.Run(async () =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Enum.IsDefined(command.Action) || string.IsNullOrWhiteSpace(command.Executable) || command.Executable.Length > 4096 || command.Executable.Any(char.IsControl) || !Path.IsPathFullyQualified(command.Executable))
                    return Rejected("请选择有效的 Java 可执行文件。");
                string executable = Path.GetFullPath(command.Executable);
                var snapshot = registry.Read();
                if (snapshot.Revision != command.ExpectedRevision) return Rejected("Java 列表已变化，请刷新后重试。");
                if (command.Action == JavaRuntimeManagementAction.DeleteManaged)
                    return await DeleteManagedAsync(command, snapshot, cancellationToken).ConfigureAwait(false);
                List<JavaRuntimeRegistration> entries = [.. snapshot.Registrations];
                if (command.Action == JavaRuntimeManagementAction.Add)
                {
                    var candidate = await locator.InspectAsync(executable, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (candidate is not { IsAvailable: true }) return Rejected("此文件不是可用的 Java 运行时。");
                    executable = candidate.Installation.JavaExecutablePath;
                    entries.RemoveAll(entry => PathIdentity.Comparer.Equals(entry.Executable, executable));
                    entries.Add(new(executable));
                }
                else
                {
                    int index = entries.FindIndex(entry => PathIdentity.Comparer.Equals(entry.Executable, executable));
                    if (command.Action == JavaRuntimeManagementAction.Remove)
                    {
                        if (index < 0 || !entries[index].Custom) return Rejected("只能移除手动添加的 Java 登记。");
                        entries.RemoveAt(index);
                    }
                    else
                    {
                        // An unavailable custom registration must still be removable/enablable.
                        if (index < 0 && await locator.InspectAsync(executable, cancellationToken).ConfigureAwait(false) is null)
                            return Rejected("Java 已变化，请重新扫描。");
                        var entry = index < 0 ? new JavaRuntimeRegistration(executable, Custom: false) : entries[index];
                        var updated = entry with { Enabled = command.Action == JavaRuntimeManagementAction.Enable };
                        if (index < 0) entries.Add(updated); else entries[index] = updated;
                    }
                }
                if (entries.Count > 64) return Rejected("最多登记 64 个 Java 运行时。");
                cancellationToken.ThrowIfCancellationRequested();
                var result = registry.Write(snapshot.Revision, entries.AsReadOnly());
                if (result.IsSuccess) locator.Invalidate();
                return result;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OverflowException or System.Text.Json.JsonException)
            { return Rejected("Java 管理未完成：" + error.Message); }
        }, cancellationToken));

    private async Task<XsrResult> DeleteManagedAsync(JavaRuntimeManageCommand command, JavaRuntimeRegistrySnapshot snapshot, CancellationToken token)
    {
        if (command.ExpectedManagedIdentity is not { Length: 64 } identity || !identity.All(char.IsAsciiHexDigit))
            return Rejected("请刷新托管 Java 列表后确认删除。");
        var owned = new JavaRuntimeManagedStore(managedRuntimeRoots ?? []);
        string executable = Path.GetFullPath(command.Executable);
        var (root, plan) = await owned.ResolveAsync(executable, identity, token).ConfigureAwait(false);
        using var rootLease = JavaRuntimeManagedStore.AcquireRoot(root);
        // Resolve again while holding the install lease: ownership can change during an async preview.
        (_, plan) = await owned.ResolveAsync(executable, identity, token).ConfigureAwait(false);
        if (registry.Read().Revision != snapshot.Revision) return Rejected("Java 列表已变化，请刷新后重试。");
        JavaRuntimeManagedStore.CheckUnused(root, plan.ComponentName);
        await JavaRuntimeManagedStore.CheckNoPendingInstallAsync(root, plan.ComponentName, token).ConfigureAwait(false);
        await JavaRuntimeManagedStore.VerifyTreeAsync(plan, token).ConfigureAwait(false);
        string quarantineRoot = Path.Combine(root, ".nexa-java-removed");
        Nexa.Services.Minecraft.Management.RecoveryBlobStore.CheckLinks(quarantineRoot);
        Directory.CreateDirectory(quarantineRoot);
        string quarantineStage = Path.Combine(quarantineRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(quarantineStage);
        // Keep payload one level below the GUID so generic runtime discovery cannot adopt it.
        string quarantine = Path.Combine(quarantineStage, "payload");
        bool committed = false;
        token.ThrowIfCancellationRequested();
        Directory.Move(plan.TargetDirectory, quarantine);
        try
        {
            var stagedPlan = plan with
            {
                TargetDirectory = quarantine,
                Files = plan.Files.Select(file => file with
                { TargetPath = PathIdentity.Contained(quarantine, file.RelativePath) }).ToArray()
            };
            await JavaRuntimeManagedStore.VerifyTreeAsync(stagedPlan, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var entries = snapshot.Registrations.Where(entry => !PathIdentity.Comparer.Equals(entry.Executable, executable)).ToArray();
            var result = registry.Write(snapshot.Revision, Array.AsReadOnly(entries));
            if (!result.IsSuccess) return result;
            committed = true;
            locator.Invalidate();
            // Commit has removed the live component. Cleanup is best effort and never follows links.
            try
            {
                await JavaRuntimeManagedStore.VerifyTreeAsync(stagedPlan, CancellationToken.None).ConfigureAwait(false);
                JavaRuntimeManagedStore.DeleteTree(quarantine);
                Directory.Delete(quarantineStage);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            return result;
        }
        finally
        {
            if (!committed && Directory.Exists(quarantine))
            {
                Nexa.Services.Minecraft.Management.RecoveryBlobStore.CheckLinks(plan.TargetDirectory);
                Directory.Move(quarantine, plan.TargetDirectory);
                Directory.Delete(quarantineStage);
            }
        }
    }

    private static XsrResult Rejected(string message) => XsrResult.Failure(new(XsrErrorKind.Rejected,
        XsrSemanticId.Parse("java.runtime.registry.rejected"), message));
}
