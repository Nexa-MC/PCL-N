using System.Runtime.InteropServices;
using System.Text;

namespace Nexa.Desktop;

internal readonly record struct DesktopJumpTask(string Title, string Executable, string Arguments, string WorkingDirectory);

/// <summary>Owns only NexaCL's Windows task list through typed NativeAOT-compatible COM calls.</summary>
internal static unsafe class DesktopJumpList
{
    internal const string ApplicationId = "NexaCL";
    private static readonly Guid DestinationClass = new("77f10cf0-3db5-4966-b520-b7c54fd35ed6");
    private static readonly Guid DestinationInterface = new("6332debf-87b5-4670-90c0-5e57b408a49e");
    private static readonly Guid CollectionClass = new("2d3468c1-36a7-43b6-ac24-d3f02fd9607a");
    private static readonly Guid CollectionInterface = new("5632b1a4-e38a-400a-928a-d4cd63230295");
    private static readonly Guid ArrayInterface = new("92ca9dcd-5622-4bba-a805-5e9f541bd8c9");
    private static readonly Guid LinkClass = new("00021401-0000-0000-c000-000000000046");
    private static readonly Guid LinkInterface = new("000214f9-0000-0000-c000-000000000046");
    private static readonly Guid StoreInterface = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
    private static readonly PropertyKey TitleKey = new(new("f29f85e0-4ff9-1068-ab91-08002b27b3d9"), 2);

    internal static Task<string?> ApplyAsync(bool enabled, CancellationToken cancellationToken = default)
        => ApplyAsync(enabled, null, cancellationToken);

    internal static Task<string?> ApplyAsync(bool enabled, IReadOnlyList<string>? captions, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
            return Task.FromResult<string?>("当前平台不支持 Windows 跳转列表。");
        IReadOnlyList<DesktopJumpTask> tasks;
        try { tasks = CreateTasks(DesktopProtocolRegistration.LauncherCommand(), captions); }
        catch (Exception error) when (error is IOException or ArgumentException)
        { return Task.FromResult<string?>("无法创建启动器跳转列表：" + error.Message); }
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            int initialized = CoInitializeEx(0, 2);
            if (initialized < 0) { completion.TrySetResult("无法初始化 Windows 跳转列表。"); return; }
            string? failure;
            try { cancellationToken.ThrowIfCancellationRequested(); Apply(enabled, tasks, cancellationToken); failure = null; }
            catch (Exception error) when (error is COMException or InvalidOperationException or OperationCanceledException
                or DllNotFoundException or EntryPointNotFoundException)
            { failure = error is OperationCanceledException ? "跳转列表设置已取消。" : "Windows 跳转列表设置失败：" + error.Message; }
            finally { CoUninitialize(); }
            completion.TrySetResult(failure);
        })
        { Name = "Nexa Jump List" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    internal static IReadOnlyList<DesktopJumpTask> CreateTasks(IReadOnlyList<string> command, IReadOnlyList<string>? captions = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Count is < 1 or > 2 || command.Any(static part => string.IsNullOrWhiteSpace(part)
            || part.Length > 8192 || part.Any(char.IsControl) || !Path.IsPathFullyQualified(part)))
            throw new ArgumentException("启动器路径必须是受支持的绝对本地路径。", nameof(command));
        if (captions is not null && (captions.Count != 4 || captions.Any(static caption => string.IsNullOrWhiteSpace(caption)
            || caption.Length > 128 || caption.Any(char.IsControl)))) throw new ArgumentException("跳转列表需要四个有效菜单名称。", nameof(captions));
        string executable = command[0];
        string directory = Path.GetDirectoryName(executable) ?? throw new ArgumentException("启动器工作目录不可用。", nameof(command));
        return Array.AsReadOnly(new[]
        {
            Task(captions?[0] ?? "启动", "launch"), Task(captions?[1] ?? "安装", "install"),
            Task(captions?[2] ?? "资源", "resources"), Task(captions?[3] ?? "设置", "settings"),
        });
        DesktopJumpTask Task(string title, string route) => new(title, executable,
            string.Join(' ', command.Skip(1).Append("nexacl://" + route).Select(QuoteArgument)), directory);
    }

    internal static string QuoteArgument(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 8192 || value.Any(char.IsControl)) throw new ArgumentException("跳转列表参数不合法。", nameof(value));
        StringBuilder text = new("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            text.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            text.Append(character); slashes = 0;
        }
        text.Append('\\', slashes * 2); text.Append('"');
        return text.ToString();
    }

    private static void Apply(bool enabled, IReadOnlyList<DesktopJumpTask> tasks, CancellationToken token)
    {
        nint destination = Create(DestinationClass, DestinationInterface), collection = 0, removed = 0;
        bool begun = false, committed = false;
        try
        {
            Check(SetCurrentProcessExplicitAppUserModelID(ApplicationId));
            SetString(destination, 3, ApplicationId);
            if (!enabled) { SetString(destination, 10, ApplicationId); return; }
            uint slots;
            Guid arrayId = ArrayInterface;
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, Guid*, nint*, int>)Method(destination, 4))
                (destination, &slots, &arrayId, &removed));
            begun = true;
            collection = Create(CollectionClass, CollectionInterface);
            foreach (DesktopJumpTask task in tasks)
            {
                token.ThrowIfCancellationRequested();
                nint link = Create(LinkClass, LinkInterface), store = 0;
                try
                {
                    SetString(link, 20, task.Executable); SetString(link, 11, task.Arguments);
                    SetString(link, 9, task.WorkingDirectory); SetString(link, 7, task.Title);
                    Guid propertyId = StoreInterface;
                    Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Method(link, 0))(link, &propertyId, &store));
                    PropertyKey title = TitleKey;
                    fixed (char* label = task.Title)
                    {
                        PropVariant value = new() { ValueType = 31, Pointer = (nint)label };
                        Check(((delegate* unmanaged[Stdcall]<nint, PropertyKey*, PropVariant*, int>)Method(store, 6))(store, &title, &value));
                    }
                    Check(((delegate* unmanaged[Stdcall]<nint, int>)Method(store, 7))(store));
                    Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Method(collection, 5))(collection, link));
                }
                finally { Release(store); Release(link); }
            }
            token.ThrowIfCancellationRequested();
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Method(destination, 7))(destination, collection));
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Method(destination, 8))(destination));
            committed = true;
        }
        finally
        {
            if (begun && !committed) _ = ((delegate* unmanaged[Stdcall]<nint, int>)Method(destination, 11))(destination);
            Release(removed); Release(collection); Release(destination);
        }
    }

    private static nint Create(Guid classId, Guid interfaceId)
    { Check(CoCreateInstance(ref classId, 0, 1, ref interfaceId, out nint value)); if (value == 0) throw new InvalidOperationException("Native COM returned no interface."); return value; }
    private static void SetString(nint value, int slot, string text)
    { fixed (char* pointer = text) Check(((delegate* unmanaged[Stdcall]<nint, char*, int>)Method(value, slot))(value, pointer)); }
    private static nint Method(nint value, int slot) => (*(nint**)value)[slot];
    private static void Release(nint value)
    { if (value != 0) _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Method(value, 2))(value); }
    private static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    { public readonly Guid FormatId = formatId; public readonly uint PropertyId = propertyId; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    { [FieldOffset(0)] public ushort ValueType; [FieldOffset(8)] public nint Pointer; }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid classId, nint outer, uint context, ref Guid interfaceId, out nint value);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
