using System.Collections.Concurrent;
using Nexa.Desktop.Ui;
using Nexa.Services.Settings;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void ReducedMotionConsumesTypedCommittedPreferenceAndLegacyOr()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        fixture.Controller.Dispose();
        AssertTrue(fixture.Foundation.Commands.TryResolve(SettingsPolicyContract.SetCommand, out var set));
        ConcurrentQueue<Action> posted = new();
        int appliedPosts = 0;
        using var session = new DesktopPresentationSession(fixture.Shell, fixture.Store, _ => { }, queries: fixture.Foundation.Queries,
            postToRender: posted.Enqueue);
        void Set(string key, bool value)
        {
            var dispatched = fixture.Foundation.Commands.Dispatch(set, new SettingsMutation(key, SettingsLayer.Global,
                new(SettingsOverrideMode.Custom, value ? "true" : "false")));
            AssertTrue(dispatched.Completion.GetAwaiter().GetResult().IsSuccess);
        }
        void Pump()
        {
            while (posted.TryDequeue(out var action)) { action(); appliedPosts++; }
            fixture.Shell.Render(new(1000, 650));
        }
        void Wait(bool expected, int afterPosts = -1) => AssertTrue(SpinWait.SpinUntil(() =>
        {
            Pump();
            return fixture.Shell.Renderer.ReducedMotion == expected && appliedPosts > afterPosts;
        }, TimeSpan.FromSeconds(5)));
        IReadOnlyDictionary<string, string?> Values()
        {
            var result = CommittedSettingsRead.QueryAsync(fixture.Foundation.Queries, default).AsTask().GetAwaiter().GetResult();
            AssertTrue(result is not null);
            return result!.Values.ToDictionary(item => item.Key, item => item.Value.Value);
        }
        Pump(); AssertFalse(fixture.Shell.Renderer.ReducedMotion);
        int before = appliedPosts;
        Set("appearance.reduced-motion", true); Wait(true, before);
        AssertEqual("false", Values()["appearance.animations-disabled"]); // The independent preference does not rewrite the legacy flag.
        AssertFalse(DesktopMediaSession.VideoPlaybackAllowed("/tmp/background.mp4", Values(), false, false));
        Set("appearance.animations-disabled", true); Wait(true);
        before = appliedPosts;
        Set("appearance.reduced-motion", false); Wait(true, before); // Legacy motion disable remains effective after the routed read.
        AssertFalse(DesktopMediaSession.VideoPlaybackAllowed("/tmp/background.mp4", Values(), false, false));
        Set("appearance.animations-disabled", false); Wait(false);
        AssertTrue(DesktopMediaSession.VideoPlaybackAllowed("/tmp/background.mp4", Values(), false, false));
        AssertFalse(DesktopMediaSession.VideoPlaybackAllowed("/tmp/background.mp4", Values(), true, false));
        before = appliedPosts;
        Set("appearance.reduced-motion", true); Wait(true, before);
        Set("music.enabled", true); Set("music.startup", true);
        AssertEqual("true", Values()["music.enabled"]); AssertEqual("true", Values()["music.startup"]);
        session.Dispose();
        Set("appearance.reduced-motion", false); Pump();
        AssertTrue(fixture.Shell.Renderer.ReducedMotion); // Retired consumers cannot change the renderer or execute pending UI work.
    }
}
