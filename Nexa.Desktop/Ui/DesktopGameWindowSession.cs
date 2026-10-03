using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Process;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

/// <summary>Native lifecycle effects from captured sealed state, even while no frames render.</summary>
internal sealed class DesktopGameWindowSession : IDisposable
{
    private readonly XsrStateStore _store;
    private readonly XsrStateId _processes, _launch;
    private readonly Action<Action> _dispatch;
    private readonly Action _hide, _minimize, _restore, _close;
    private readonly Action<string>? _warn;
    private readonly Dictionary<Guid, MinecraftLauncherVisibility> _managed = [];
    private bool _adjusted, _closeAfterExit, _failed;
    private int _disposed;

    internal DesktopGameWindowSession(XsrStateStore store, Action<Action> dispatch, Action hide,
        Action minimize, Action restore, Action close, Action<string>? warn = null)
    {
        _store = store; _dispatch = dispatch; _hide = hide; _minimize = minimize;
        _restore = restore; _close = close; _warn = warn;
        _processes = store.Resolve(MinecraftProcessStateComposition.SessionsKey);
        _launch = store.Resolve(MinecraftLaunchProgressState.SnapshotKey);
        store.Changed += OnChanged;
    }

    private void OnChanged(XsrStateChange change)
    {
        if (change.Id != _processes && change.Id != _launch) return;
        var snapshots = _store.ReadCollection<MinecraftProcessSnapshot>(_processes).Items.ToArray();
        Guid? launched = change.Id == _launch && _store.ReadAppliedValue(_launch)
            is MinecraftLaunchProgressSnapshot { IsLaunched: true, SessionId: { } id } ? id : null;
        _dispatch(() =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try
            {
                if (launched is { } session && snapshots.FirstOrDefault(item => item.SessionId == session) is { } item
                    && item.State is MinecraftProcessState.Created or MinecraftProcessState.Running && _managed.TryAdd(session, item.LauncherVisibility))
                {
                    if (item.LauncherVisibility == MinecraftLauncherVisibility.Minimize) { _adjusted = true; _minimize(); }
                    else if (item.LauncherVisibility is MinecraftLauncherVisibility.Hide or MinecraftLauncherVisibility.HideAndClose)
                    { _adjusted = true; _hide(); }
                }
                foreach (var entry in _managed.ToArray())
                {
                    var current = snapshots.FirstOrDefault(item => item.SessionId == entry.Key);
                    if (current?.State is MinecraftProcessState.Created or MinecraftProcessState.Running) continue;
                    _managed.Remove(entry.Key);
                    if (current is not { State: MinecraftProcessState.Exited, ExitCode: 0 })
                    { _failed = true; _closeAfterExit = false; _adjusted = false; _restore(); }
                    else if (entry.Value == MinecraftLauncherVisibility.HideAndClose) _closeAfterExit = true;
                }
                if (snapshots.Any(item => item.State is MinecraftProcessState.Created or MinecraftProcessState.Running)) return;
                if (_adjusted) { _restore(); if (_closeAfterExit && !_failed) _close(); }
                _adjusted = _closeAfterExit = _failed = false;
            }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            { _warn?.Invoke($"Launcher window effect failed: {error.GetType().Name}: {error.Message}"); }
        });
    }

    public void Dispose() { Interlocked.Exchange(ref _disposed, 1); _store.Changed -= OnChanged; }
}
