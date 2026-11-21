namespace EnterpriseCommerce.TenantMerchant.Domain.Invitations;

/// <summary>
/// The SHA-256 hash of an invitation's acceptance token: the only form of the token that is ever stored (ADR-021).
/// A stolen row cannot be turned into a usable token.
/// </summary>
public sealed record InvitationTokenHash
{
    public const int Length = 32;

    private readonly byte[] _value;

    private InvitationTokenHash(byte[] value) => _value = value;

    /// <summary>A copy of the 32 hash bytes.</summary>
    public byte[] ToArray() => (byte[])_value.Clone();

    /// <exception cref="ArgumentException">The value is not 32 bytes long.</exception>
    public static InvitationTokenHash FromBytes(ReadOnlySpan<byte> value) =>
        value.Length == Length
            ? new InvitationTokenHash(value.ToArray())
            : throw new ArgumentException($"An invitation token hash is {Length} bytes.", nameof(value));

    public bool Equals(InvitationTokenHash? other) => other is not null && _value.AsSpan().SequenceEqual(other._value);

    public override int GetHashCode() => BitConverter.ToInt32(_value, 0);

    /// <summary>Never renders the hash.</summary>
    public override string ToString() => "[invitation token hash]";
}
