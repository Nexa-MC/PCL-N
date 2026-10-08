using System.Globalization;
using Nexa.Services.Logging;
using Nexa.UI.Next;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private DesktopContentWorkspaceRoutes? _diagnosticAi;
    private bool? _diagnosticAiWorkspaceEnabled;
    private Guid _diagnosticAiResultDialog;
    private sealed record DiagnosticAiConfirmation(DiagnosticAiRequest Request, DiagnosticRawPreview Preview);
    private readonly Dictionary<string, XsrUiEntityId> _diagnosticAiInputs = [];
    private readonly Dictionary<string, string> _diagnosticAiDraft = new(StringComparer.Ordinal)
    { ["endpoint"] = "", ["model"] = "", ["key"] = "", ["inputBudget"] = "32768", ["outputBudget"] = "1024" };

    internal void ConfigureDiagnosticAi(bool enabled) => _diagnosticAi = enabled ? new(_queries, _commands) : null;

    private bool DiagnosticAiEnabled => _values?.Values.FirstOrDefault(x => x.Key == "diagnostics.ai.enabled") is { ValidationError: null, Value.Value: "true" };

    private void BuildDiagnosticAiWorkspace()
    {
        _diagnosticAiInputs.Clear(); if (_diagnosticAi is null) return;
        _diagnosticAiWorkspaceEnabled = DiagnosticAiEnabled;
        var group = FormGroup(_sections, "DiagnosticAiWorkspace", "AI 诊断建议");
        foreach (string id in new[] { "global.privacy.f07ee7c87d0e", "global.privacy.c1b151abf947" })
            if (_catalog?.Entries.FirstOrDefault(x => x.Id == id) is { } entry) BuildRow(group, entry);
        if (!DiagnosticAiEnabled) { Text(group, "AI 诊断已停用。启用后仍需逐次确认数据预览和发送范围。", 11, Muted, 42); return; }
        Text(group, "仅在确认发送范围和预览后请求指定服务；API 密钥只保留在当前页面会话。建议不会自动执行命令。", 11, Muted, 42);
        foreach (var (key, label, maximum) in new[] { ("endpoint", "服务 HTTPS 地址", 2048), ("model", "模型", 128), ("key", "API 密钥", 4096), ("inputBudget", "输入字节预算", 6), ("outputBudget", "输出 Token 预算", 4) })
        {
            var input = ManagementField(group, "DiagnosticAi." + key, label, _diagnosticAiDraft[key]);
            _shell.Tree.SetComponent(input, new XsrUiTextInput { MaximumLength = maximum, IsPassword = key == "key", Placeholder = label });
            _shell.Renderer.SetTextInputValue(input, _diagnosticAiDraft[key]);
            _diagnosticAiInputs[key] = input;
        }
        var actions = Stack(group, "DiagnosticAi.Actions", XsrUiOrientation.Horizontal, 8);
        WorkspaceButton(actions, "AiFacts", "预览结构化诊断", "ai-facts");
        WorkspaceButton(actions, "AiRaw", "预览脱敏文本", "ai-raw");
        WorkspaceButton(actions, "AiRepair", "预览存储修复", "prune");
    }

    private void CaptureDiagnosticAiDraft()
    {
        foreach (var (key, entity) in _diagnosticAiInputs)
            if (_shell.Tree.IsAlive(entity) && _shell.Tree.GetComponent<XsrUiTextInput>(entity) is { } input)
                _diagnosticAiDraft[key] = input.ReadDraft();
    }

    private DiagnosticAiRequest? ReadDiagnosticAiRequest(string kind)
    {
        if (!DiagnosticAiEnabled) { _feedback.Error("请先启用 AI 诊断。"); return null; }
        if (!Uri.TryCreate(_diagnosticAiDraft["endpoint"], UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps
            || !int.TryParse(_diagnosticAiDraft["inputBudget"], out int input) || input is < 1 or > 128 * 1024
            || !int.TryParse(_diagnosticAiDraft["outputBudget"], out int output) || output is < 1 or > 4096
            || string.IsNullOrWhiteSpace(_diagnosticAiDraft["model"]))
        { _feedback.Error("请填写 HTTPS 服务地址、模型和有效请求预算。"); return null; }
        string? reasoning = _values?.Values.FirstOrDefault(x => x.Key == "diagnostics.ai.reasoning" && x.ValidationError is null)?.Value.Value;
        if (reasoning is not ("provider" or "low" or "medium" or "high")) { _feedback.Error("AI 推理偏好无效，请重新选择。"); return null; }
        return new(endpoint, _diagnosticAiDraft["model"], _diagnosticAiDraft["key"],
            kind == "ai-raw" ? DiagnosticAiDataScope.RedactedRawText : DiagnosticAiDataScope.StructuredFacts, "", input, output)
        { Reasoning = reasoning switch { "low" => DiagnosticAiReasoning.Low, "medium" => DiagnosticAiReasoning.Medium, "high" => DiagnosticAiReasoning.High, _ => DiagnosticAiReasoning.Provider } };
    }

    private async Task<ContentWorkspaceResult> PreviewDiagnosticAiAsync(DiagnosticAiRequest request, string? raw, CancellationToken token)
    {
        var preview = request.DataScope == DiagnosticAiDataScope.RedactedRawText ? await _contentBackups!.PreviewRawAsync(raw ?? "", token).ConfigureAwait(false)
            : await _contentBackups!.PreviewFactsAsync(await _diagnosticHistory!.ReadAsync(token).ConfigureAwait(false), token).ConfigureAwait(false);
        if (preview.Utf8Bytes > request.MaximumInputBytes) throw new IOException("诊断预览超过输入预算，请缩小内容或提高明确预算。");
        request = request with { PreviewRevision = preview.Revision };
        return new(AiConfirmation: new(request, preview), Apply: async cancellation =>
            {
                var suggestion = await _diagnosticAi!.SuggestAsync(request, preview, request.DataScope, cancellation).ConfigureAwait(false);
                return new(AiSuggestion: suggestion);
            });
    }

    // Called only by the UI-frame dispatcher; worker code never invokes the renderer localizer.
    private string FormatDiagnosticAiConfirmation(DiagnosticAiConfirmation confirmation)
    {
        var request = confirmation.Request; var preview = confirmation.Preview;
        string scope = _shell.Renderer.LocalizeText(request.DataScope == DiagnosticAiDataScope.RedactedRawText
            ? "脱敏原始日志文本" : "结构化操作事实（不含消息正文）");
        string format = _shell.Renderer.LocalizeText("将向 {0} 发送 {1}，模型 {2}，推理参数 {3}。\n输入 {4} 字节，输出最多 {5} Token。\n请检查内容后确认：\n");
        return string.Format(CultureInfo.CurrentCulture, format, request.Endpoint, scope, request.Model,
            request.Reasoning, preview.Utf8Bytes, request.MaximumOutputTokens) + preview.RedactedText;
    }

    private void PresentDiagnosticAiSuggestion(DiagnosticAiSuggestion suggestion)
    {
        string message = _shell.Renderer.LocalizeText("AI 建议（未执行修复）：\n") + suggestion.Text;
        _contentWorkspaceMessage = message;
        _diagnosticAiResultDialog = _feedback.ShowMessageDialog("settings.diagnostic-ai-result", "AI 建议", message, "知道了", localizeMessage: false);
    }
}
