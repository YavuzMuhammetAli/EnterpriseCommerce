namespace EnterpriseCommerce.Catalog.Api.Hosting;

/// <summary>The <c>Idempotency-Key</c> request header (IDENTITY_API_CONTRACT.md section 1, the cross-service convention).</summary>
internal static class IdempotencyKey
{
    public const string HeaderName = "Idempotency-Key";
    public const int MaxLength = 100;

    public static bool IsValid(string? value) =>
        value is { Length: > 0 and <= MaxLength }
        && value.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or ':' or '-');

    /// <summary>The validation errors of a missing or malformed key, keyed by the header name.</summary>
    public static Dictionary<string, string[]> Errors() => new(StringComparer.Ordinal)
    {
        [HeaderName] = [$"The {HeaderName} header is required: 1 to {MaxLength} characters of letters, digits, '.', '_', ':' or '-' (a UUID is recommended)."],
    };
}
