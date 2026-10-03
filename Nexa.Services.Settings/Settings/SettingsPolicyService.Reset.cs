using System.Text.Json.Nodes;
using Nexa.Xsr;

namespace Nexa.Services.Settings;

public sealed partial class SettingsPolicyService
{
    public SettingsResetPreview PreviewReset(SettingsResetQuery query)
    {
        var snapshot = _settings.ReadBatch();
        try
        {
            string? instance = InstanceKey(query.InstanceId);
            JsonObject document = ReadDocument(snapshot.Values);
            var effective = Resolve(snapshot.Revision, snapshot.Values, document, instance);
            var available = SettingsCatalog.Entries.Where(entry => entry.Availability == SettingsCapabilityAvailability.Available
                && entry.Kind == SettingsCatalogEntryKind.Setting && entry.Definition is not null
                && (instance is null || entry.Definition.InstanceOverride)).Select(entry => entry.SettingKey!).ToHashSet(StringComparer.Ordinal);
            JsonObject? local = instance is null ? null : document["instances"]![instance] as JsonObject;
            var layer = instance is null ? SettingsLayer.Global : SettingsLayer.Instance;
            var changes = effective.Values.Where(value => available.Contains(value.Key)
                && (value.Source == layer || value.ValidationError is not null && (instance is null || local?[value.Key] is not null)))
                .Select(value => new SettingsMutation(value.Key, layer, new(SettingsOverrideMode.Inherit), query.InstanceId)).ToArray();
            _ = PrepareChanges(snapshot.Values, changes);
            return new(snapshot.Revision, Array.AsReadOnly(changes), []);
        }
        catch (Exception error) when (Recoverable(error)) { return new(snapshot.Revision, [], [error.Message]); }
    }

    public XsrResult ApplyReset(SettingsResetCommand command)
    {
        var preview = PreviewReset(new(command.InstanceId));
        if (preview.Revision != command.ExpectedRevision)
            return XsrResult.Failure(new(XsrErrorKind.Rejected, XsrSemanticId.Parse("settings.stale_revision"), "Settings changed; preview again."));
        if (preview.Errors.Count > 0) return XsrResult.Failure(Invalid(string.Join(" ", preview.Errors)));
        if (preview.Changes.Count == 0) return XsrResult.Success();
        return Apply(preview.Changes, command.ExpectedRevision);
    }
}
