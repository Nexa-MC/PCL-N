using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Java;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Settings;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Services.Composition;

public static class InstanceOfflineReadinessRuntime
{
    public static void Register(XsrQueryRouterBuilder queries, IJavaRuntimeLocator locator, SettingsPolicyService policy)
    {
        var selection = new JavaSelectionService(locator);
        var service = new InstanceOfflineReadinessService(async (instance, requirement, metadata, token) =>
        {
            var snapshot = policy.Read(new SettingsEffectiveQuery(instance));
            if (!snapshot.IsSuccess) throw new IOException(snapshot.Error?.Message ?? "无法读取实例 Java 偏好。");
            JavaPreference fallback = metadata.JavaSelectionMode == 2 && metadata.SelectedJavaPath.Length > 0
                ? new ExistingJavaPreference(metadata.SelectedJavaPath) : new AutoSelectJavaPreference();
            var preference = MinecraftLaunchCoordinator.ApplyJavaPreference(fallback, snapshot.Value!);
            return await selection.SelectAsync(requirement, preference, token).ConfigureAwait(false);
        });
        queries.Register<InstanceOfflineReadinessQuery, InstanceOfflineReadinessReport>(InstanceOfflineReadinessContract.Query,
            async (query, token) =>
            {
                try { return XsrResult.Success(await service.ReadAsync(query, token).ConfigureAwait(false)); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return XsrResult.Failure<InstanceOfflineReadinessReport>(XsrRuntimeErrors.Cancelled()); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                { return XsrResult.Failure<InstanceOfflineReadinessReport>(new XsrError(XsrErrorKind.Rejected, XsrSemanticId.Parse("minecraft.instance.offline-readiness.rejected"), "无法检查本地离线依赖，请检查版本目录和权限。")); }
            });
    }
}
