using Nexa.Desktop;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static readonly string[] JumpListFixtureRoutes = ["launch", "install", "resources", "settings"];

    private static void DesktopJumpListPreservesBoundedLauncherArguments()
    {
        string executable = Path.Combine(Path.GetTempPath(), "Application Space", "dotnet.exe");
        string assembly = Path.Combine(Path.GetTempPath(), "Application Space", "Nexa.Desktop.dll");
        var tasks = DesktopJumpList.CreateTasks([executable, assembly]);
        AssertEqual(4, tasks.Count);
        AssertEqual("Launch", DesktopJumpList.CreateTasks([executable], ["Launch", "Install", "Resources", "Settings"])[0].Title);
        foreach (var pair in tasks.Zip(JumpListFixtureRoutes))
        {
            AssertEqual(executable, pair.First.Executable);
            AssertEqual(Path.GetDirectoryName(executable)!, pair.First.WorkingDirectory);
            AssertEqual(DesktopJumpList.QuoteArgument(assembly) + " " + DesktopJumpList.QuoteArgument("nexacl://" + pair.Second), pair.First.Arguments);
        }
        AssertEqual("\"C:\\with space\\\\\"", DesktopJumpList.QuoteArgument("C:\\with space\\"));
        AssertEqual("\"a\\\"b\"", DesktopJumpList.QuoteArgument("a\"b"));
        foreach (string invalid in new[] { "relative.exe", executable + "\n", new string('a', 8193) })
        {
            bool refused = false;
            try { _ = DesktopJumpList.CreateTasks([invalid]); }
            catch (ArgumentException) { refused = true; }
            AssertTrue(refused);
        }
        if (!OperatingSystem.IsWindows()) AssertTrue(DesktopJumpList.ApplyAsync(true).GetAwaiter().GetResult() is not null);
    }
}
