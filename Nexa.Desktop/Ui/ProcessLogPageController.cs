using Nexa.Pxml;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Process;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

internal static class ProcessLogPresentationState
{
    internal static readonly XsrSemanticId Wake = XsrSemanticId.Parse("ui.process.log.wake");
    internal static void DeclareState(XsrStateStoreBuilder builder) => builder.Cell<long>(Wake, "Nexa.Desktop.ProcessLogs");
}

/// <summary>Bounded presentation of typed, redacted process output; native copy is an explicit effect.</summary>
internal sealed class ProcessLogPageController : IDisposable
{
    internal const int PageSize = 24;
    private static readonly XsrUiColor Ink = new(38, 49, 65), Muted = new(94, 110, 130), Tint = new(231, 240, 255);
    private readonly XsrUiShell _shell;
    private readonly DesktopUiIntentSink _intents;
    private readonly XsrQueryRouter _queries;
    private readonly XsrStateStore _store;
    private readonly DesktopFeedbackService _feedback;
    private readonly Func<string, Task>? _copy;
    private readonly Dictionary<string, XsrUiEntityId> _entities = [];
    private readonly Dictionary<XsrUiEntityId, long> _rows = [];
    private readonly HashSet<long> _selected = [];
    private CancellationTokenSource? _readStop;
    private Task<XsrResult<MinecraftProcessOutputSnapshot>>? _reading;
    private Task _pending = Task.CompletedTask;
    private MinecraftProcessOutputSnapshot? _snapshot;
    private MinecraftProcessOutputEntry[] _filtered = [];
    private Guid _session;
    private string _search = "", _error = "";
    private MinecraftProcessOutputChannel? _stream;
    private int _page;
    private long _generation, _wakeRevision, _sessionRevision = -1;
    private bool _visible;
    private volatile bool _dirty = true, _disposed, _copying;

    internal ProcessLogPageController(XsrUiShell shell, DesktopUiIntentSink intents, XsrQueryRouter queries,
        XsrStateStore store, DesktopFeedbackService feedback, Func<string, Task>? copy = null)
    {
        _shell = shell; _intents = intents; _queries = queries; _store = store; _feedback = feedback; _copy = copy;
        using Stream source = typeof(ProcessLogPageController).Assembly.GetManifestResourceStream("Nexa.Desktop.Ui.ProcessLogPage.pxml")
            ?? throw new InvalidOperationException("Missing process log page.");
        using StreamReader reader = new(source);
        var template = PxmlCompiler.Compile(PxmlParser.Parse(reader.ReadToEnd()));
        var parent = shell.Tree.Create("process-log-loader");
        Page = PxmlUiLoader.Load(template, shell.Tree, store, parent);
        shell.Tree.Detach(Page); shell.Tree.Destroy(parent);
        shell.Tree.Walk(Page, entity =>
        {
            _entities[shell.Tree.Name(entity)] = entity;
            bool interactive = shell.Tree.GetComponent<XsrUiInput>(entity) is not null;
            shell.Tree.SetComponent(entity, new XsrUiVisualStyle
            {
                Foreground = Ink,
                FontSize = 13,
                CornerRadius = 10,
                Background = interactive ? DesktopUiPalette.CapsuleBackground : XsrUiColor.Transparent,
                Hover = DesktopUiPalette.CapsuleHover,
            });
            return true;
        });
        shell.Tree.GetComponent<XsrUiVisualStyle>(_entities["ProcessLogStatus"])!.WrapText = true;
        shell.Tree.GetComponent<XsrUiVisualStyle>(_entities["ProcessLogStatus"])!.Foreground = Muted;
        shell.Tree.GetComponent<XsrUiVisualStyle>(_entities["ProcessLogEmpty"])!.WrapText = true;
        shell.Tree.GetComponent<XsrUiScroll>(_entities["ProcessLogRows"])!.ShowsVerticalIndicator = true;
        DesktopLiteralText.Preserve(shell.Tree, _entities["ProcessLogSession"]);
        shell.Renderer.FramePreparing += OnFrame;
        intents.IntentEmitted += OnIntent;
    }

    internal XsrUiEntityId Page { get; }
    internal Task WaitUntilIdle() => _pending;

    internal void Leave()
    {
        RetireRead(); _snapshot = null; _filtered = []; _selected.Clear(); ClearRows(); _visible = false;
    }

    internal void Open(Guid session)
    {
        RetireRead();
        _session = session; _snapshot = null; _selected.Clear(); _page = 0; _stream = null; _search = "";
        _shell.Renderer.SetTextInputValue(_entities["ProcessLogSearch"], "");
        _visible = true;
        Read();
    }

    private MinecraftProcessSnapshot[] Sessions()
    {
        if (!_store.TryResolve(MinecraftProcessStateComposition.SessionsKey, out var id)) return [];
        return _store.ReadCollection<MinecraftProcessSnapshot>(id).Items.OrderBy(item => item.StartedAt).ToArray();
    }

    private void Read()
    {
        RetireRead();
        _error = ""; _dirty = true;
        if (!_queries.TryResolve(MinecraftProcessOutputContract.Query, out var route))
        { _error = "本次运行不支持读取日志。"; return; }
        long generation = _generation;
        Guid session = _session;
        _readStop = new();
        CancellationToken token = _readStop.Token;
        _reading = Task.Run(async () =>
        {
            try
            {
                return await _queries.QueryAsync<MinecraftProcessOutputQuery, MinecraftProcessOutputSnapshot>(route,
                    new(session), cancellationToken: token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            { return XsrResult.Failure<MinecraftProcessOutputSnapshot>(MinecraftErrors.InvalidRequest("日志读取已取消。")); }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            { return XsrResult.Failure<MinecraftProcessOutputSnapshot>(MinecraftErrors.InvalidRequest("暂时无法读取日志。")); }
        });
        _pending = _reading;
        WakeOnCompletion(_reading, generation);
    }

    private void WakeOnCompletion(Task task, long generation)
    {
        _ = task.ContinueWith(_ =>
        {
            if (_disposed || generation != Interlocked.Read(ref _generation)) return;
            try { _store.Publish(_store.Resolve(ProcessLogPresentationState.Wake), Interlocked.Increment(ref _wakeRevision)); }
            catch (ObjectDisposedException) { }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void RetireRead()
    {
        Interlocked.Increment(ref _generation);
        _readStop?.Cancel(); _readStop?.Dispose(); _readStop = null; _reading = null;
    }

    private void OnFrame(object? sender, EventArgs args)
    {
        if (_disposed) return;
        bool visible = _shell.Stage.Navigation.Current == Page;
        if (_visible && !visible)
        {
            Leave();
        }
        else if (!_visible && visible) Read();
        _visible = visible;
        if (!visible) return;
        string draft = _shell.Tree.GetComponent<XsrUiTextInput>(_entities["ProcessLogSearch"])!.ReadDraft().Trim();
        if (draft != _search) { _search = draft; _page = 0; _dirty = true; }
        if (_store.TryResolve(MinecraftProcessStateComposition.SessionsKey, out var sessionsId))
        {
            long revision = _store.ReadCollection<MinecraftProcessSnapshot>(sessionsId).Revision;
            if (revision != _sessionRevision)
            {
                _sessionRevision = revision; _dirty = true;
                if (_snapshot is not null && !_store.ReadCollection<MinecraftProcessSnapshot>(sessionsId).Items.Any(item => item.SessionId == _session))
                {
                    RetireRead(); _snapshot = null; _selected.Clear();
                    _error = "这次运行的日志已过期，或该进程不由启动器管理。";
                }
            }
        }
        if (_reading is { IsCompleted: true } reading)
        {
            _reading = null; _readStop?.Dispose(); _readStop = null;
            bool completed = new PendingQuery<MinecraftProcessOutputSnapshot>(reading).TryRead(out var result);
            // A malformed provider cannot silently substitute another session's output.
            if (completed && result!.IsSuccess && result.Value!.SessionId == _session)
            {
                _snapshot = result.Value;
                _selected.IntersectWith(_snapshot.Entries.Select(entry => entry.Sequence));
            }
            else
            {
                _snapshot = null; _selected.Clear();
                _error = !completed ? "暂时无法读取日志。" : result!.Error?.Message ?? "日志与所选运行不匹配。";
            }
            _dirty = true;
        }
        if (_dirty) { _dirty = false; Project(); }
    }

    private void OnIntent(object? sender, DesktopUiIntentEventArgs args)
    {
        if (_disposed || _shell.Stage.Navigation.Current != Page) return;
        string command = args.Intent.Command.Value;
        if (command == "ui.page.back" || command.StartsWith("ui.navigation.", StringComparison.Ordinal))
        { RetireRead(); return; }
        if (!command.StartsWith("ui.process.log.", StringComparison.Ordinal)) return;
        if (command == "ui.process.log.select" && _rows.TryGetValue(args.Intent.Source, out var sequence))
        { if (!_selected.Add(sequence)) _selected.Remove(sequence); }
        else if (command == "ui.process.log.refresh") Read();
        else if (command == "ui.process.log.previous-session") SwitchSession(-1);
        else if (command == "ui.process.log.next-session") SwitchSession(1);
        else if (command == "ui.process.log.previous") _page = Math.Max(0, _page - 1);
        else if (command == "ui.process.log.next") _page++;
        else if (command is "ui.process.log.all" or "ui.process.log.stdout" or "ui.process.log.stderr")
        {
            _stream = command.EndsWith(".stdout", StringComparison.Ordinal) ? MinecraftProcessOutputChannel.Stdout
                : command.EndsWith(".stderr", StringComparison.Ordinal) ? MinecraftProcessOutputChannel.Stderr : null;
            _page = 0;
        }
        else if (command == "ui.process.log.clear-selection") _selected.Clear();
        else if (command is "ui.process.log.copy-selected" or "ui.process.log.copy-page")
        {
            if (_copying || _snapshot is null) return;
            var entries = command == "ui.process.log.copy-selected"
                ? _snapshot.Entries.Where(entry => _selected.Contains(entry.Sequence)).ToArray()
                : _filtered.Skip(_page * PageSize).Take(PageSize).ToArray();
            if (entries.Length == 0) return;
            _copying = true;
            _pending = CopyAsync(string.Join(Environment.NewLine, entries.Select(Display)), _generation);
            WakeOnCompletion(_pending, _generation);
        }
        _dirty = true;
    }

    private void SwitchSession(int direction)
    {
        var sessions = Sessions();
        int index = sessions.ToList().FindIndex(item => item.SessionId == _session);
        int next = index + direction;
        if (next >= 0 && next < sessions.Length) Open(sessions[next].SessionId);
    }

    private async Task CopyAsync(string text, long generation)
    {
        try
        {
            if (_copy is null) throw new InvalidOperationException("系统剪贴板不可用。");
            await _copy(text).ConfigureAwait(false);
            if (!_disposed && generation == Interlocked.Read(ref _generation)) _feedback.Info("日志已复制。");
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            if (!_disposed && generation == Interlocked.Read(ref _generation))
                _feedback.Error(error is InvalidOperationException && error.Message == "系统剪贴板不可用。"
                    ? "系统剪贴板不可用，无法复制日志。" : "无法复制日志，请稍后重试。");
        }
        finally { _copying = false; _dirty = true; }
    }

    private static string Display(MinecraftProcessOutputEntry entry) =>
        "[" + (entry.Stream == MinecraftProcessOutputChannel.Stdout ? "stdout" : "stderr") + "] " + entry.Text;

    private void Project()
    {
        var sessions = Sessions();
        int index = sessions.ToList().FindIndex(item => item.SessionId == _session);
        var session = index >= 0 ? sessions[index] : null;
        var state = session?.State ?? _snapshot?.State;
        string lifecycle = state is MinecraftProcessState.Created or MinecraftProcessState.Running ? "运行中"
            : state == MinecraftProcessState.Cancelled ? "已结束（取消）" : state == MinecraftProcessState.Failed ? "异常退出" : "已结束";
        lifecycle = _shell.Renderer.LocalizeText(lifecycle);
        Text("ProcessLogSession", (session?.InstanceId ?? _snapshot?.InstanceId ?? "本次运行")
            + " · " + lifecycle + (session is null ? "" : " · " + session.StartedAt.ToLocalTime().ToString("MM-dd HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture)));
        Enable("ProcessLogPreviousSession", index > 0);
        Enable("ProcessLogNextSession", index >= 0 && index + 1 < sessions.Length);
        Enable("ProcessLogRefresh", _reading is null);
        _filtered = (_snapshot?.Entries ?? []).Where(entry => (!_stream.HasValue || entry.Stream == _stream)
            && (_search.Length == 0 || entry.Text.Contains(_search, StringComparison.OrdinalIgnoreCase))).ToArray();
        int pages = Math.Max(1, (_filtered.Length + PageSize - 1) / PageSize);
        _page = Math.Clamp(_page, 0, pages - 1);
        Text("ProcessLogPageNumber", $"{_page + 1} / {pages}");
        Text("ProcessLogStatus", _reading is not null ? "正在读取日志…"
            : _error.Length > 0 ? _error : $"{_filtered.Length} 条日志 · 已选 {_selected.Count} 条 · 打开或点击刷新时读取"
                + (_snapshot?.DroppedEntries > 0 ? $"\n仅保留最近 512 条，已省略 {_snapshot.DroppedEntries} 条较早输出。" : ""));
        string empty = _error.Length > 0 || _reading is not null ? ""
            : _snapshot?.Entries.Count == 0 ? "本次运行尚未产生可读取的标准输出或标准错误。"
            : _filtered.Length == 0 ? "没有符合搜索条件的日志。" : "";
        Text("ProcessLogEmpty", empty);
        _shell.Tree.GetComponent<XsrUiElement>(_entities["ProcessLogEmpty"])!.IsVisible = empty.Length > 0;
        Enable("ProcessLogPrevious", _page > 0);
        Enable("ProcessLogNext", _page + 1 < pages);
        Enable("ProcessLogClearSelection", _selected.Count > 0);
        Enable("ProcessLogCopySelected", _selected.Count > 0 && !_copying);
        Enable("ProcessLogCopyPage", _filtered.Length > 0 && !_copying);
        foreach (var choice in new[] { ("ProcessLogAll", (MinecraftProcessOutputChannel?)null),
            ("ProcessLogStdout", (MinecraftProcessOutputChannel?)MinecraftProcessOutputChannel.Stdout),
            ("ProcessLogStderr", (MinecraftProcessOutputChannel?)MinecraftProcessOutputChannel.Stderr) })
            _shell.Tree.SetComponent(_entities[choice.Item1], new XsrUiSelection { IsSelected = _stream == choice.Item2 });
        var entries = _filtered.Skip(_page * PageSize).Take(PageSize).ToArray();
        if (!_rows.Values.SequenceEqual(entries.Select(entry => entry.Sequence))) ClearRows();
        foreach (var entry in entries)
        {
            var row = _rows.FirstOrDefault(pair => pair.Value == entry.Sequence).Key;
            if (!row.IsAssigned)
            {
                row = _shell.Tree.Create("ProcessLogEntry:" + entry.Sequence);
                _shell.Tree.SetComponent(row, new XsrUiElement { MinHeight = 38, Padding = new(12, 10, 12, 10) });
                _shell.Tree.SetComponent(row, new XsrUiInput { Clickable = true, Focusable = true });
                _shell.Tree.SetComponent(row, new XsrUiCommandBinding(XsrSemanticId.Parse("ui.process.log.select")));
                _shell.Tree.Attach(row, _entities["ProcessLogRows"]); _rows[row] = entry.Sequence;
            }
            _shell.Tree.SetComponent(row, new XsrUiText(Display(entry)) { Localize = false });
            _shell.Tree.SetComponent(row, new XsrUiSemantic(XsrUiSemanticRole.Button, Display(entry)) { Localize = false });
            _shell.Tree.SetComponent(row, new XsrUiSelection { IsSelected = _selected.Contains(entry.Sequence) });
            _shell.Tree.SetComponent(row, new XsrUiVisualStyle
            {
                Foreground = Ink,
                FontSize = 13,
                WrapText = true,
                CornerRadius = 8,
                Background = _selected.Contains(entry.Sequence) ? Tint : DesktopUiPalette.CapsuleBackground,
                Hover = DesktopUiPalette.CapsuleHover,
            });
        }
        _shell.Tree.MarkDirty(Page, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }

    private void ClearRows()
    {
        foreach (var row in _rows.Keys) _shell.Tree.Destroy(row);
        _rows.Clear();
        _shell.Tree.GetComponent<XsrUiScroll>(_entities["ProcessLogRows"])!.OffsetY = 0;
    }

    private void Text(string key, string text) => _shell.Tree.GetComponent<XsrUiText>(_entities[key])!.Content = text;
    private void Enable(string key, bool enabled)
    {
        var input = _shell.Tree.GetComponent<XsrUiInput>(_entities[key])!;
        input.Clickable = input.Focusable = enabled;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; RetireRead();
        _intents.IntentEmitted -= OnIntent; _shell.Renderer.FramePreparing -= OnFrame;
        _shell.Tree.Destroy(Page); _snapshot = null; _filtered = []; _selected.Clear(); _rows.Clear();
    }
}
