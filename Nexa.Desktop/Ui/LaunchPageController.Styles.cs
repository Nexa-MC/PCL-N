








using Nexa.UI.Next;




namespace Nexa.Desktop.Ui;

internal sealed partial class LaunchPageController
{

    /// <summary>
    /// Applies the legacy experimental launch-home styling that the PXML control vocabulary
    /// cannot express: card surfaces, section typography, the badge, the picker row, and the
    /// accent launch button. PXML keys name internal handles; semantic labels remain human text.
    /// </summary>
    private void StyleLaunchPage(XsrUiEntityId page, Dictionary<string, XsrUiEntityId> entities)
    {
        StyleCard(entities, "CardAccount", cornerRadius: XsrUiCornerRadii.Surface);
        StyleCard(entities, "CardVersion", cornerRadius: XsrUiCornerRadii.Surface);
        StyleCard(entities, "CardAbout", cornerRadius: XsrUiCornerRadii.Surface);
        StyleText(entities, "AccountHeader", PrimaryText, fontSize: 18, weight: 600);
        StyleText(entities, "VersionHeader", SecondaryText, fontSize: 12, weight: 600);
        StyleText(entities, "AboutTitle", SecondaryText, fontSize: 12, weight: 600);
        StyleText(entities, "TriviaTitle", SecondaryText, fontSize: 12, weight: 600);
        StyleText(entities, "EchoTitle", SecondaryText, fontSize: 12, weight: 600);
        StyleText(entities, "AboutMessage", PrimaryText, fontSize: 14, weight: 600);
        StyleText(entities, "TriviaMessage", PrimaryText, fontSize: 14, weight: 600);
        StyleText(entities, "EchoMessage", PrimaryText, fontSize: 14, weight: 600);
        foreach (string key in new[] { "AboutMessage", "TriviaMessage", "EchoMessage" })
            _shell.Tree.GetComponent<XsrUiVisualStyle>(entities[key])!.WrapText = true;
        foreach (string key in new[] { "WidgetAboutIndicator", "WidgetTriviaIndicator", "WidgetEchoIndicator" })
            ApplyVisual(entities[key], XsrUiColor.Transparent, PrimaryText, cornerRadius: 3, hover: BadgeBackground);
        foreach (string key in new[] { "WidgetAboutDot", "WidgetTriviaDot", "WidgetEchoDot" })
            ApplyVisual(entities[key], BadgeText, PrimaryText, cornerRadius: 3);
        StyleText(entities, "AccountKind", ProfileSecondaryText, fontSize: 13);
        StyleText(entities, "AccountHint", ProfileSecondaryText, fontSize: 12);
        _shell.Tree.GetComponent<XsrUiVisualStyle>(entities["AccountHint"])!.WrapText = true;
        ApplyVisual(entities["AccountAvatarSurface"], ProfileSurface, BadgeText, XsrUiCornerRadii.Surface);
        ApplyVisual(entities["AccountBack"], ProfileSurface, ProfileSecondaryText, XsrUiCornerRadii.Pill(32), hover: BadgeBackground);
        ApplyVisual(entities["AccountAdd"], ProfileSurface, BadgeText, XsrUiCornerRadii.Pill(32),
            hover: BadgeBackground, hoverExpand: true);
        StyleText(entities, "AccountAdd", BadgeText, 13, 600);
        AlignText(entities, "AccountAdd", XsrUiTextAlignment.Center);
        ApplyVisual(entities["AccountImport"], ProfileSurface, BadgeText, XsrUiCornerRadii.Pill(34), hover: BadgeBackground);
        StyleText(entities, "AccountImport", BadgeText, 13, 600);
        AlignText(entities, "AccountImport", XsrUiTextAlignment.Center);
        StyleText(entities, "AccountAvatar", BadgeText, fontSize: 14);
        foreach (string key in new[] { "AccountName", "AccountKind" })
            AlignText(entities, key, XsrUiTextAlignment.Center);
        foreach (string key in new[] { "AccountSwitch", "AccountWardrobe" })
        {
            ApplyVisual(entities[key], DesktopUiPalette.CapsuleBackground, DesktopUiPalette.CapsuleForeground, XsrUiCornerRadii.Pill(36), hover: DesktopUiPalette.CapsuleHover, hoverExpand: true);
            StyleText(entities, key, BadgeText, 13, 600);
        }
        StyleText(entities, "VersionAction", SecondaryText, fontSize: 11);
        if (entities.TryGetValue("AccountName", out XsrUiEntityId accountName))
        {
            StyleText(accountName, PrimaryText, fontSize: 22, weight: 600);
        }

        if (entities.TryGetValue("VersionName", out XsrUiEntityId versionName))
        {
            StyleText(versionName, PrimaryText, fontSize: 20, weight: 600);
            DesktopLiteralText.Preserve(_shell.Tree, versionName);
        }

        if (entities.TryGetValue("InstanceRow", out XsrUiEntityId pickerRow))
        {
            ApplyVisual(pickerRow, PickerBackground, PrimaryText, cornerRadius: XsrUiCornerRadii.Inset);
        }

        if (entities.TryGetValue("InstanceListButton", out XsrUiEntityId instanceListButton))
        {
            // Hover-expanding capsule: at rest an icon circle pinned to the right edge; on
            // hover the pill grows leftward and the function name fades in beside the icon.
            ApplyVisual(
                instanceListButton,
                PickerBackground,
                PrimaryText,
                cornerRadius: XsrUiCornerRadii.Pill(36),
                border: CardBorder,
                hoverExpand: true);
            StyleText(instanceListButton, PrimaryText, fontSize: 13, weight: 600);
            AlignText(instanceListButton, XsrUiTextAlignment.Center);
        }

        foreach (string key in new[] { "InstanceSettings", "InstanceModify" })
        {
            XsrUiEntityId action = entities[key];
            ApplyVisual(
                action,
                PickerBackground,
                PrimaryText,
                cornerRadius: XsrUiCornerRadii.Pill(36),
                border: CardBorder,
                hoverExpand: true);
            StyleText(action, PrimaryText, fontSize: 13, weight: 600);
            AlignText(action, XsrUiTextAlignment.Center);
        }


        if (entities.TryGetValue("LaunchButton", out XsrUiEntityId button))
        {
            // Legacy accent button: normal #0b5bcb, hover #1370f3, white 13 px semibold label.
            ApplyVisual(
                button,
                LaunchButtonBackground,
                new XsrUiColor(255, 255, 255),
                cornerRadius: XsrUiCornerRadii.Pill(44),
                hover: LaunchButtonHover);
            StyleText(button, new XsrUiColor(255, 255, 255), fontSize: 13, weight: 600);
            AlignText(button, XsrUiTextAlignment.Center);
        }
    }


    private void StyleCard(
        Dictionary<string, XsrUiEntityId> entities,
        string label,
        double cornerRadius)
    {
        if (entities.TryGetValue(label, out XsrUiEntityId card))
        {
            ApplyVisual(card, CardBackground, PrimaryText, cornerRadius, border: CardBorder);
        }
    }


    private void StyleText(Dictionary<string, XsrUiEntityId> entities, string label, XsrUiColor foreground, double fontSize, double weight = 400)
    {
        if (entities.TryGetValue(label, out XsrUiEntityId entity))
        {
            StyleText(entity, foreground, fontSize, weight);
        }
    }


    private void StyleText(XsrUiEntityId entity, XsrUiColor foreground, double fontSize, double weight = 400)
    {
        XsrUiVisualStyle visual = RequireVisual(entity);
        visual.Foreground = foreground;
        visual.FontSize = fontSize;
        visual.FontWeight = weight;
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint | XsrUiDirtyKinds.Layout);
    }


    private void AlignText(Dictionary<string, XsrUiEntityId> entities, string key, XsrUiTextAlignment alignment)
    {
        if (entities.TryGetValue(key, out XsrUiEntityId entity))
        {
            AlignText(entity, alignment);
        }
    }


    private void AlignText(XsrUiEntityId entity, XsrUiTextAlignment alignment)
    {
        RequireVisual(entity).TextAlignment = alignment;
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
    }


    private void ApplyVisual(
        XsrUiEntityId entity,
        XsrUiColor background,
        XsrUiColor foreground,
        double cornerRadius,
        XsrUiColor? border = null,
        XsrUiColor? hover = null,
        bool hoverExpand = false)
    {
        XsrUiVisualStyle visual = RequireVisual(entity);
        visual.Background = background;
        visual.Foreground = foreground;
        visual.Border = border ?? XsrUiColor.Transparent;
        visual.BorderWidth = border is null ? 0 : 1;
        visual.Hover = hover ?? XsrUiColor.Transparent;
        visual.HoverExpand = hoverExpand;
        visual.Surface = XsrUiSurfaceKind.Solid;
        visual.CornerRadius = cornerRadius;
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
    }


    private XsrUiVisualStyle RequireVisual(XsrUiEntityId entity)
    {
        XsrUiVisualStyle? visual = _shell.Tree.GetComponent<XsrUiVisualStyle>(entity);
        if (visual is null)
        {
            visual = new XsrUiVisualStyle();
            _shell.Tree.SetComponent(entity, visual);
        }

        return visual;
    }


    private XsrUiEntityId BuildPlaceholderPage() => LoadVersionSubpage("placeholder-page", "此功能");


    private static string ReadEmbeddedResource(string suffix)
    {
        System.Reflection.Assembly assembly = typeof(LaunchPageController).Assembly;
        string resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(suffix, StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The embedded resource '{resourceName}' is missing.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

}
