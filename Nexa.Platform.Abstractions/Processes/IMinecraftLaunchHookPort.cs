namespace Nexa.Services.Minecraft.Process;

/// <summary>Platform-owned lease for a user-authored shell command, without captured output.</summary>
public interface IMinecraftLaunchHookSession : IAsyncDisposable
{
    ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Transfers lifetime to an asynchronous reaper. A detached command outlives launch
    /// preparation; its exit callback cannot change an already started game session.
    /// </summary>
    void Detach(Action<int>? exited = null);
}

public interface IMinecraftLaunchHookPort
{
    ValueTask<IMinecraftLaunchHookSession> StartAsync(string command, string workingDirectory,
        CancellationToken cancellationToken = default);
}
