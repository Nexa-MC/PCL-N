using Nexa.Desktop.Ui;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Settings;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void GameWindowPreferencesSuperviseHiddenAndOverlappingSessions()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var processes = fixture.Store.Resolve(MinecraftProcessStateComposition.SessionsKey);
        var launch = fixture.Store.Resolve(MinecraftLaunchProgressState.SnapshotKey);
        Queue<Action> queue = []; List<string> effects = []; List<MinecraftProcessSnapshot> all = [];
        long revision = 0;
        using var session = new DesktopGameWindowSession(fixture.Store, queue.Enqueue,
            () => effects.Add("hide"), () => effects.Add("minimize"), () => effects.Add("restore"), () => effects.Add("close"));
        var history = New(MinecraftLauncherVisibility.HideAndClose) with { State = MinecraftProcessState.Exited, ExitCode = 0 };
        Publish(history); Drain(); AssertEqual(0, effects.Count);
        var first = New(MinecraftLauncherVisibility.HideAndClose); Publish(first); Success(first); Drain();
        AssertEqual("hide", effects.Single());
        var second = New(MinecraftLauncherVisibility.Minimize); Publish(second); Success(second); Drain();
        AssertEqual("minimize", effects.Last());
        Publish(first with { State = MinecraftProcessState.Exited, ExitCode = 0 }); Drain(); AssertEqual(2, effects.Count);
        Publish(second with { State = MinecraftProcessState.Failed, ExitCode = 1 }); Drain();
        AssertEqual("restore", effects.Last()); AssertFalse(effects.Contains("close"));
        var normal = New(MinecraftLauncherVisibility.HideAndClose); Publish(normal); Success(normal); Drain();
        Publish(normal with { State = MinecraftProcessState.Exited, ExitCode = 0 }); Drain();
        AssertEqual("restore", effects[^2]); AssertEqual("close", effects[^1]);
        var keep = New(MinecraftLauncherVisibility.Keep); Publish(keep); Success(keep); Drain();
        int prior = effects.Count; Publish(keep with { State = MinecraftProcessState.Exited, ExitCode = 0 }); Drain(); AssertEqual(prior, effects.Count);
        var disposed = New(MinecraftLauncherVisibility.Hide); Publish(disposed); Success(disposed);
        session.Dispose(); Drain(); AssertEqual(prior, effects.Count);

        MinecraftProcessSnapshot New(MinecraftLauncherVisibility behavior) => new(Guid.NewGuid(), "same-id", 123,
            MinecraftProcessState.Running, null, DateTimeOffset.UtcNow, null)
        { LauncherVisibility = behavior };
        void Publish(MinecraftProcessSnapshot snapshot)
        {
            all.RemoveAll(item => item.SessionId == snapshot.SessionId); all.Add(snapshot);
            fixture.Store.PublishDelta(processes, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(revision++, [snapshot], []));
        }
        void Success(MinecraftProcessSnapshot snapshot) => fixture.Store.Publish(launch, new MinecraftLaunchProgressSnapshot(true, "end", 1, "", "", true, snapshot.SessionId));
        void Drain() { while (queue.TryDequeue(out var action)) action(); }
    }

    private static void GameWindowPreferencesUseSettingsSelectors()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Stage.Navigation.Replace(settings.Page); fixture.Shell.Renderer.ReducedMotion = true;
        var scene = fixture.Shell.Render(new(1000, 1500));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.game").Entity);
        scene = fixture.Shell.Render(new(1000, 1500));
        var isolation = FindByKey(fixture.Shell, scene, "SettingsOption.game.default-isolation.none");
        AssertTrue(fixture.Shell.Renderer.Activate(isolation.Entity)); fixture.Shell.Render(new(1000, 1500));
        // Wait for controller admission to reopen, not only for the committed value.
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            fixture.Shell.Render(new(1000, 1500));
            return !settings.SettingsWritePending && fixture.Foundation.Host.SettingsPolicy.Read(new()).Value!.Values
                .Single(value => value.Key == "game.default-isolation").Value.Value == "none";
        }, 5000));
        scene = fixture.Shell.Render(new(1000, 1500));
        var option = FindByKey(fixture.Shell, scene, "SettingsOption.game.launcher-visibility.hide");
        AssertTrue(fixture.Shell.Renderer.Activate(option.Entity)); fixture.Shell.Render(new(1000, 900));
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            fixture.Shell.Render(new(1000, 900));
            return !settings.SettingsWritePending && fixture.Foundation.Host.SettingsPolicy.Read(new()).Value!.Values
                .Single(value => value.Key == "game.launcher-visibility").Value.Value == "hide";
        }, TimeSpan.FromSeconds(5)));
    }
}
