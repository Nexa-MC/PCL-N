namespace Nexa.Platform.Updates;

public static class UpdateInstallation
{
    public static string Root => OperatingSystem.IsWindows()
        ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NexaCL")
        : OperatingSystem.IsMacOS() ? "/Library/Application Support/NexaCL" : "/usr/lib/nexacl";
    public static string Helper => System.IO.Path.Combine(Root, "Nexa.Update.Helper" + (OperatingSystem.IsWindows() ? ".exe" : ""));
    public static string Bootstrap => OperatingSystem.IsMacOS()
        ? System.IO.Path.Combine(Root, "Nexa.app/Contents/MacOS/Nexa.Desktop")
        : System.IO.Path.Combine(Root, "Nexa.Desktop" + (OperatingSystem.IsWindows() ? ".exe" : ""));
}
