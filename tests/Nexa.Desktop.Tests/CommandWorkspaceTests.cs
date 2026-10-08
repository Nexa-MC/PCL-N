using Nexa.Desktop.Ui;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void CommandLineAdmitsOnlyFiniteNavigationAndExplicitSafeMode()
    {
        foreach (var route in DesktopCommandLine.Commands)
        {
            var parsed = DesktopCommandLine.Parse(["--command=" + route.Id, "--safe-mode", "--validate-shell"]);
            AssertTrue(parsed.IsSuccess); AssertTrue(parsed.SafeMode); AssertFalse(parsed.ListCommands);
            AssertEqual(route, parsed.Command!);
            foreach (string uri in new[] { "nexacl://" + route.Id, "nexacl://open/" + route.Id })
            {
                AssertTrue(DesktopActivation.TryParse(uri, out var destination));
                AssertEqual(Enum.Parse<DesktopDestination>(route.Id, ignoreCase: true), destination);
            }
        }
        AssertEqual((byte)4, (byte)DesktopDestination.Activate);
        foreach (string[] arguments in new string[][]
        {
            ["--command="], ["--command"], ["--command=unknown"], ["--command=launch;whoami"],
            ["--command=launch", "--command=install"], ["--list-commands", "--command=launch"],
            ["--safe-mode=false"], ["--safe-mode", "--safe-mode"], ["--list-commands", "--list-commands"],
            ["--list-commands=true"], ["--command=launch\n"], [new string('x', 8193)],
        })
            AssertFalse(DesktopCommandLine.Parse(arguments).IsSuccess);
        AssertFalse(DesktopCommandLine.Parse(Enumerable.Repeat("x", 129).ToArray()).IsSuccess);
        AssertTrue(DesktopCommandLine.Parse(["--list-commands", "--safe-mode"]).ListCommands);
        AssertFalse(DesktopCommandLine.Parse([]).SafeMode);
        var catalog = new UiLocalizationCatalog(); catalog.SetLanguage("en");
        string[] listing = DesktopCommandLine.FormatListing(catalog.Translate).Split(Environment.NewLine);
        AssertEqual(8, listing.Length); AssertEqual("settings\tSettings", listing[3]);
        AssertEqual("about\tAbout", listing[^1]);
    }

    private static void CommandPaletteFiltersLocalizedRoutesAndRetiresSources()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var shell = fixture.Shell; var platform = new AvaloniaUiPlatformActions();
        var catalog = new UiLocalizationCatalog(); catalog.SetLanguage("en"); shell.Renderer.TextLocalizer = catalog.Translate;
        shell.Render(new(1100, 750));
        var previous = shell.NavigationEntities.Values.First(); shell.Renderer.Focus(previous);
        var selected = new List<string>();
        using var palette = new DesktopCommandPalette(shell, fixture.Intents, platform, route => selected.Add(route.Id));
        AssertTrue(platform.CommandPaletteOpenRequested!.Invoke());
        var scene = shell.Render(new(1100, 750));
        AssertTrue(scene.Nodes.Any(node => node.Text == "Command palette"));
        AssertEqual(palette.Find("CommandPaletteSearch"), shell.Renderer.Focused);
        var storage = palette.Find("CommandPaletteCommand.storage");
        var about = palette.Find("CommandPaletteCommand.about");
        AssertTrue(scene.Nodes.Single(node => node.Entity == storage).Rect.Height > 0);
        shell.Renderer.SetTextInputValue(palette.Find("CommandPaletteSearch"), "About"); shell.Render(new(1100, 750));
        AssertEqual(storage, palette.Find("CommandPaletteCommand.storage"));
        AssertFalse(shell.Tree.GetComponent<XsrUiElement>(storage)!.IsVisible);
        Emit(fixture.Intents, "ui.command-palette.execute", storage); AssertEqual(0, selected.Count);
        Emit(fixture.Intents, "ui.command-palette.execute", previous); AssertEqual(0, selected.Count);
        // The draft is checked again at admission, even before the next render filters rows.
        shell.Renderer.SetTextInputValue(palette.Find("CommandPaletteSearch"), "storage");
        Emit(fixture.Intents, "ui.command-palette.execute", about); AssertEqual(0, selected.Count);
        shell.Render(new(1100, 750));
        Emit(fixture.Intents, "ui.command-palette.execute", storage);
        AssertEqual("storage", selected.Single()); AssertFalse(palette.IsOpen); AssertFalse(shell.Tree.IsAlive(storage));
        AssertEqual(previous, shell.Renderer.Focused);
        AssertTrue(palette.Open()); shell.Render(new(1100, 750));
        Emit(fixture.Intents, "ui.command-palette.execute", about); AssertEqual(1, selected.Count);
        shell.Renderer.SetTextInputValue(palette.Find("CommandPaletteSearch"), "没有匹配项"); shell.Render(new(1100, 750));
        AssertTrue(shell.Tree.GetComponent<XsrUiElement>(palette.Find("CommandPaletteEmpty"))!.IsVisible);
        AssertTrue(platform.CommandPaletteCloseRequested!.Invoke()); AssertFalse(platform.CommandPaletteCloseRequested.Invoke());
        AssertTrue(palette.Open()); shell.Render(new(1100, 750));
        var live = palette.Find("CommandPaletteCommand.launch"); palette.Dispose();
        Emit(fixture.Intents, "ui.command-palette.execute", live); AssertEqual(1, selected.Count);
        AssertTrue(platform.CommandPaletteOpenRequested is null); AssertTrue(platform.CommandPaletteCloseRequested is null);
    }

    private static void CommandRoutesForwardNumericDestinationsAndSafeBootstrapRefusesOwner()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nexacl-command-" + Guid.NewGuid().ToString("N"));
        try
        {
            var primary = DesktopSingleInstance.AcquireAsync(directory, DesktopDestination.Activate).GetAwaiter().GetResult()!;
            try
            {
                AssertTrue(primary.TryTake(out var first)); AssertEqual(DesktopDestination.Activate, first);
                foreach (var route in DesktopCommandLine.Commands)
                {
                    var destination = Enum.Parse<DesktopDestination>(route.Id, ignoreCase: true);
                    AssertTrue(DesktopSingleInstance.AcquireAsync(directory, destination).GetAwaiter().GetResult() is null);
                    AssertTrue(primary.TryTake(out var forwarded)); AssertEqual(destination, forwarded);
                }
                AssertFalse(primary.TryTake(out _));
                AssertTrue(DesktopSingleInstance.AcquireAsync(directory, DesktopDestination.Settings, forwardActivation: false).GetAwaiter().GetResult() is null);
                AssertFalse(primary.TryTake(out _)); // Safe-mode rejection never degrades to route forwarding.
            }
            finally { primary.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static void CommandPaletteDefersToModalDecisionsAndClosesOnNavigation()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var shell = fixture.Shell; var platform = new AvaloniaUiPlatformActions();
        using var palette = new DesktopCommandPalette(shell, fixture.Intents, platform, _ => throw new InvalidOperationException("Unexpected navigation."));
        var dialog = shell.Tree.Create("ExistingModal"); shell.Tree.SetComponent(dialog, new XsrUiElement()); shell.Stage.Show(dialog, modal: true);
        AssertFalse(palette.Open()); shell.Stage.Dismiss(dialog); shell.Tree.Destroy(dialog);
        AssertTrue(palette.Open()); var row = palette.Find("CommandPaletteCommand.about");
        var page = shell.Tree.Create("ExternalNavigation"); shell.Tree.SetComponent(page, new XsrUiElement()); shell.Stage.Navigation.Replace(page);
        Emit(fixture.Intents, "ui.command-palette.execute", row); shell.Render(new(1000, 700));
        AssertFalse(palette.IsOpen); AssertFalse(shell.Tree.IsAlive(row));
    }
}
