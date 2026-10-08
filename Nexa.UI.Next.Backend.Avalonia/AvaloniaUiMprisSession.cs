using System.Runtime.InteropServices;

namespace Nexa.UI.Next.Backend.Avalonia;

/// <summary>Linux MPRIS native ABI. One owned connection/thread; all controls cross an explicit callback.</summary>
public sealed partial class AvaloniaUiMprisSession : IAsyncDisposable
{
    private readonly Func<AvaloniaUiMediaState> _capture;
    private readonly Func<double> _volume;
    private readonly Action<string, double?> _control;
    private readonly Action<string> _report;
    private readonly Func<bool> _enabled;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;
    private const string PathName = "/org/mpris/MediaPlayer2";
    private const string Player = "org.mpris.MediaPlayer2.Player";
    private const string Root = "org.mpris.MediaPlayer2";
    public AvaloniaUiMprisSession(Func<AvaloniaUiMediaState> capture, Func<double> volume,
        Action<string, double?> control, Action<string> report, Func<bool>? enabled = null)
    {
        _capture = capture; _volume = volume; _control = control; _report = report;
        _enabled = enabled ?? (() => true);
        _run = OperatingSystem.IsLinux() ? Task.Run(Run) : Task.CompletedTask;
        if (!OperatingSystem.IsLinux()) report("MPRIS: PlatformUnsupported.");
    }

    private void Run()
    {
        nint connection = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
            { _report("MPRIS: DependencyMissing (session bus)."); return; }
            if (InitThreads() == 0) { _report("MPRIS: native thread initialization failed."); return; }
            connection = BusGetPrivate(0, 0);
            if (connection == 0) { _report("MPRIS: DependencyMissing (session bus)."); return; }
            SetExitOnDisconnect(connection, 0);
            if (RequestName(connection, "org.mpris.MediaPlayer2.nexacl", 4, 0) != 1)
            { _report("MPRIS: service name is already owned."); return; }
            string? published = null;
            while (!_stop.IsCancellationRequested && ReadWrite(connection, 200) != 0)
            {
                string current = PlaybackStatus();
                if (published != current) { PublishStatus(connection, current); published = current; }
                for (int count = 0; count < 32 && PopMessage(connection) is var message && message != 0; count++)
                {
                    try { if (MessageType(message) == 1 && Text(GetPath(message)) == PathName) Dispatch(connection, message); }
                    finally { UnrefMessage(message); }
                }
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        { _report("MPRIS: DependencyMissing (" + error.GetType().Name + ")."); }
        finally { if (connection != 0) { CloseConnection(connection); UnrefConnection(connection); } }
    }
    private string PlaybackStatus() => _capture().Status switch
    { AvaloniaUiMediaStatus.Playing => "Playing", AvaloniaUiMediaStatus.Paused => "Paused", _ => "Stopped" };
    private static string Text(nint pointer) => Marshal.PtrToStringUTF8(pointer) ?? "";

    private unsafe void Dispatch(nint connection, nint message)
    {
        string iface = Text(GetInterface(message)), member = Text(GetMember(message));
        nint reply;
        if (iface == Player && member is "Play" or "Pause" or "PlayPause" or "Stop" or "Next")
        {
            _control(member switch { "PlayPause" => "play-pause", "Next" => "next", "Stop" => "stop", "Pause" => "pause", _ => "play" }, null);
            reply = MethodReturn(message);
        }
        else if (iface == Root && member == "Raise") { _control("raise", null); reply = MethodReturn(message); }
        else if (iface == "org.freedesktop.DBus.Introspectable" && member == "Introspect")
        {
            reply = MethodReturn(message); Iterator iterator = default; InitAppend(reply, ref iterator);
            AppendString(ref iterator, Introspection);
        }
        else if (iface == "org.freedesktop.DBus.Properties" && member is "Get" or "GetAll" or "Set")
        {
            Iterator arguments = default;
            if (InitMessage(message, ref arguments) == 0 || ArgumentType(ref arguments) != 's') { SendError(connection, message, "Invalid arguments."); return; }
            string requested = ReadString(ref arguments);
            if (requested != Player && requested != Root) { SendError(connection, message, "Unknown interface."); return; }
            reply = MethodReturn(message); Iterator output = default; InitAppend(reply, ref output);
            if (member == "GetAll") AppendProperties(ref output, requested);
            else
            {
                if (NextArgument(ref arguments) == 0 || ArgumentType(ref arguments) != 's') { UnrefMessage(reply); SendError(connection, message, "Invalid property."); return; }
                string property = ReadString(ref arguments);
                if (member == "Get")
                {
                    if (!AppendProperty(ref output, requested, property)) { UnrefMessage(reply); SendError(connection, message, "Unknown property."); return; }
                }
                else
                {
                    if (requested != Player || property != "Volume" || NextArgument(ref arguments) == 0 || ArgumentType(ref arguments) != 'v')
                    { UnrefMessage(reply); SendError(connection, message, "Property is not writable."); return; }
                    Iterator value = default; Recurse(ref arguments, ref value);
                    if (ArgumentType(ref value) != 'd') { UnrefMessage(reply); SendError(connection, message, "Invalid volume."); return; }
                    double volume = 0; GetBasic(ref value, &volume);
                    if (!double.IsFinite(volume) || volume is < 0 or > 1) { UnrefMessage(reply); SendError(connection, message, "Invalid volume."); return; }
                    _control("volume", volume);
                }
            }
        }
        else { SendError(connection, message, "Unknown method."); return; }
        if (Send(connection, reply, 0) == 0) { UnrefMessage(reply); throw new InvalidOperationException("MPRIS reply queue failed."); }
        UnrefMessage(reply);
    }

    private void AppendProperties(ref Iterator target, string iface)
    {
        Iterator array = default; OpenContainer(ref target, 'a', "{sv}", ref array);
        foreach (string name in iface == Player
            ? new[] { "PlaybackStatus", "Volume", "Metadata", "CanControl", "CanPlay", "CanPause", "CanGoNext", "CanGoPrevious", "CanSeek" }
            : new[] { "Identity", "CanQuit", "CanRaise", "HasTrackList", "SupportedUriSchemes", "SupportedMimeTypes" })
        {
            Iterator entry = default; OpenContainer(ref array, 'e', null, ref entry); AppendString(ref entry, name);
            AppendProperty(ref entry, iface, name); CloseContainer(ref array, ref entry);
        }
        CloseContainer(ref target, ref array);
    }
    private unsafe bool AppendProperty(ref Iterator target, string iface, string name)
    {
        string signature; object value;
        if (iface == Player)
        {
            (signature, value) = name switch
            {
                "PlaybackStatus" => ("s", (object)PlaybackStatus()),
                "Volume" => ("d", (object)_volume()),
                "Metadata" => ("a{sv}", (object)""),
                "CanControl" or "CanPlay" or "CanPause" or "CanGoNext" => ("b", (object)_enabled()),
                "CanGoPrevious" or "CanSeek" => ("b", (object)false),
                _ => ("", (object)""),
            };
        }
        else
        {
            (signature, value) = name switch
            {
                "Identity" => ("s", (object)"NexaCL"),
                "CanRaise" => ("b", (object)true),
                "CanQuit" or "HasTrackList" => ("b", (object)false),
                "SupportedUriSchemes" or "SupportedMimeTypes" => ("as", (object)Array.Empty<string>()),
                _ => ("", (object)""),
            };
        }
        if (signature.Length == 0) return false;
        Iterator variant = default; OpenContainer(ref target, 'v', signature, ref variant);
        switch (signature)
        {
            case "s": AppendString(ref variant, (string)value); break;
            case "b": int boolean = (bool)value ? 1 : 0; AppendBasic(ref variant, 'b', &boolean); break;
            case "d": double number = (double)value; AppendBasic(ref variant, 'd', &number); break;
            case "as":
                Iterator strings = default; OpenContainer(ref variant, 'a', "s", ref strings);
                foreach (string text in (string[])value) AppendString(ref strings, text); CloseContainer(ref variant, ref strings); break;
            case "a{sv}": Iterator metadata = default; OpenContainer(ref variant, 'a', "{sv}", ref metadata); CloseContainer(ref variant, ref metadata); break;
        }
        CloseContainer(ref target, ref variant); return true;
    }
    private static unsafe string ReadString(ref Iterator iterator) { nint value = 0; GetBasic(ref iterator, &value); return Text(value); }
    private static unsafe void AppendString(ref Iterator iterator, string text)
    {
        nint utf8 = Marshal.StringToCoTaskMemUTF8(text);
        try { AppendBasic(ref iterator, 's', &utf8); } finally { Marshal.FreeCoTaskMem(utf8); }
    }
    private static void SendError(nint connection, nint message, string error)
    {
        nint reply = ErrorReturn(message, "org.freedesktop.DBus.Error.InvalidArgs", error);
        if (Send(connection, reply, 0) == 0) { UnrefMessage(reply); throw new InvalidOperationException("MPRIS error queue failed."); }
        UnrefMessage(reply);
    }
    private static void PublishStatus(nint connection, string status)
    {
        nint message = Signal(PathName, "org.freedesktop.DBus.Properties", "PropertiesChanged");
        try
        {
            Iterator output = default; InitAppend(message, ref output); AppendString(ref output, Player);
            Iterator array = default; OpenContainer(ref output, 'a', "{sv}", ref array);
            Iterator entry = default; OpenContainer(ref array, 'e', null, ref entry); AppendString(ref entry, "PlaybackStatus");
            Iterator variant = default; OpenContainer(ref entry, 'v', "s", ref variant); AppendString(ref variant, status);
            CloseContainer(ref entry, ref variant); CloseContainer(ref array, ref entry); CloseContainer(ref output, ref array);
            Iterator empty = default; OpenContainer(ref output, 'a', "s", ref empty); CloseContainer(ref output, ref empty);
            if (Send(connection, message, 0) == 0) throw new InvalidOperationException("MPRIS signal queue failed.");
        }
        finally { UnrefMessage(message); }
    }
    public async ValueTask DisposeAsync() { await _stop.CancelAsync().ConfigureAwait(false); await _run.ConfigureAwait(false); _stop.Dispose(); }

    private const string Introspection = "<node><interface name='org.mpris.MediaPlayer2'><method name='Raise'/></interface><interface name='org.mpris.MediaPlayer2.Player'><method name='Play'/><method name='Pause'/><method name='PlayPause'/><method name='Stop'/><method name='Next'/><property name='PlaybackStatus' type='s' access='read'/><property name='Volume' type='d' access='readwrite'/><property name='Metadata' type='a{sv}' access='read'/><property name='CanControl' type='b' access='read'/></interface><interface name='org.freedesktop.DBus.Properties'><method name='Get'><arg name='interface' type='s' direction='in'/><arg name='property' type='s' direction='in'/><arg name='value' type='v' direction='out'/></method><method name='GetAll'><arg name='interface' type='s' direction='in'/><arg name='properties' type='a{sv}' direction='out'/></method><method name='Set'><arg name='interface' type='s' direction='in'/><arg name='property' type='s' direction='in'/><arg name='value' type='v' direction='in'/></method></interface></node>";
    [StructLayout(LayoutKind.Sequential, Size = 80)] private struct Iterator { }
    [LibraryImport("libdbus-1.so.3", EntryPoint = "dbus_threads_init_default")] private static partial int InitThreads();
    [LibraryImport("libdbus-1.so.3", EntryPoint = "dbus_bus_get_private")] private static partial nint BusGetPrivate(int type, nint error);
    [LibraryImport("libdbus-1.so.3", EntryPoint = "dbus_connection_set_exit_on_disconnect")] private static partial void SetExitOnDisconnect(nint connection, int exit);
    [LibraryImport("libdbus-1.so.3", EntryPoint = "dbus_bus_request_name", StringMarshalling = StringMarshalling.Utf8)] private static partial int RequestName(nint connection, string name, uint flags, nint error);
    [LibraryImport("libdbus-1.so.3", EntryPoint = "dbus_connection_read_write")] private static partial int ReadWrite(nint connection, int timeout);
    [LibraryImport("libdbus-1.so.3", EntryPoint = "dbus_connection_pop_message")] private static partial nint PopMessage(nint connection);
    [LibraryImport("libdbus-1.so.3", EntryPoint = "dbus_connection_close")] private static partial void CloseConnection(nint connection);
    [LibraryImport("libdbus-1.so.3", EntryPoint = "dbus_connection_unref")] private static partial void UnrefConnection(nint connection);
    [LibraryImport("libdbus-1.so.3", EntryPoint = "dbus_message_unref")] private static partial void UnrefMessage(nint message);
    [LibraryImport("libdbus-1.so.3", EntryPoint = "dbus_message_get_type")] private static partial int MessageType(nint message);
    [LibraryImport("libdbus-1.so.3", EntryPoint = "dbus_message_get_path")] private static partial nint GetPath(nint message);
    [LibraryImport("libdbus-1.so.3", EntryPoint = "dbus_message_get_interface")] private static partial nint GetInterface(nint message);
    [LibraryImport("libdbus-1.so.3", EntryPoint = "dbus_message_get_member")] private static partial nint GetMember(nint message);
    [LibraryImport("libdbus-1.so.3", EntryPoint = "dbus_message_new_method_return")] private static partial nint MethodReturn(nint message);
    [LibraryImport("libdbus-1.so.3", EntryPoint = "dbus_message_new_error", StringMarshalling = StringMarshalling.Utf8)] private static partial nint ErrorReturn(nint message, string name, string error);
    [LibraryImport("libdbus-1.so.3", EntryPoint = "dbus_message_new_signal", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Signal(string path, string iface, string name);
    [LibraryImport("libdbus-1.so.3", EntryPoint = "dbus_connection_send")] private static partial int Send(nint connection, nint message, nint serial);
    [LibraryImport("libdbus-1.so.3", EntryPoint = "dbus_message_iter_init_append")] private static partial void InitAppend(nint message, ref Iterator iterator);
    [LibraryImport("libdbus-1.so.3", EntryPoint = "dbus_message_iter_init")] private static partial int InitMessage(nint message, ref Iterator iterator);
    [LibraryImport("libdbus-1.so.3", EntryPoint = "dbus_message_iter_get_arg_type")] private static partial int ArgumentType(ref Iterator iterator);
    [LibraryImport("libdbus-1.so.3", EntryPoint = "dbus_message_iter_next")] private static partial int NextArgument(ref Iterator iterator);
    [LibraryImport("libdbus-1.so.3", EntryPoint = "dbus_message_iter_recurse")] private static partial void Recurse(ref Iterator iterator, ref Iterator child);
    [LibraryImport("libdbus-1.so.3", EntryPoint = "dbus_message_iter_get_basic")] private static unsafe partial void GetBasic(ref Iterator iterator, void* value);
    [LibraryImport("libdbus-1.so.3", EntryPoint = "dbus_message_iter_append_basic")] private static unsafe partial int AppendBasic(ref Iterator iterator, int type, void* value);
    [LibraryImport("libdbus-1.so.3", EntryPoint = "dbus_message_iter_open_container", StringMarshalling = StringMarshalling.Utf8)] private static partial int OpenContainer(ref Iterator iterator, int type, string? signature, ref Iterator child);
    [LibraryImport("libdbus-1.so.3", EntryPoint = "dbus_message_iter_close_container")] private static partial int CloseContainer(ref Iterator iterator, ref Iterator child);
}
