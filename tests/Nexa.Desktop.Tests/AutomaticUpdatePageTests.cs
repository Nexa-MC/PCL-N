using Nexa.Desktop.Ui;
using Nexa.Services.Updates;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void AutomaticUpdateResumesOnStartupWithoutOpeningSettings()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        var update = new AutomaticUpdateRecoveryFixture();
        settings.ConfigureUpdates(fixture.Foundation.Queries, new("2.0.0.alpha.5", "win-x64", "alpha"), _ => { }, update);
        AssertTrue(fixture.Shell.Stage.Navigation.Current != settings.Page);
        fixture.Shell.Render(new(1000, 650));
        AssertEqual(1, update.InstallCount);
        fixture.Shell.Render(new(1000, 650));
        AssertEqual(1, update.InstallCount);
    }

    private static void AutomaticUpdateRestartFollowsSelectedVersionAndRollback()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true;
        fixture.Controller.SettingsPage = settings.Page;
        var update = new AutomaticUpdatePageFixture();
        bool restart = false;
        settings.ConfigureUpdates(fixture.Foundation.Queries, new("2.0.0.alpha.6", "win-x64", "alpha"),
            _ => { }, update, () => restart = true);
        Emit(fixture.Intents, "ui.navigation.settings");
        var scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.advanced").Entity);
        fixture.Shell.Render(new(1000, 650));
        fixture.Shell.Render(new(1000, 650));
        AssertFalse(Find("SettingsRestartUpdate").IsAssigned);
        AssertFalse(Find("SettingsResumeUpdate").IsAssigned);
        Emit(fixture.Intents, "ui.settings.update.rollback", Find("SettingsRollbackUpdate"));
        fixture.Shell.Render(new(1000, 650));
        fixture.Shell.Render(new(1000, 650));
        AssertEqual(1, update.RollbackCount);
        update.CompleteProgressRead();
        fixture.Shell.Render(new(1000, 650));
        var wake = fixture.Store.Resolve(SettingsPresentationState.WakeKey);
        long revision = fixture.Store.Read<long>(wake).Value;
        AssertTrue(SpinWait.SpinUntil(() => fixture.Store.Read<long>(wake).Value > revision, TimeSpan.FromSeconds(3)));
        fixture.Shell.Render(new(1000, 650)); // The timer wakes the next read without pointer input.
        update.CompleteRollback();
        fixture.Shell.Render(new(1000, 650));
        update.CompleteStaleRead();
        fixture.Shell.Render(new(1000, 650));
        AssertTrue(Find("SettingsRestartUpdate").IsAssigned);
        AssertFalse(Find("SettingsResumeUpdate").IsAssigned);
        Emit(fixture.Intents, "ui.settings.update.restart", Find("SettingsRestartUpdate"));
        fixture.Shell.Render(new(1000, 650));
        AssertTrue(restart);

        XsrUiEntityId Find(string name)
        {
            XsrUiEntityId found = default;
            fixture.Shell.Tree.Walk(settings.Page, entity =>
            { if (fixture.Shell.Tree.Name(entity) == name) found = entity; return true; });
            return found;
        }
    }

    private sealed class AutomaticUpdatePageFixture : IAutomaticUpdateControl
    {
        private AutomaticUpdateStatus _status = new("2.0.0.alpha.6", "alpha", "complete", true, true);
        private readonly TaskCompletionSource<AutomaticUpdateStatus> _rollback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<AutomaticUpdateStatus> _progressRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<AutomaticUpdateStatus> _staleRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;
        internal int RollbackCount { get; private set; }
        public Task<AutomaticUpdateStatus> ReadAsync(CancellationToken token) => ++_reads switch
        { 1 => Task.FromResult(_status), 2 => _progressRead.Task, _ => _staleRead.Task };
        public Task<AutomaticUpdateStatus> InstallAsync(string version, string channel, CancellationToken token)
            => throw new InvalidOperationException("Completed update must not resume.");
        public Task<AutomaticUpdateStatus> RollbackAsync(CancellationToken token)
        {
            RollbackCount++;
            return _rollback.Task;
        }
        internal void CompleteRollback()
        {
            _status = new("2.0.0.alpha.5", "", "rolledback", true, false);
            _rollback.SetResult(_status);
        }
        internal void CompleteStaleRead() => _staleRead.SetResult(new("2.0.0.alpha.6", "alpha", "complete", true, true));
        internal void CompleteProgressRead() => _progressRead.SetResult(new("2.0.0.alpha.6", "alpha", "preparing", true, false));
        public void Restart() => throw new InvalidOperationException("Restart must use the host close lifecycle.");
    }

    private sealed class AutomaticUpdateRecoveryFixture : IAutomaticUpdateControl
    {
        private readonly TaskCompletionSource<AutomaticUpdateStatus> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int InstallCount { get; private set; }
        public Task<AutomaticUpdateStatus> ReadAsync(CancellationToken token)
            => Task.FromResult(new AutomaticUpdateStatus("2.0.0.alpha.6", "alpha", "paused", true, false));
        public Task<AutomaticUpdateStatus> InstallAsync(string version, string channel, CancellationToken token)
        {
            AssertEqual("2.0.0.alpha.6", version); AssertEqual("alpha", channel); InstallCount++;
            return _pending.Task;
        }
        public Task<AutomaticUpdateStatus> RollbackAsync(CancellationToken token) => throw new InvalidOperationException();
        public void Restart() => throw new InvalidOperationException();
    }
}
