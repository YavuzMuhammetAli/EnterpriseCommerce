using EnterpriseCommerce.Catalog.Application.Common;

namespace EnterpriseCommerce.Catalog.Api.Common;

/// <summary>Validation of query parameters: closed value sets only, never a client expression (ADR-024 section 9).</summary>
internal static class QueryParameters
{
    public static bool TryParseSort(string? value, out CatalogSort sort)
    {
        sort = CatalogSort.Newest;
        switch (value)
        {
            case null or "" or "newest":
                return true;
            case "oldest":
                sort = CatalogSort.Oldest;
                return true;
            default:
                return false;
        }
    }

    /// <summary>An enum value by its exact name (case-sensitive), or none.</summary>
    public static bool TryParseExact<TEnum>(string? value, out TEnum? result)
        where TEnum : struct, Enum
    {
        result = null;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (Enum.GetNames<TEnum>().Contains(value, StringComparer.Ordinal))
        {
            result = Enum.Parse<TEnum>(value);
            return true;
        }

        return false;
    }

    public static void Add(Dictionary<string, string[]> errors, string field, string message) => errors[field] = [message];
}
