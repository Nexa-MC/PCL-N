using Nexa.Services.Accounts;




namespace Nexa.Services.Foundation;

/// <summary>One foundation command: set one setting to a raw schema-encoded value.</summary>
public sealed record SettingsSetCommand(string Key, string Value);

/// <summary>One foundation command: grant or revoke telemetry consent.</summary>
public sealed record TelemetryConsentCommand(bool Consent);

/// <summary>One foundation command: insert or replace one launch profile in the roster.</summary>
public sealed record AccountUpsertProfileCommand(LaunchProfile Profile);

/// <summary>Selects a credential-free roster entry at the revision displayed by the caller.</summary>
public sealed record AccountSelectProfileCommand(int Index, long? ExpectedRosterRevision = null);

/// <summary>Removes only the local profile displayed at this roster revision.</summary>
public sealed record AccountRemoveProfileCommand(int Index, long ExpectedRosterRevision);

/// <summary>One foundation query: read one setting's raw value.</summary>
public sealed record SettingsGetQuery(string Key);
