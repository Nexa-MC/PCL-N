using Nexa.Core;
using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Java;

public sealed class JavaRuntimeManagementService(IJavaRuntimeLocator locator, IJavaRuntimeRegistrationStore registry)
{
    public ValueTask<XsrResult> ManageAsync(JavaRuntimeManageCommand command, CancellationToken cancellationToken = default)
        => new(Task.Run(async () =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Enum.IsDefined(command.Action) || !Path.IsPathFullyQualified(command.Executable))
                    return Rejected("请选择有效的 Java 可执行文件。");
                string executable = Path.GetFullPath(command.Executable);
                var snapshot = registry.Read();
                if (snapshot.Revision != command.ExpectedRevision) return Rejected("Java 列表已变化，请刷新后重试。");
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
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OverflowException or System.Text.Json.JsonException)
            { return Rejected("Java 管理未完成：" + error.Message); }
        }, cancellationToken));

    private static XsrResult Rejected(string message) => XsrResult.Failure(new(XsrErrorKind.Rejected,
        XsrSemanticId.Parse("java.runtime.registry.rejected"), message));
}
