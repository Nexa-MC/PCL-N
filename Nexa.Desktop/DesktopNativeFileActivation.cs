using Nexa.UI.Next.Backend.Avalonia;

namespace Nexa.Desktop;

/// <summary>Product admission for native macOS Open With events, including first-run setup.</summary>
internal static class DesktopNativeFileActivation
{
    internal static IDisposable Subscribe(Action<string> activate, Action<string>? report = null)
    {
        ArgumentNullException.ThrowIfNull(activate);
        return AvaloniaUiFileActivation.Subscribe(files =>
        {
            foreach (string path in files)
            {
                if (DesktopActivation.TryFile(path, out string admitted)) activate(admitted);
                else report?.Invoke("不支持的本地整合包路径。");
            }
        });
    }
}
