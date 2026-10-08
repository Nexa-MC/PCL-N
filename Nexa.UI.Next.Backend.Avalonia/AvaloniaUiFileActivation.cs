using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Nexa.UI.Next.Backend.Avalonia;

/// <summary>Typed native document events, retained until the product supplies its admission callback.</summary>
public static class AvaloniaUiFileActivation
{
    private static Application? _application;
    private static readonly Queue<string> Pending = new(16);
    private static Action<IReadOnlyList<string>>? _consumer;

    public static void Initialize(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (!Dispatcher.UIThread.CheckAccess()) throw new InvalidOperationException("Native activation must initialize on the UI dispatcher.");
        if (ReferenceEquals(_application, application)) return;
        _application = application;
        if (application.TryGetFeature<IActivatableLifetime>() is { } lifetime)
            lifetime.Activated += OnActivated;
    }

    public static IDisposable Subscribe(Action<IReadOnlyList<string>> consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        void SubscribeOnDispatcher()
        {
            if (Application.Current is { } application) Initialize(application);
            _consumer = consumer;
            if (Pending.Count == 0) return;
            string[] files = Pending.ToArray(); Pending.Clear(); consumer(files);
        }
        if (Dispatcher.UIThread.CheckAccess()) SubscribeOnDispatcher();
        else Dispatcher.UIThread.InvokeAsync(SubscribeOnDispatcher).GetAwaiter().GetResult();
        return new Subscription(consumer);
    }

    private static void OnActivated(object? sender, ActivatedEventArgs args)
    {
        if (args is not FileActivatedEventArgs fileArgs) return;
        List<string> files = [];
        foreach (IStorageItem item in fileArgs.Files.Take(16))
        {
            string? path = item.TryGetLocalPath();
            if (path is null || path.Length > 8192 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path)) continue;
            files.Add(path);
        }
        if (_consumer is { } consumer) { if (files.Count > 0) consumer(files.AsReadOnly()); return; }
        foreach (string path in files) if (Pending.Count < 16) Pending.Enqueue(path);
    }
    private sealed class Subscription(Action<IReadOnlyList<string>> consumer) : IDisposable
    {
        private Action<IReadOnlyList<string>>? _subscribed = consumer;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _subscribed, null) is not { } previous) return;
            void Remove() { if (ReferenceEquals(_consumer, previous)) _consumer = null; }
            if (Dispatcher.UIThread.CheckAccess()) Remove(); else Dispatcher.UIThread.Post(Remove);
        }
    }
}
