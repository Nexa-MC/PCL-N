

namespace Nexa.Services.Accounts;

/// <summary>
/// Identifies an account provider by its stable, case-insensitive name. Equality, the
/// equality operators, and hashing are all case-insensitive — the generated record members
/// are overridden precisely so `Parse("Microsoft") == Parse("microsoft")` holds.
/// </summary>
public readonly struct AccountProviderId : IEquatable<AccountProviderId>
{
    public AccountProviderId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The account provider ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;

    public bool Equals(string? value) =>
        string.Equals(Value, value, StringComparison.OrdinalIgnoreCase);

    public bool Equals(AccountProviderId other) =>
        string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) =>
        obj is AccountProviderId other && Equals(other);

    public override int GetHashCode() =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    public static bool operator ==(AccountProviderId left, AccountProviderId right) => left.Equals(right);

    public static bool operator !=(AccountProviderId left, AccountProviderId right) => !left.Equals(right);

    public static AccountProviderId Parse(string value) => new(value);
}
