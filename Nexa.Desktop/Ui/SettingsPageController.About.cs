using Nexa.UI.Next;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    internal Action<Uri>? OpenAboutLink { get; set; }
    private void BuildAboutPage()
    {
        Text(_sections, "关于 Nexa", 24, Ink, height: 36, weight: 600);
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
        var scroll = _shell.Tree.GetComponent<XsrUiScroll>(_sections)!;
        scroll.OffsetY = _scrollPositions.GetValueOrDefault(_selected);
        _shell.Tree.GetComponent<XsrUiTransition>(_sections)!.Key = _selected;
        _shell.Tree.MarkDirty(_sections, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }

    private void AboutCopy(string title, string content, string key)
    {
        var group = Stack(_sections, key, XsrUiOrientation.Vertical, 8);
        Text(group, title, 18, Ink, height: 28, weight: 600);
        var copy = Text(group, content, 13, Muted, height: key == "AboutOpenSource" ? 120 : 84);
        _shell.Tree.GetComponent<XsrUiVisualStyle>(copy)!.WrapText = true;
    }
}
