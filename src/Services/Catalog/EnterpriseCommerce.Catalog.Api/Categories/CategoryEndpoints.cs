using System.Text.Json.Serialization;
using EnterpriseCommerce.BuildingBlocks.Authentication;
using EnterpriseCommerce.Catalog.Api.Common;
using EnterpriseCommerce.Catalog.Api.Hosting;
using EnterpriseCommerce.Catalog.Api.Products;
using EnterpriseCommerce.Catalog.Api.Security;
using EnterpriseCommerce.Catalog.Application.Categories;
using EnterpriseCommerce.Catalog.Application.Common;
using EnterpriseCommerce.Catalog.Domain.Categories;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;

namespace EnterpriseCommerce.Catalog.Api.Categories;

/// <summary><c>POST /internal/v1/catalog/categories</c> body. The parent is chosen once, here.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateCategoryRequest(string? Slug, string? Name, string? Description, Guid? ParentId);

/// <summary><c>PUT /internal/v1/catalog/categories/{id}</c> body. A <c>parentId</c> member is refused: the parent never changes.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record UpdateCategoryRequest(string? Slug, string? Name, string? Description);

/// <summary>A category as the taxonomy service sees it.</summary>
internal sealed record CategoryResponse(
    Guid Id,
    string Slug,
    string Name,
    string? Description,
    Guid? ParentId,
    IReadOnlyList<Guid> AncestorIds,
    int Depth,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version)
{
    public static CategoryResponse Of(Category category)
    {
        ArgumentNullException.ThrowIfNull(category);
        return new CategoryResponse(
            category.Id, category.Slug.Value, category.Name, category.Description, category.ParentId, category.AncestorIds, category.Depth,
            category.Status.ToString(), category.CreatedAt, category.UpdatedAt, category.Version);
    }
}

/// <summary>An active category as the public sees it: no status, version or timestamps.</summary>
internal sealed record PublicCategoryResponse(Guid Id, string Slug, string Name, string? Description, Guid? ParentId, IReadOnlyList<Guid> AncestorIds, int Depth)
{
    public static PublicCategoryResponse Of(Category category)
    {
        ArgumentNullException.ThrowIfNull(category);
        return new PublicCategoryResponse(category.Id, category.Slug.Value, category.Name, category.Description, category.ParentId, category.AncestorIds, category.Depth);
    }
}

/// <summary>
/// The platform category taxonomy (CATALOG_API_CONTRACT.md sections 3 and 6). Writes: the internal API, for
/// allow-listed service clients only (ADR-024 section 6); the Gateway never routes <c>/internal/**</c>. Reads: public.
/// </summary>
internal static class CategoryEndpoints
{
    public static IEndpointRouteBuilder MapCategoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var internalCategories = endpoints.MapGroup("/internal/v1/catalog/categories")
            .RequireAuthorization(CatalogSecuritySetup.TaxonomyServicePolicy)
            .WithRequestSizeLimit();
        internalCategories.MapPost("/", CreateAsync);
        internalCategories.MapGet("/", (int? page, int? pageSize, CategoryHandlers handler, CancellationToken token) => ListAsync(page, pageSize, includeRetired: true, handler, token));
        internalCategories.MapGet("/{categoryId:guid}", (HttpContext context, Guid categoryId, CategoryHandlers handler, CancellationToken token) => GetAsync(context, categoryId, activeOnly: false, handler, token));
        internalCategories.MapPut("/{categoryId:guid}", UpdateAsync);
        internalCategories.MapPost("/{categoryId:guid}/retire", (HttpContext context, Guid categoryId, CategoryHandlers handler, CancellationToken token) =>
            ChangeStateAsync(context, categoryId, CategoryCommand.Retire, handler, token));
        internalCategories.MapPost("/{categoryId:guid}/reactivate", (HttpContext context, Guid categoryId, CategoryHandlers handler, CancellationToken token) =>
            ChangeStateAsync(context, categoryId, CategoryCommand.Reactivate, handler, token));

        var publicCategories = endpoints.MapGroup("/api/v1/catalog/categories").AllowAnonymous();
        publicCategories.MapGet("/", (int? page, int? pageSize, CategoryHandlers handler, CancellationToken token) => ListAsync(page, pageSize, includeRetired: false, handler, token));
        publicCategories.MapGet("/{categoryId:guid}", (HttpContext context, Guid categoryId, CategoryHandlers handler, CancellationToken token) => GetAsync(context, categoryId, activeOnly: true, handler, token));
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(HttpContext httpContext, CreateCategoryRequest request, CategoryHandlers handler, CancellationToken cancellationToken)
    {
        // The policy already requires an allow-listed service client; the endpoint never proceeds without one.
        if (CallerClaims.ServiceClientOf(httpContext.User) is null)
        {
            return Results.Forbid(authenticationSchemes: [JwtBearerDefaults.AuthenticationScheme]);
        }

        var result = await handler.CreateAsync(new CategoryInput(request.Slug, request.Name, request.Description, request.ParentId), cancellationToken);
        if (result.Status != CommandStatus.Created)
        {
            return Problems.For(result.Status, "category", result.Errors);
        }

        EntityTags.Set(httpContext, result.Value!.Version);
        return Results.Created($"/internal/v1/catalog/categories/{result.Value.Id}", CategoryResponse.Of(result.Value));
    }

    private static async Task<IResult> ListAsync(
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        bool includeRetired,
        CategoryHandlers handler,
        CancellationToken cancellationToken)
    {
        if (!PageRequest.TryCreate(page, pageSize, out var pageRequest, out var errors))
        {
            return Problems.Validation(errors);
        }

        var categories = await handler.ListAsync(includeRetired, pageRequest, cancellationToken);
        return includeRetired
            ? Results.Ok(new PageResponse<CategoryResponse>([.. categories.Items.Select(CategoryResponse.Of)], categories.PageNumber, categories.PageSize, categories.TotalCount))
            : Results.Ok(new PageResponse<PublicCategoryResponse>([.. categories.Items.Select(PublicCategoryResponse.Of)], categories.PageNumber, categories.PageSize, categories.TotalCount));
    }

    private static async Task<IResult> GetAsync(HttpContext httpContext, Guid categoryId, bool activeOnly, CategoryHandlers handler, CancellationToken cancellationToken)
    {
        var result = await handler.GetAsync(categoryId, activeOnly, cancellationToken);
        if (result.Value is not { } category)
        {
            return Problems.For(result.Status, "category");
        }

        if (activeOnly)
        {
            return Results.Ok(PublicCategoryResponse.Of(category));
        }

        EntityTags.Set(httpContext, category.Version);
        return Results.Ok(CategoryResponse.Of(category));
    }

    private static async Task<IResult> UpdateAsync(
        HttpContext httpContext,
        Guid categoryId,
        UpdateCategoryRequest request,
        CategoryHandlers handler,
        CancellationToken cancellationToken)
    {
        if (EntityTags.ExpectedVersion(httpContext.Request) is not { } version)
        {
            return Problems.PreconditionRequired();
        }

        return Respond(httpContext, await handler.UpdateAsync(categoryId, version, new CategoryDetailsInput(request.Slug, request.Name, request.Description), cancellationToken));
    }

    private static async Task<IResult> ChangeStateAsync(HttpContext httpContext, Guid categoryId, CategoryCommand command, CategoryHandlers handler, CancellationToken cancellationToken) =>
        Respond(httpContext, await handler.ChangeStateAsync(categoryId, command, cancellationToken));

    private static IResult Respond(HttpContext httpContext, CommandResult<Category> result)
    {
        if (result.Value is not { } category)
        {
            return Problems.For(result.Status, "category", result.Errors);
        }

        EntityTags.Set(httpContext, category.Version);
        return Results.Ok(CategoryResponse.Of(category));
    }
}
