

using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Settings;

public static class SettingsPolicyContract
{
    public static readonly XsrSemanticId RevisionKey = XsrSemanticId.Parse("settings.policy.revision");
    public static readonly XsrSemanticId CatalogQuery = XsrSemanticId.Parse("settings.catalog.query");
    public static readonly XsrSemanticId EffectiveQuery = XsrSemanticId.Parse("settings.effective.query");
    public static readonly XsrSemanticId PreviewQuery = XsrSemanticId.Parse("settings.policy.preview");
    public static readonly XsrSemanticId SetCommand = XsrSemanticId.Parse("settings.policy.set");
    public static readonly XsrSemanticId BatchCommand = XsrSemanticId.Parse("settings.policy.batch");
    public static readonly XsrSemanticId ExportQuery = XsrSemanticId.Parse("settings.policy.export");
    public static readonly XsrSemanticId ImportPreviewQuery = XsrSemanticId.Parse("settings.policy.import.preview");
    public static readonly XsrSemanticId ImportCommand = XsrSemanticId.Parse("settings.policy.import.apply");
    public static void DeclareState(XsrStateStoreBuilder builder) => builder.Cell<long>(RevisionKey, "Nexa.Services.Settings");
}
