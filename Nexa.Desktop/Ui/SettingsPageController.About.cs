using Nexa.UI.Next;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    internal Action<Uri>? OpenAboutLink { get; set; }
    internal Func<CancellationToken, Task<bool>>? ExportDiagnostics { get; set; }
    private readonly CancellationTokenSource _diagnosticStop = new();
    private int _diagnosticWriting;
    private void BuildAboutPage()
    {
        DesktopLiteralText.Preserve(_shell.Tree, Text(_sections, Program.ProductDisplayTitle(_shell.Version), 24, Ink, height: 36, weight: 600));
        AboutCopy("版权信息", "Copyright © 2025 muxue。Nexa 项目贡献者保留各自贡献的版权。", "AboutCopyright");
        AboutCopy("开源声明", "Nexa Host 采用 Apache License 2.0。源代码、许可证及贡献记录：\nhttps://github.com/PCL-N-Edition/PCL-N\n随附 LICENSE 为完整授权条款；独立 Sidecar、插件与第三方依赖按各自许可证发布。", "AboutOpenSource");
        if (OpenAboutLink is { } open)
        {
            var links = Stack(_sections, "AboutLinks", XsrUiOrientation.Horizontal, 10);
            ManagementButton(links, "源代码", () => open(new Uri("https://github.com/PCL-N-Edition/PCL-N")), 96);
            ManagementButton(links, "完整许可证", () => open(new Uri("https://github.com/PCL-N-Edition/PCL-N/blob/refactor/xsr/LICENSE")), 128);
            ManagementButton(links, "贡献者", () => open(new Uri("https://github.com/PCL-N-Edition/PCL-N/graphs/contributors")), 96);
        }
        AboutCopy("商标声明", "Minecraft 是 Mojang / Microsoft 的商标。本项目由社区独立维护，与 Mojang / Microsoft 无隶属或官方认可关系。", "AboutTrademarks");
        AboutCopy("鸣谢", "感谢所有代码、翻译、测试和文档贡献者，以及 .NET、Avalonia、SkiaSharp、Bouncy Castle 等开源项目。感谢 Mojang 提供的游戏接口，以及 Modrinth、CurseForge、Fabric、Forge、NeoForge、Quilt 和 LittleSkin 的生态服务。", "AboutThanks");
        AboutCopy("中国大陆下载服务", "感谢 BMCLAPI（bangbang93）、MCIM 与相关镜像维护者。中国大陆优化仅在中国大陆地区策略启用时使用。", "AboutMainland");
        if (ExportDiagnostics is not null)
        {
            AboutCopy("诊断包", "导出版本、系统架构、实例与 Mod 标识、问题分类及结构化操作日志。账户、凭据、完整路径和个人文件不包含在内。诊断包保存在你选择的目录。", "AboutDiagnostics");
            ManagementButton(_sections, "导出诊断包", () => _ = ExportDiagnosticsAsync(), 144);
        }
        var scroll = _shell.Tree.GetComponent<XsrUiScroll>(_sections)!;
        scroll.OffsetY = _scrollPositions.GetValueOrDefault(_selected);
        _shell.Tree.GetComponent<XsrUiTransition>(_sections)!.Key = _selected;
        _shell.Tree.MarkDirty(_sections, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }

    private async Task ExportDiagnosticsAsync()
    {
        if (_disposed || ExportDiagnostics is not { } export || Interlocked.CompareExchange(ref _diagnosticWriting, 1, 0) != 0) return;
        try
        {
            bool saved = await export(_diagnosticStop.Token).ConfigureAwait(false);
            if (!_disposed && saved) _feedback.Info("诊断包已导出到所选目录。");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            if (!_disposed) _feedback.Error("诊断包未导出，请检查保存目录是否可写。");
        }
        finally { Volatile.Write(ref _diagnosticWriting, 0); }
    }

    private void AboutCopy(string title, string content, string key)
    {
        var group = Stack(_sections, key, XsrUiOrientation.Vertical, 8);
        Text(group, title, 18, Ink, height: 28, weight: 600);
        var copy = Text(group, content, 13, Muted, height: key == "AboutOpenSource" ? 120 : 84);
        var text = _shell.Tree.GetComponent<XsrUiText>(copy)!;
        text.MaxLines = 0;
        text.TrimOverflow = false;
        _shell.Tree.GetComponent<XsrUiVisualStyle>(copy)!.WrapText = true;
    }
}
