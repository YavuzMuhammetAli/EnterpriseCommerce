using System.Diagnostics.CodeAnalysis;

namespace EnterpriseCommerce.TenantMerchant.Domain.Tenants;

/// <summary>
/// The calling service's opaque reference to the person who asked for an operation (for example Admin's reference to a
/// platform administrator): 1 to 128 characters of letters, digits, <c>.</c>, <c>_</c>, <c>:</c> or <c>-</c>, the same
/// rule as Identity's internal account API. It is recorded for audit only and never authorizes anything.
/// </summary>
public sealed record ActorReference
{
    public const int MaxLength = 128;

    private ActorReference(string value) => Value = value;

    public string Value { get; }

    public static bool TryCreate(string? value, [NotNullWhen(true)] out ActorReference? reference)
    {
        reference = value is { Length: > 0 and <= MaxLength } && value.All(IsAllowed) ? new ActorReference(value) : null;
        return reference is not null;
    }

    public override string ToString() => Value;

    private static bool IsAllowed(char character) =>
        character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or ':' or '-';
}
