using System.Diagnostics.CodeAnalysis;

namespace EnterpriseCommerce.TenantMerchant.Application.Common;

/// <summary>
/// A page of a collection: <c>page</c> is 1-based, <c>pageSize</c> defaults to 20 and is at most 100
/// (docs/api/README.md, pagination convention).
/// </summary>
public sealed record PageRequest
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    private PageRequest(int page, int pageSize)
    {
        Page = page;
        PageSize = pageSize;
    }

    public int Page { get; }

    public int PageSize { get; }

    /// <summary>The number of items before this page.</summary>
    public int Skip => (Page - 1) * PageSize;

    /// <summary>Validates the query values. Absent values take their defaults.</summary>
    public static bool TryCreate(
        int? page,
        int? pageSize,
        [NotNullWhen(true)] out PageRequest? request,
        out IReadOnlyDictionary<string, string[]> errors)
    {
        var problems = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var size = pageSize ?? DefaultPageSize;
        var number = page ?? 1;

        if (size is < 1 or > MaxPageSize)
        {
            problems["pageSize"] = [$"pageSize must be between 1 and {MaxPageSize}."];
        }

        // The upper bound keeps Skip within int for every valid page size.
        if (number < 1 || number > int.MaxValue / MaxPageSize)
        {
            problems["page"] = [$"page must be between 1 and {int.MaxValue / MaxPageSize}."];
        }

        errors = problems;
        request = problems.Count == 0 ? new PageRequest(number, size) : null;
        return request is not null;
    }
}
