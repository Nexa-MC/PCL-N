using System.Globalization;

using Nexa.Services.Accounts;


using Nexa.Services.Minecraft;

using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Process;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

internal sealed partial class LaunchPageController
{

    private async Task StartLaunchAsync(string instanceId, XsrUiEntityId source)
    {
        IReadOnlyList<LaunchProfileView> profiles = ReadProfiles();
        int selected = SelectedAccountIndex;
        if (!profiles.Any(profile => profile.Index == selected))
        {
            _feedback.Warn(AccountNeedLoginSummary);
            return;
        }

        if (!_minecraft.Commands.TryResolve(MinecraftRouteIds.Start, out XsrCommandId commandId))
        {
            _feedback.Error("启动失败：产品启动命令未注册。");
            return;
        }

        if (LaunchBusy) return;
        ShowLaunchingPage(source);

        // The launching page replicates the legacy launching card: reset facts, narrate the
        // pipeline through the launch progress cells, and return on failure or cancellation.
        Publish(LaunchPageState.LaunchingTitleKey, "正在启动");
        Publish(LaunchPageState.LaunchingNameKey, ReadCell(LaunchPageState.InstanceSummaryKey));
        Publish(LaunchPageState.LaunchingStageKey, "初始化");
        Publish(LaunchPageState.LaunchingMethodKey, "等待账户档案");
        Publish(LaunchPageState.LaunchingPercentKey, "0%");
        Publish(LaunchPageState.LaunchingSpeedVisibleKey, false);
        Publish(LaunchPageState.LaunchingHintKey, LaunchWidgetHints.BuiltIn[_hintIndex]);
        RefreshLaunchingDisplay();

        XsrCommandDispatch dispatch = _minecraft.Commands.Dispatch(
            commandId,
            new MinecraftStartCommand(instanceId, selected)
            { MinecraftRootDirectory = ReadCell(LaunchPageState.InstanceDirectoryKey) },
            cancellationToken: _lifetimeCancellation.Token);
        XsrResult result = await dispatch.Completion.ConfigureAwait(false);
        if (_disposed)
        {
            return;
        }

        if (result.IsSuccess)
        {
            // The window confirmation (or its platform fallback) already passed: success
            // closes the page immediately so the user is back at the library while the game
            // warms up, instead of parking on a 游戏已启动 card until the session ends.
            _feedback.Info("Minecraft 已启动。");
            RequestCloseLaunchingPage();
            return;
        }

        _feedback.Error($"启动失败：{result.Error?.Message}");
        RequestCloseLaunchingPage();
    }


    private async Task CancelLaunchAsync()
    {
        if (!LaunchBusy)
        {
            return;
        }

        // Once the game is running there is no pipeline left to cancel — the button has become
        // "back" — so just leave the page without touching the process.
        bool launched = _store.ReadAppliedValue(_launchProgressId)
            is MinecraftLaunchProgressSnapshot snapshot && snapshot.IsLaunched;
        if (launched)
        {
            RequestCloseLaunchingPage();
            return;
        }

        Publish(LaunchPageState.LaunchingStageKey, "已请求取消启动");
        if (_minecraft.Commands.TryResolve(MinecraftRouteIds.LaunchCancel, out XsrCommandId route))
        {
            await _minecraft.Commands.Dispatch(route, new MinecraftCancelLaunchCommand(),
                cancellationToken: _lifetimeCancellation.Token).Completion.ConfigureAwait(false);
        }

        if (!_disposed)
        {
            RequestCloseLaunchingPage();
        }
    }


    /// <summary>
    /// Requests the launching page to close from any thread. The close itself mutates the
    /// navigation stack, tree components, and focus — all render-thread state — so it is
    /// drained on the next frame preparation instead of running here.
    /// </summary>
    private void RequestCloseLaunchingPage()
    {
        Interlocked.Exchange(ref _pendingCloseLaunching, 1);
    }


    /// <summary>
    /// Opens the dedicated launching page (a navigation push, mirroring the subpage flow) and
    /// records where to restore focus when it closes.
    /// </summary>
    private void ShowLaunchingPage(XsrUiEntityId source)
    {
        if (_shell.Stage.Navigation.Current == _launchingPage) return;
        _launchingViaKeyboard = IsKeyboardIntent(source);
        _returnFocus.Push(source);
        _shell.Stage.Navigation.Push(_launchingPage);
        UpdateTitleBar();
        _shell.Renderer.Focus(_launchingEntities.GetValueOrDefault("LaunchingCancelButton"), _launchingViaKeyboard);
    }


    private async Task DecideAcquisitionAsync(bool approve)
    {
        if (!LaunchBusy
            || !_minecraft.Commands.TryResolve(MinecraftRouteIds.AcquireDecide, out XsrCommandId route))
        {
            _feedback.Error("Java 下载确认命令未注册。");
            return;
        }

        XsrResult result = await _minecraft.Commands.Dispatch(
            route,
            new MinecraftDecideJavaAcquisitionCommand(approve),
            cancellationToken: _lifetimeCancellation.Token).Completion.ConfigureAwait(false);
        if (!_disposed && !result.IsSuccess)
        {
            _feedback.Error($"Java 下载确认失败：{result.Error?.Message}");
        }
    }


    private void CloseLaunchingPage()
    {
        UpdateLaunchButton();
        DismissAcquisitionDialog();
        if (_javaChoicePage.IsAssigned && _shell.Stage.Navigation.Current == _javaChoicePage)
        { _shell.Stage.Navigation.Pop(); _returnFocus.TryPop(out _); }
        if (_shell.Stage.Navigation.Current != _launchingPage)
        {
            return;
        }

        _ = _shell.Stage.Navigation.Pop();
        UpdateTitleBar();
        if (_returnFocus.TryPop(out XsrUiEntityId focus))
        {
            _shell.Renderer.Focus(focus, _launchingViaKeyboard);
        }
    }


    /// <summary>
    /// Projects the services launch progress cells into the overlay display strings: stage
    /// tokens become legacy stage labels, the progress fraction formats as whole percent, and
    /// the title switches to the launched state once the pipeline reports the game running.
    /// </summary>
    private void RefreshLaunchingDisplay()
    {
        string stage = ReadServiceCell(MinecraftLaunchProgressState.StageKey);
        if (stage.Length > 0)
        {
            Publish(LaunchPageState.LaunchingStageKey,
                LaunchStageDisplay.GetValueOrDefault(stage, stage));
        }

        double progress = _store.ReadAppliedValue(_store.Resolve(MinecraftLaunchProgressState.ProgressKey)) is double value
            ? Math.Clamp(value, 0d, 1d)
            : 0d;
        Publish(LaunchPageState.LaunchingPercentKey, Math.Round(progress * 100) + "%");

        string method = ReadServiceCell(MinecraftLaunchProgressState.MethodKey);
        Publish(LaunchPageState.LaunchingMethodKey,
            method.Length == 0 ? "等待账户档案" : LaunchMethodDisplay.GetValueOrDefault(method, method));

        string speed = ReadServiceCell(MinecraftLaunchProgressState.SpeedKey);
        Publish(LaunchPageState.LaunchingSpeedKey, speed);
        Publish(LaunchPageState.LaunchingSpeedVisibleKey, speed.Length > 0);

        bool launched = _store.ReadAppliedValue(_store.Resolve(MinecraftLaunchProgressState.LaunchedKey)) is bool running && running;
        Publish(LaunchPageState.LaunchingTitleKey, launched ? "游戏已启动" : "正在启动");
        Publish(LaunchPageState.LaunchingCancelLabelKey, launched ? "返回" : "取消");
    }


    /// <summary>
    /// Projects the acquisition cells into the shared window-internal dialog. The feedback
    /// service is thread-safe; its presenter performs all PXML mutations at frame preparation.
    /// </summary>
    private void RefreshAcquisitionPrompt()
    {
        bool pending = _store.ReadAppliedValue(_store.Resolve(MinecraftLaunchProgressState.AcquirePendingKey)) is bool waiting && waiting;
        string component = ReadServiceCell(MinecraftLaunchProgressState.AcquireComponentKey);
        int major = _store.ReadAppliedValue(_store.Resolve(MinecraftLaunchProgressState.AcquireMajorKey)) is int version ? version : 0;
        if (LaunchBusy && pending && major > 0 && component.Length > 0)
        {
            _javaAcquisitionDialog = _feedback.ShowDialog(
                "minecraft.java.acquire",
                $"需要 Java {major}",
                $"未找到兼容的 Java {major}。请选择已安装的 Java，或自动下载。",
                "自动下载",
                "取消",
                approve => _ = DecideAcquisitionAsync(approve),
                "选择 Java 版本", ShowJavaVersionChoice);
        }
        else if (!pending && _javaAcquisitionDialog is { } dialog)
        {
            _feedback.DismissDialog(dialog);
            _javaAcquisitionDialog = null;
        }
    }


    private void RefreshPreflightPrompt()
    {
        var prompt = _store.ReadAppliedValue(_store.Resolve(LaunchPreflightContract.StateKey)) as LaunchPreflightPrompt;
        if (prompt is null)
        {
            if (_preflightDialog is { } old) _feedback.DismissDialog(old);
            _preflightDialog = _preflightAttempt = null;
            return;
        }
        if (_preflightAttempt == prompt.Attempt) return;
        _preflightAttempt = prompt.Attempt;
        bool blocked = prompt.Report.Issues.Any(static issue => issue.Severity == Nexa.Services.Capabilities.PreflightSeverity.Blocked);
        string message = string.Join("\n\n", prompt.Report.CollapsedIssues.Select(issue => $"{issue.Title}\n{issue.Description}"));
        _preflightDialog = _feedback.ShowDialog("minecraft.preflight", blocked ? "需要先处理启动问题" : "启动前请确认",
            message, blocked ? "返回" : "仍然启动", "取消", approve => _ = DecidePreflightAsync(prompt.Attempt, approve && !blocked));
    }


    private async Task DecidePreflightAsync(Guid attempt, bool proceed)
    {
        if (!_minecraft.Commands.TryResolve(LaunchPreflightContract.DecisionCommand, out XsrCommandId route)) return;
        await _minecraft.Commands.Dispatch(route, new LaunchPreflightDecision(attempt, proceed),
            cancellationToken: _lifetimeCancellation.Token).Completion.ConfigureAwait(false);
    }


    private void DismissAcquisitionDialog()
    {
        if (_javaAcquisitionDialog is not { } dialog)
        {
            return;
        }

        _feedback.DismissDialog(dialog);
        _javaAcquisitionDialog = null;
    }


    private string ReadServiceCell(XsrSemanticId key) =>
        Convert.ToString(_store.ReadAppliedValue(_store.Resolve(key)), CultureInfo.InvariantCulture) ?? string.Empty;


    /// <summary>
    /// Receives host state publications. Launch progress changes refresh the overlay; a
    /// terminal process session while the game was reported launched closes it, mirroring the
    /// legacy flow that returns to the launch page when the game exits.
    /// </summary>
    private sealed class LaunchingStateObserver(LaunchPageController owner) : IXsrStateObserver
    {
        public void OnChanged(XsrStateChange change)
        {
            if (change.SemanticId == LaunchPreflightContract.StateKey) { owner.RefreshPreflightPrompt(); return; }
            if (change.SemanticId == MinecraftLibraryContract.StateKey) { owner.ProjectLibrary(); return; }
            if (change.SemanticId.Equals(MinecraftLaunchProgressState.StageKey)
                || change.SemanticId.Equals(MinecraftLaunchProgressState.ProgressKey)
                || change.SemanticId.Equals(MinecraftLaunchProgressState.MethodKey)
                || change.SemanticId.Equals(MinecraftLaunchProgressState.SpeedKey)
                || change.SemanticId.Equals(MinecraftLaunchProgressState.LaunchedKey))
            {
                owner.RefreshLaunchingDisplay();
                return;
            }

            if (change.SemanticId.Equals(MinecraftLaunchProgressState.AcquirePendingKey)
                || change.SemanticId.Equals(MinecraftLaunchProgressState.AcquireComponentKey)
                || change.SemanticId.Equals(MinecraftLaunchProgressState.AcquireMajorKey))
            {
                owner.RefreshAcquisitionPrompt();
                return;
            }

            // The narration belongs to one session: only THAT game's terminal state closes the
            // page. Other running games must keep the flow alive.
            if (owner.LaunchBusy
                && change.SemanticId.Equals(MinecraftProcessStateComposition.SessionsKey)
                && owner.LaunchedSessionId() is { } sessionId
                && sessionId != Guid.Empty
                && owner.IsSessionTerminal(sessionId))
            {
                owner.RequestCloseLaunchingPage();
            }
        }
    }


    /// <summary>The session this narration launched, from the coherent snapshot truth.</summary>
    private Guid? LaunchedSessionId() =>
        _store.ReadAppliedValue(_launchProgressId) is MinecraftLaunchProgressSnapshot snapshot
            ? snapshot.SessionId
            : null;


    private bool IsSessionTerminal(Guid sessionId) =>
        _store.ReadCollection<MinecraftProcessSnapshot>(_store.Resolve(MinecraftProcessStateComposition.SessionsKey))
            .Items.Any(snapshot => snapshot.SessionId == sessionId
                && snapshot.State is MinecraftProcessState.Exited
                    or MinecraftProcessState.Failed
                    or MinecraftProcessState.Cancelled);


    private IReadOnlyList<LaunchProfileView> ReadProfiles() =>
        _store.ReadCollection<LaunchProfileView>(_accountProfilesId).Items;

}
