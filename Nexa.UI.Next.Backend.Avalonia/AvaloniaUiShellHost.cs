using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Nexa.UI.Next;

namespace Nexa.UI.Next.Backend.Avalonia;

/// <summary>
/// Owns the Avalonia application edge for the XSR product shell. The shell contract is supplied
/// by the composition root; this host only creates the native application and window, and runs
/// the legacy startup choreography: the splash appears immediately, the shell window shows
/// underneath it, and the splash fades once the shell window is on screen.
/// </summary>
public static class AvaloniaUiShellHost
{
    private static XsrUiShell? _shell;
    private static AvaloniaUiPlatformActions? _platformActions;

    /// <summary>
    /// Builds an Avalonia app configured for the supplied shell. Keeping this separate from
    /// <see cref="Run"/> makes the composition edge testable without starting a native loop.
    /// </summary>
    public static AppBuilder Build(XsrUiShell shell, AvaloniaUiPlatformActions? platformActions = null,
        bool disableHardwareAcceleration = false)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;
        _platformActions = platformActions;
        return new AvaloniaUiRenderingConfiguration(disableHardwareAcceleration)
            .Apply(AppBuilder.Configure<ShellApplication>().UsePlatformDetect());
    }

    /// <summary>
    /// Starts the classic desktop lifetime: splash first, then the shell window with the splash
    /// dismissing on top of it.
    /// </summary>
    public static int Run(XsrUiShell shell, string[]? args = null, AvaloniaUiPlatformActions? platformActions = null,
        bool disableHardwareAcceleration = false)
    {
        if (AvaloniaUiStartupSession.Active is { } startup)
            return startup.Present(shell, platformActions, TryOpenProductAsset("Nexa.Desktop.Assets.icon.png"));
        return Build(shell, platformActions, disableHardwareAcceleration).StartWithClassicDesktopLifetime(args ?? []);
    }

    internal static void BindActivation(Application application, AvaloniaUiPlatformActions? actions)
    {
        if (application.TryGetFeature<IActivatableLifetime>() is { } activation)
            activation.Activated += (_, args) =>
            {
                if (args is ProtocolActivatedEventArgs protocol) actions?.OnProtocolActivated(protocol.Uri.AbsoluteUri);
                else if (args.Kind == ActivationKind.Reopen) actions?.RestoreWindow();
            };
    }

    private static XsrUiShell CurrentShell =>
        _shell ?? throw new InvalidOperationException("The Avalonia shell host was not configured.");

    /// <summary>
    /// Resolves a brand asset embedded in the product assembly (the composition root owns the
    /// assets; this host only consumes streams). Missing assets disable the decoration that
    /// needed them — the splash is never a startup dependency.
    /// </summary>
    private static Stream? TryOpenProductAsset(string resourceName)
    {
        try
        {
            Assembly? product = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly =>
                    string.Equals(assembly.GetName().Name, "Nexa.Desktop", StringComparison.Ordinal));
            return product?.GetManifestResourceStream(resourceName);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class ShellApplication : Application
    {
        public override void OnFrameworkInitializationCompleted()
        {
            AvaloniaUiFileActivation.Initialize(this);
            Styles.Add(new FluentTheme());
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // A second stream per consumer: the splash, the taskbar icon, and the
                // close-collapse overlay each decode their own copy of the product icon.
                AvaloniaUiShellWindow window = AvaloniaUiShellLifetime.Compose(
                    desktop,
                    CurrentShell,
                    TryOpenProductAsset("Nexa.Desktop.Assets.icon.png"),
                    TryOpenProductAsset("Nexa.Desktop.Assets.icon.png"));
                _platformActions?.Attach(window);
                BindActivation(this, _platformActions);
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
