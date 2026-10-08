using System.Collections.ObjectModel;
using Nexa.Core;
using Nexa.Services.Logging;

namespace Nexa.Services.Minecraft.Launch;

/// <summary>Retains only bounded, redacted executor observations; never prepares or starts a launch.</summary>
public sealed class MinecraftLaunchPlanDiagnostics
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MinecraftLaunchPlanDiagnosticSnapshot> _items = new(PathIdentity.Comparer);
    private readonly Queue<string> _order = [];

    public void Capture(MinecraftLaunchPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string instance = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.InstanceDirectory));
        int remaining = 128 * 1024; bool truncated = false;
        string Keep(string text)
        {
            string value = LogRedactor.Redact(text);
            int length = Math.Min(Math.Min(value.Length, 4096), remaining);
            if (length != value.Length) truncated = true;
            remaining -= length; return value[..length];
        }
        ReadOnlyCollection<string> List(IEnumerable<string> values)
        {
            List<string> result = [];
            foreach (string value in values)
            {
                if (result.Count == 2048 || remaining <= 0) { truncated = true; break; }
                result.Add(Keep(value));
            }
            return result.AsReadOnly();
        }
        var arguments = new List<string>(); bool maskNext = false;
        foreach (string argument in plan.Arguments)
        {
            if (arguments.Count == 2048 || remaining <= 0) { truncated = true; break; }
            bool sensitive = SensitiveArgument(argument);
            string value = maskNext ? "<redacted>" : sensitive && argument.Contains('=')
                ? argument[..(argument.IndexOf('=') + 1)] + "<redacted>" : argument;
            arguments.Add(Keep(value)); maskNext = sensitive && !argument.Contains('=');
        }
        string main = plan.MainClassIndex is { } index && index >= 0 && index < plan.Arguments.Count
            ? Keep(plan.Arguments[index]) : "未提供 main-class 边界";
        var snapshot = new MinecraftLaunchPlanDiagnosticSnapshot(instance, DateTimeOffset.UtcNow,
            Keep(plan.JavaExecutablePath), Keep(plan.WorkingDirectory), main, plan.ModLoader.Kind.ToString(),
            plan.HeapLimitMiB, plan.Overlay.SafeLaunch, arguments.AsReadOnly(), List(plan.ClasspathEntries),
            List(plan.Libraries.Where(library => library.IsNatives).Select(library => library.LocalPath)),
            List(plan.EnvironmentVariables.Keys), truncated);
        lock (_gate)
        {
            if (!_items.ContainsKey(instance))
            {
                while (_items.Count >= 16) _items.Remove(_order.Dequeue());
                _order.Enqueue(instance);
            }
            _items[instance] = snapshot;
        }
    }

    public MinecraftLaunchPlanDiagnosticSnapshot Read(MinecraftLaunchPlanDiagnosticQuery query)
    {
        if (!Path.IsPathFullyQualified(query.InstanceDirectory)) throw new ArgumentException("实例必须使用完整路径。", nameof(query));
        string instance = Path.TrimEndingDirectorySeparator(Path.GetFullPath(query.InstanceDirectory));
        lock (_gate)
            return _items.TryGetValue(instance, out var snapshot) ? snapshot
                : new(instance, null, "", "", "", "", -1, false, [], [], [], [], false);
    }

    private static bool SensitiveArgument(string value)
    {
        if (!value.StartsWith('-')) return false;
        string key = value.Split('=', 2)[0];
        return key.Contains("token", StringComparison.OrdinalIgnoreCase) || key.Contains("password", StringComparison.OrdinalIgnoreCase)
            || key.Contains("apikey", StringComparison.OrdinalIgnoreCase) || key.Contains("api_key", StringComparison.OrdinalIgnoreCase)
            || key.Contains("api-key", StringComparison.OrdinalIgnoreCase) || key.Contains("accesskey", StringComparison.OrdinalIgnoreCase)
            || key.Contains("credential", StringComparison.OrdinalIgnoreCase) || key.Contains("cookie", StringComparison.OrdinalIgnoreCase)
            || key.Contains("passwd", StringComparison.OrdinalIgnoreCase) || key.Contains("privatekey", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("-javaagent:", StringComparison.OrdinalIgnoreCase)
            || key.Contains("user.name", StringComparison.OrdinalIgnoreCase) || key.Contains("user.email", StringComparison.OrdinalIgnoreCase)
            || key.Contains("secret", StringComparison.OrdinalIgnoreCase) || key.Contains("session", StringComparison.OrdinalIgnoreCase)
            || key.Contains("authlibinjector", StringComparison.OrdinalIgnoreCase) || key.Contains("authorization", StringComparison.OrdinalIgnoreCase)
            || key.Contains("username", StringComparison.OrdinalIgnoreCase) || key.Contains("uuid", StringComparison.OrdinalIgnoreCase)
            || key.Contains("xuid", StringComparison.OrdinalIgnoreCase) || key.Contains("clientid", StringComparison.OrdinalIgnoreCase);
    }
}
