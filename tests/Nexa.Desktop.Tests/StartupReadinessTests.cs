using Nexa.Desktop.Ui;
using Nexa.Xsr;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void StartupReadinessOrdersAndReusesFiniteSteps()
    {
        List<string> stages = [];
        DesktopStartupReadiness readiness = new(stages.Add, default);
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int admissions = 0;
        Task first = readiness.RunAsync("local-state", _ => { admissions++; return gate.Task; });
        Task reused = readiness.RunAsync("local-state", _ => throw new InvalidOperationException("Duplicate initialization."));
        AssertTrue(ReferenceEquals(first, reused));
        AssertEqual(1, admissions);
        AssertTrue(!first.IsCompleted);
        gate.SetResult();
        first.GetAwaiter().GetResult();
        readiness.RunAsync("native-scene", _ => Task.CompletedTask).GetAwaiter().GetResult();
        AssertEqual("local-state,native-scene", string.Join(',', stages));
    }

    private static void StartupReadinessBoundsOfflineWorkAndKeepsCancellation()
    {
        using var lifetime = new CancellationTokenSource();
        DesktopStartupReadiness readiness = new(_ => { }, lifetime.Token);
        CancellationToken providerToken = default;
        bool offline = false;
        readiness.RunAsync("online-facts", token =>
        {
            providerToken = token;
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, TimeSpan.FromMilliseconds(20), () => { offline = true; return Task.CompletedTask; }).GetAwaiter().GetResult();
        AssertTrue(offline && providerToken.IsCancellationRequested && !lifetime.IsCancellationRequested);
        bool providerOffline = false;
        using var providerCancellation = new CancellationTokenSource();
        providerCancellation.Cancel();
        readiness.RunAsync("provider-timeout", _ => Task.FromCanceled(providerCancellation.Token),
            onTimeout: () => { providerOffline = true; return Task.CompletedTask; }).GetAwaiter().GetResult();
        AssertTrue(providerOffline && !lifetime.IsCancellationRequested);

        bool canceledFallback = false;
        Task pending = readiness.RunAsync("cancelled-attempt", token => Task.Delay(Timeout.InfiniteTimeSpan, token),
            onTimeout: () => { canceledFallback = true; return Task.CompletedTask; });
        lifetime.Cancel();
        try { pending.GetAwaiter().GetResult(); throw new InvalidOperationException("A canceled attempt became ready."); }
        catch (OperationCanceledException) { }
        AssertTrue(!canceledFallback);
    }

    private static void StartupReadinessPropagatesMandatoryFailure()
    {
        DesktopStartupReadiness readiness = new(_ => { }, default);
        bool timedOut = false;
        try
        {
            readiness.RunAsync("required-local-state", _ => new TaskCompletionSource().Task,
                TimeSpan.FromMilliseconds(20)).GetAwaiter().GetResult();
        }
        catch (TimeoutException) { timedOut = true; }
        AssertTrue(timedOut);
        bool failed = false;
        try
        {
            readiness.RunAsync("corrupt-settings", _ => Task.FromException(new IOException("settings corrupt")))
                .GetAwaiter().GetResult();
        }
        catch (IOException error) { failed = error.Message == "settings corrupt"; }
        AssertTrue(failed);
    }

    private static void StartupSettingsMetadataPreparesRetainedPageBeforeNavigation()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([Instance("1.21")]));
        fixture.Controller.PrepareStartupAsync(default).GetAwaiter().GetResult();
        using SettingsPageController settings = new(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        settings.PrepareStartupAsync(default).GetAwaiter().GetResult();
        List<string> navigation = [];
        fixture.Shell.Tree.Walk(settings.Page, entity =>
        {
            string name = fixture.Shell.Tree.Name(entity);
            if (name.StartsWith("SettingsNav.", StringComparison.Ordinal)) navigation.Add(name);
            return true;
        });
        AssertTrue(navigation.Contains("SettingsNav.general") && navigation.Contains("SettingsNav.java"));
        settings.PrepareStartupAsync(default).GetAwaiter().GetResult();
        fixture.Controller.SettingsPage = settings.Page;
        fixture.Intents.Emit(XsrSemanticId.Parse("ui.navigation.settings"), default, XsrCorrelationId.Create());
        var scene = fixture.Shell.Render(new Nexa.UI.Next.XsrUiSize(850, 500));
        AssertTrue(scene.Count > 0 && fixture.Shell.Stage.Navigation.Current == settings.Page);
    }
}
