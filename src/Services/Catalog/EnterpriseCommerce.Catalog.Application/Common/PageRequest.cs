using System.Diagnostics.CodeAnalysis;

namespace EnterpriseCommerce.Catalog.Application.Common;

/// <summary>
/// A page of a collection: <c>page</c> is 1-based, <c>pageSize</c> defaults to 20 and is at most 100
/// (docs/api/README.md, pagination convention). Public discovery also bounds the page number (ADR-024 section 9).
/// </summary>
public sealed record PageRequest
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    /// <summary>The deepest page any query may read: it keeps every <see cref="Skip"/> within <see cref="int"/>.</summary>
    public const int MaxPage = int.MaxValue / MaxPageSize;

    /// <summary>The deepest page of public discovery: skip-based paging costs grow with the page number.</summary>
    public const int MaxPublicPage = 100;

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
        out IReadOnlyDictionary<string, string[]> errors) =>
        TryCreate(page, pageSize, MaxPage, out request, out errors);

    /// <summary>Validates the query values against a page limit. Absent values take their defaults.</summary>
    public static bool TryCreate(
        int? page,
        int? pageSize,
        int maxPage,
        [NotNullWhen(true)] out PageRequest? request,
        out IReadOnlyDictionary<string, string[]> errors)
    {
        var problems = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var size = pageSize ?? DefaultPageSize;
        var number = page ?? 1;
        var limit = Math.Clamp(maxPage, 1, MaxPage);

        if (size is < 1 or > MaxPageSize)
        {
            problems["pageSize"] = [$"pageSize must be between 1 and {MaxPageSize}."];
        }

        if (number < 1 || number > limit)
        {
            problems["page"] = [$"page must be between 1 and {limit}."];
        }

        errors = problems;
        request = problems.Count == 0 ? new PageRequest(number, size) : null;
        return request is not null;
    }
}

/// <summary>One page of results.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, long TotalCount);
