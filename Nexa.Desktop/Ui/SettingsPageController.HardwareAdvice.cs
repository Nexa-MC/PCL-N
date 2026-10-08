using System.Globalization;
using Nexa.Services.Capabilities;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private MachineCapabilitySnapshot? _hardwareAdviceSource;
    private Task<XsrResult<CapabilityPreflightReport>>? _hardwareAdviceRead;
    private CancellationTokenSource? _hardwareAdviceStop;
    private CapabilityPreflightReport? _hardwareAdviceReport;
    private void RetireHardwareAdvice()
    {
        _hardwareAdviceStop?.Cancel(); _hardwareAdviceStop?.Dispose(); _hardwareAdviceStop = null;
        _hardwareAdviceRead = null; _hardwareAdviceReport = null; _hardwareAdviceSource = null;
    }
    private void UpdateHardwareAdvice()
    {
        if (!_visible || _selected != "platform") { RetireHardwareAdvice(); return; }
        if (_hardwareAdviceRead is not { IsCompleted: true } read) return;
        _hardwareAdviceRead = null; _hardwareAdviceStop?.Dispose(); _hardwareAdviceStop = null;
        if (ReferenceEquals(_hardwareAdviceSource, _machine) && PendingQuery.Succeeded(read))
            _hardwareAdviceReport = read.Result.Value;
        BuildSections();
    }
    private void BuildHardwareAdvice()
    {
        if (_machine is not { } source || !_machineScope.HasInstanceScope) { RetireHardwareAdvice(); return; }
        if (!ReferenceEquals(_hardwareAdviceSource, source))
        {
            RetireHardwareAdvice(); _hardwareAdviceSource = source;
            if (_queries.TryResolve(MachineCapabilityStateContract.PreflightQuery, out var route))
            {
                _hardwareAdviceStop = new();
                _hardwareAdviceRead = _queries.QueryAsync<LaunchPreflightQuery, CapabilityPreflightReport>(route,
                    new(source), cancellationToken: _hardwareAdviceStop.Token).AsTask();
                WakeOnPlatformCompletion(_hardwareAdviceRead);
            }
        }
        var facts = CaptureHardwareAdvice(source, _hardwareAdviceReport, _shell.Renderer.LocalizeText);
        Text(_sections, "硬件与资源建议", 12, Muted, 24, 600);
        Text(_sections, "建议只根据本次捕获的估算与预检，不会自动修改游戏配置；未知输入仍需启动后验证。", 11, Muted, 42);
        Card("Shader", "光影", facts.Shader,
            facts.ResourceRisk ? "先降低光影负载或停用光影，再刷新预检比较资源预算。" : "新增光影后请刷新预检；模型预算不能证明实际 GPU 使用。");
        Card("ResourcePack", "资源包", facts.ResourcePacks,
            facts.ResourceRisk ? "先减少启用的资源包或降低纹理分辨率，再比较新的加载峰值。" : "增加资源包后请比较加载峰值与可用内存预算。");
        Card("RenderDistance", "渲染距离", facts.RenderDistance,
            facts.ResourceRisk ? "降低游戏中的渲染距离后刷新预检，观察资源预算变化。" : "当前距离来自已读取的游戏设置，实际帧率仍需游戏内验证。");
        Card("Risk", "风险", facts.Risk, "这里只显示当前预检分类；时间关联、估算和未知读数不能证明崩溃原因。");
        void Card(string key, string title, string value, string advice)
        {
            var card = SettingsCard("HardwareAdvice." + key, new(16, 8, 16, 8), 4);
            Text(card, title, 13, Ink, 26, 600);
            DiagnosticText(card, "HardwareAdvice." + key + ".Facts", value, 12, Ink, 42);
            Text(card, advice, 11, Muted, 42);
        }
    }

    internal sealed record HardwareAdviceSnapshot(string Shader, string ResourcePacks, string RenderDistance, string Risk, bool ResourceRisk);
    internal static HardwareAdviceSnapshot CaptureHardwareAdvice(MachineCapabilitySnapshot source, CapabilityPreflightReport? report,
        Func<string, string>? localize = null)
    {
        string L(string text) => localize?.Invoke(text) ?? text;
        string Estimate(string id) => source.Get<long>(id) is { Availability: CapabilityAvailability.Available, Value: >= 0 } value
            ? value.Value.ToString(CultureInfo.InvariantCulture) + " MiB（" + L("估算") + " · " + value.Confidence + "）" : L("不可用");
        bool resourceRisk = report?.CollapsedIssues.Any(issue => (issue.Category is "memory" or "gpu" or "resource")
            && issue.Severity >= PreflightSeverity.Warning) == true;
        string shaderInput = source.Get<long>("resource.shader.estimated_gpu_memory") is { Availability: CapabilityAvailability.Available }
            ? L("已捕获光影估算输入") : L("光影输入未知，零模型值不代表未启用光影");
        string shader = L("模型光影预算") + ": " + Estimate("estimate.graphics.shader") + " · " + shaderInput;
        string packs = source.Get<IReadOnlyList<string>>("minecraft.settings.resource_packs") is { Availability: CapabilityAvailability.Available, Value: { } enabled }
            ? enabled.Count.ToString(CultureInfo.InvariantCulture) + " " + L("个已启用资源包") : L("启用资源包未知");
        packs += " · " + L("加载峰值") + ": " + Estimate("estimate.resource.load_peak");
        string distance = source.Get<int>("minecraft.settings.render_distance") is { Availability: CapabilityAvailability.Available, Value: >= 0 } current
            ? current.Value.ToString(CultureInfo.InvariantCulture) + " chunks（" + L("已读取") + "）" : L("当前渲染距离不可用");
        string risk = report is null ? L("当前预检尚未取得，风险未知") : report.OverallSeverity == PreflightSeverity.None
            ? L("本次预检没有已识别风险；未知输入不代表通过") : L(SeverityLabel(report.OverallSeverity)) + " · "
                + string.Join(" · ", report.CollapsedIssues.Take(8).Select(issue => issue.Code)
                    .Where(code => code.Length <= 128 && code.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-')));
        return new(shader, packs, distance, risk, resourceRisk);
    }
}
