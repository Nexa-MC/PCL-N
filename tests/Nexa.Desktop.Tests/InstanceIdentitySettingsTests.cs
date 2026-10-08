using System.Text.Json.Nodes;
using Nexa.Desktop.Ui;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Management;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void InstanceIdentityEditorWritesTypedMetadataAndShowsServerRequirements()
    {
        string root = Path.Combine(Path.GetTempPath(), "nexacl-identity-ui-" + Guid.NewGuid().ToString("N"));
        string instance = Path.Combine(root, "versions", "identity"); Directory.CreateDirectory(instance);
        File.WriteAllText(Path.Combine(instance, "identity.json"), new JsonObject { ["id"] = "identity", ["mainClass"] = "fixture.Main", ["_minecraftVersion"] = "1.20.1", ["libraries"] = new JsonArray() }.ToJsonString());
        try
        {
            using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
            using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries, fixture.Foundation.Commands,
                fixture.Store, fixture.Feedback, () => instance);
            string? launched = null, modified = null;
            settings.LaunchManagementInstance = path => launched = path;
            settings.ModifyManagementInstance = path => modified = path;
            fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
            Wait(() => Find("InstanceIdentityInput.name", false).IsAssigned);
            fixture.Shell.Renderer.SetTextInputValue(Find("InstanceIdentityInput.name"), "Custom display");
            fixture.Shell.Renderer.SetTextInputValue(Find("InstanceIdentityInput.tags"), "tag-one, tag-two");
            fixture.Shell.Renderer.SetTextInputValue(Find("InstanceIdentityInput.modpack-project"), "project:42");
            fixture.Shell.Renderer.SetTextInputValue(Find("InstanceIdentityInput.modpack-version"), "2026.10");
            Emit(fixture.Intents, "ui.settings.instance-identity.action", Find("InstanceIdentityIsolation"));
            fixture.Shell.Renderer.SetTextInputValue(Find("InstanceNoteInput.0"), "First note");
            Emit(fixture.Intents, "ui.settings.instance-identity.action", Find("InstanceIdentitySave"));
            Wait(() => Read().Fields.DisplayName == "Custom display");
            AssertTrue(Read().Fields.Tags.SequenceEqual(["tag-one", "tag-two"])); AssertEqual("First note", Read().Fields.Notes);
            AssertEqual("project:42", Read().Fields.ModpackProject); AssertEqual("2026.10", Read().Fields.ModpackVersion);
            AssertTrue(!Read().Fields.InstanceIsolation);
            Wait(() => Find("InstanceOverviewLaunch", false).IsAssigned && Find("InstanceOverviewModify", false).IsAssigned);
            Emit(fixture.Intents, "ui.settings.instance-identity.action", Find("InstanceOverviewLaunch"));
            Emit(fixture.Intents, "ui.settings.instance-identity.action", Find("InstanceOverviewModify"));
            Wait(() => launched == instance && modified == instance);
            AssertEqual(instance, launched); AssertEqual(instance, modified);
            Wait(() => Find("SettingsNav.servers", false).IsAssigned);
            Emit(fixture.Intents, "ui.settings.section", Find("SettingsNav.servers"));
            Wait(() => Find("InstanceIdentityInput.game-version", false).IsAssigned);
            fixture.Shell.Renderer.SetTextInputValue(Find("InstanceIdentityInput.game-version"), "1.19.4");
            Emit(fixture.Intents, "ui.settings.instance-identity.action", Find("InstanceIdentitySave"));
            Wait(() => Read().Server.ExpectedGameVersion == "1.19.4");
            AssertTrue(Read().EnvironmentMismatches.Count > 0);
            AssertEqual("Custom display", Read().Fields.DisplayName);

            InstanceIdentitySnapshot Read() => InstanceIdentityService.ReadAsync(new(instance)).GetAwaiter().GetResult();
            void Wait(Func<bool> condition) => AssertTrue(SpinWait.SpinUntil(() =>
            { fixture.Shell.Render(new(1200, 900)); return condition(); }, TimeSpan.FromSeconds(5)));
            XsrUiEntityId Find(string name, bool required = true)
            {
                XsrUiEntityId found = default;
                fixture.Shell.Tree.Walk(settings.Page, entity => { if (fixture.Shell.Tree.Name(entity) == name) found = entity; return true; });
                if (required) AssertTrue(found.IsAssigned); return found;
            }
        }
        finally { Directory.Delete(root, true); }
        InstanceOverviewActionsRequireCurrentLibraryIdentity();
    }

    private static void InstanceOverviewActionsRequireCurrentLibraryIdentity()
    {
        var recording = new RecordingStartRoute();
        using var runtime = CreateRecordingRuntime(recording);
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([Instance("playable")]), runtime,
            addProfile: true, ownsMinecraftRuntime: false);
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        var state = fixture.Store.Resolve(MinecraftLibraryService.StateKey);
        var snapshot = (MinecraftLibrarySnapshot)fixture.Store.ReadAppliedValue(state)!;
        string directory = Path.Combine(snapshot.RootDirectory, "versions", "playable");
        Directory.CreateDirectory(directory);
        string manifest = Path.Combine(directory, "playable.json");
        File.WriteAllText(manifest, """{"id":"playable","mainClass":"fixture.Main","_minecraftVersion":"1.20.1","libraries":[]}""");
        var descriptor = snapshot.SelectedInstance!;
        descriptor = descriptor with { DirectoryPath = directory, Version = descriptor.Version with { DirectoryPath = directory, JsonPath = manifest } };
        fixture.Store.Publish(state, snapshot with { Instances = [descriptor] });
        fixture.Shell.Renderer.ReducedMotion = true;
        fixture.Shell.Render(new(850, 600));
        var originalPage = fixture.Shell.Stage.Navigation.Current;
        fixture.Controller.LaunchInstance(Path.Combine(snapshot.RootDirectory, "versions", "other"));
        AssertTrue(recording.LastCommand is null);
        fixture.Controller.ModifyInstance(Path.Combine(snapshot.RootDirectory, "versions", "other"));
        AssertEqual(originalPage, fixture.Shell.Stage.Navigation.Current);
        fixture.Controller.ModifyInstance(directory);
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        AssertEqual("修改版本", FindByKey(fixture.Shell, fixture.Shell.Render(new(850, 600)), "TitleSubpage").Text);
        Emit(fixture.Intents, "ui.page.back"); fixture.Shell.Render(new(850, 600));
        fixture.Controller.LaunchInstance(directory);
        AssertTrue(SpinWait.SpinUntil(() => recording.LastCommand is not null, TimeSpan.FromSeconds(2)));
        AssertEqual("playable", recording.LastCommand!.InstanceId);
        AssertEqual(snapshot.RootDirectory, recording.LastCommand.MinecraftRootDirectory);
        AssertEqual(0, recording.LastCommand.AccountIndex);
    }
}
