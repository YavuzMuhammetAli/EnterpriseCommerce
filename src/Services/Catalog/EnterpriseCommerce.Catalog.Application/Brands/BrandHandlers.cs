using EnterpriseCommerce.Catalog.Application.Authorization;
using EnterpriseCommerce.Catalog.Application.Common;
using EnterpriseCommerce.Catalog.Application.Products;
using EnterpriseCommerce.Catalog.Domain.Brands;
using EnterpriseCommerce.Catalog.Domain.Common;

namespace EnterpriseCommerce.Catalog.Application.Brands;

/// <summary>The brand input as received, before validation. It never contains the tenant.</summary>
public sealed record BrandInput(string? Slug, string? Name, string? Description);

/// <summary>The brand state commands.</summary>
public enum BrandCommand
{
    Retire,
    Reactivate,
}

/// <summary>
/// Tenant brands (ADR-024 section 7): reads need <see cref="CatalogPermission.ViewCatalog"/>, changes
/// <see cref="CatalogPermission.ManageBrands"/>. A brand belongs to the route tenant the caller was authorized for, and
/// every read and write is scoped to it.
/// </summary>
public sealed class BrandHandlers(CatalogAccessAuthorizer authorizer, IBrandStore store, TimeProvider timeProvider)
{
    public async Task<CommandResult<Brand>> CreateAsync(MerchantCaller caller, Guid tenantId, BrandInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (await DenyAsync<Brand>(caller, tenantId, CatalogPermission.ManageBrands, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (!Brand.TryCreateDetails(input.Slug, input.Name, input.Description, out var details, out var errors))
        {
            return CommandResult<Brand>.Invalid(errors.ToDictionary());
        }

        var brand = Brand.Create(Guid.CreateVersion7(), tenantId, details, CatalogClock.Now(timeProvider));
        var outcome = await store.InsertAsync(brand, cancellationToken);
        return outcome == WriteOutcome.Written
            ? CommandResult<Brand>.Done(brand, CommandStatus.Created)
            : CommandResult<Brand>.Of(WriteOutcomes.ToStatus(outcome));
    }

    public async Task<CommandResult<Brand>> UpdateAsync(
        MerchantCaller caller,
        Guid tenantId,
        Guid brandId,
        long expectedVersion,
        BrandInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (await DenyAsync<Brand>(caller, tenantId, CatalogPermission.ManageBrands, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (await store.GetAsync(tenantId, brandId, cancellationToken) is not { } brand)
        {
            return CommandResult<Brand>.Of(CommandStatus.NotFound);
        }

        if (brand.Version != expectedVersion)
        {
            return CommandResult<Brand>.Of(CommandStatus.PreconditionFailed);
        }

        if (!Brand.TryCreateDetails(input.Slug, input.Name, input.Description, out var details, out var errors))
        {
            return CommandResult<Brand>.Invalid(errors.ToDictionary());
        }

        var loadedVersion = brand.Version;
        brand.Update(details, CatalogClock.Now(timeProvider));
        return await SaveAsync(brand, loadedVersion, cancellationToken);
    }

    public async Task<CommandResult<Brand>> ChangeStateAsync(
        MerchantCaller caller,
        Guid tenantId,
        Guid brandId,
        BrandCommand command,
        CancellationToken cancellationToken)
    {
        if (await DenyAsync<Brand>(caller, tenantId, CatalogPermission.ManageBrands, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (await store.GetAsync(tenantId, brandId, cancellationToken) is not { } brand)
        {
            return CommandResult<Brand>.Of(CommandStatus.NotFound);
        }

        var loadedVersion = brand.Version;
        var now = CatalogClock.Now(timeProvider);
        var outcome = command == BrandCommand.Retire ? brand.Retire(now) : brand.Reactivate(now);
        return outcome == TransitionOutcome.Unchanged
            ? CommandResult<Brand>.Done(brand)
            : await SaveAsync(brand, loadedVersion, cancellationToken);
    }

    public async Task<CommandResult<Brand>> GetAsync(MerchantCaller caller, Guid tenantId, Guid brandId, CancellationToken cancellationToken)
    {
        if (await DenyAsync<Brand>(caller, tenantId, CatalogPermission.ViewCatalog, cancellationToken) is { } denied)
        {
            return denied;
        }

        return await store.GetAsync(tenantId, brandId, cancellationToken) is { } brand
            ? CommandResult<Brand>.Done(brand)
            : CommandResult<Brand>.Of(CommandStatus.NotFound);
    }

    public async Task<CommandResult<Page<Brand>>> ListAsync(MerchantCaller caller, Guid tenantId, PageRequest page, CancellationToken cancellationToken)
    {
        if (await DenyAsync<Page<Brand>>(caller, tenantId, CatalogPermission.ViewCatalog, cancellationToken) is { } denied)
        {
            return denied;
        }

        return CommandResult<Page<Brand>>.Done(await store.ListAsync(tenantId, page, cancellationToken));
    }

    private async Task<CommandResult<T>?> DenyAsync<T>(MerchantCaller caller, Guid tenantId, CatalogPermission permission, CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(caller);
        var access = await authorizer.AuthorizeAsync(caller, tenantId, permission, cancellationToken);
        return access == CatalogAccess.Allowed ? null : CommandResult<T>.Of(MerchantAccess.ToStatus(access));
    }

    private async Task<CommandResult<Brand>> SaveAsync(Brand brand, long loadedVersion, CancellationToken cancellationToken)
    {
        var outcome = await store.ReplaceAsync(brand, loadedVersion, cancellationToken);
        return outcome == WriteOutcome.Written ? CommandResult<Brand>.Done(brand) : CommandResult<Brand>.Of(WriteOutcomes.ToStatus(outcome));
    }
}
