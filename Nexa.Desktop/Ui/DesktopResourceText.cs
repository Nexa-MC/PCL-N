using System.Globalization;
using Nexa.Services.Resources;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

/// <summary>Provider text stays literal; enrichment follows interface language, not country or formatting.</summary>
internal static class DesktopResourceText
{
    private static readonly XsrSemanticId LanguageKey = XsrSemanticId.Parse("UiLanguage");
    internal static string Language(XsrStateStore state) => UiLocalizationCatalog.ResolveLanguage(
        state.TryResolve(LanguageKey, out var key) ? state.Read<string>(key).Value ?? "auto" : "auto",
        CultureInfo.CurrentUICulture.Name);
    internal static bool UsesChinese(XsrStateStore state) => Language(state).StartsWith("zh", StringComparison.Ordinal);
    internal static string Name(ResourceProject project, XsrStateStore state) => UsesChinese(state) ? project.DisplayName : project.Title;
    internal static string Description(ResourceProject project, XsrStateStore state) => UsesChinese(state) ? project.DisplayDescription : project.Description;
}
