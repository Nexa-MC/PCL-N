using System.Globalization;
using Nexa.Desktop.Ui;
using Nexa.Services.Minecraft.Management;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static async ValueTask RecoveryTimelineOrdersPersistedPointsBoundsRowsAndRetiresInstanceHistory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nexa-recovery-timeline-" + Guid.NewGuid().ToString("N"));
        string first = Path.Combine(directory, "root-a", "versions", "same-name"), second = Path.Combine(directory, "root-b", "versions", "same-name");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        try
        {
            foreach (string instance in new[] { first, second })
                await File.WriteAllTextAsync(Path.Combine(instance, "same-name.json"), """{"id":"same-name","_minecraftVersion":"1.20.1"}""");
            using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
            var snapshots = new RecoverySnapshotStore(first, first);
            var captured = new List<RecoverySnapshot>();
            string settingsDocument = fixture.Foundation.Host.SettingsPolicy.CaptureRecoverySettings(first);
            await File.WriteAllTextAsync(Path.Combine(first, "optionsof.txt"), "fixture");
            for (int index = 0; index < 18; index++)
            {
                await File.WriteAllTextAsync(Path.Combine(first, "options.txt"), "fixture:" + index.ToString(CultureInfo.InvariantCulture));
                RecoverySource[] sources = index % 2 == 0 ? [new("instance", "options.txt")] : [new("instance", "options.txt"), new("instance", "optionsof.txt")];
                captured.Add(await snapshots.CaptureAsync(sources, settingsDocument, null, retainHistory: true));
            }
            await File.WriteAllTextAsync(Path.Combine(second, "options.txt"), "second-root");
            var other = await new RecoverySnapshotStore(second, second).CaptureAsync([new("instance", "options.txt")], fixture.Foundation.Host.SettingsPolicy.CaptureRecoverySettings(second));
            string selected = first;
            using var controller = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries, fixture.Foundation.Commands,
                fixture.Store, fixture.Feedback, () => selected);
            fixture.Shell.Renderer.ReducedMotion = true;
            fixture.Shell.Stage.Navigation.Replace(controller.Page);
            var scene = fixture.Shell.Render(new(1000, 650));
            AssertTrue(SpinWait.SpinUntil(() =>
            {
                scene = fixture.Shell.Render(new(1000, 650));
                return HasKey(fixture.Shell, scene, "SettingsNav.recovery");
            }, TimeSpan.FromSeconds(10)));
            Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.recovery").Entity);
            List<XsrUiEntityId> Rows()
            {
                List<XsrUiEntityId> rows = [];
                fixture.Shell.Tree.Walk(controller.Page, entity =>
                {
                    string name = fixture.Shell.Tree.Name(entity);
                    if (name.StartsWith("RecoveryTimeline.", StringComparison.Ordinal) && Guid.TryParseExact(name.AsSpan("RecoveryTimeline.".Length), "D", out _)) rows.Add(entity);
                    return true;
                });
                return rows;
            }
            AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1000, 650)); return Rows().Count == 16; }, TimeSpan.FromSeconds(10)));
            var expected = captured.OrderBy(point => point.CapturedAt).ThenBy(point => point.Revision).TakeLast(16).ToArray();
            var rows = Rows();
            for (int index = 0; index < expected.Length; index++)
            {
                AssertEqual("RecoveryTimeline." + expected[index].Revision, fixture.Shell.Tree.Name(rows[index]));
                var caption = fixture.Shell.Tree.GetComponent<XsrUiText>(fixture.Shell.Tree.Children(rows[index])[0])!;
                AssertTrue(caption.Content.StartsWith(expected[index].CapturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture), StringComparison.Ordinal));
                AssertTrue(caption.Content.Contains(" · " + expected[index].Files.Count.ToString(CultureInfo.InvariantCulture) + " ", StringComparison.Ordinal));
                AssertFalse(caption.Localize);
            }
            AssertEqual(1, fixture.Shell.Tree.Children(rows[0]).Count);
            AssertEqual(2, fixture.Shell.Tree.Children(rows[1]).Count);
            AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiText>(fixture.Shell.Tree.Children(rows[1])[1])!.Content.EndsWith(
                (expected[1].Files.Count - expected[0].Files.Count).ToString("+0;-0;0", CultureInfo.InvariantCulture), StringComparison.Ordinal));
            selected = second;
            AssertTrue(SpinWait.SpinUntil(() =>
            {
                scene = fixture.Shell.Render(new(1000, 650));
                return controller.SelectedSection == "overview" && Rows().Count == 0 && HasKey(fixture.Shell, scene, "SettingsNav.recovery");
            }, TimeSpan.FromSeconds(10)));
            Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.recovery").Entity);
            AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1000, 650)); return Rows().Count == 1; }, TimeSpan.FromSeconds(10)));
            AssertEqual("RecoveryTimeline." + other.Revision, fixture.Shell.Tree.Name(Rows()[0]));
        }
        finally { Directory.Delete(directory, true); }
    }
}
