using System.Text.Json;
using EnterpriseCommerce.Catalog.Application.Authorization;
using EnterpriseCommerce.Catalog.Application.Common;
using EnterpriseCommerce.Catalog.Domain.Common;
using EnterpriseCommerce.Catalog.Domain.Products;

namespace EnterpriseCommerce.Catalog.Application.Products;

/// <summary>The lifecycle commands of ADR-024 section 5.</summary>
public enum ProductCommand
{
    Publish,
    Unpublish,
    Archive,
    Restore,
}

/// <summary>
/// Creates a draft product for an authorized member (<see cref="CatalogPermission.EditProducts"/>). The tenant is the
/// route tenant the caller was authorized for, never a value of the body. Idempotent per (tenant, user,
/// <c>Idempotency-Key</c>): the same key and request replay the first product; the same key with another request is
/// refused. Concurrent duplicates are settled by the unique indexes.
/// </summary>
public sealed class CreateProductHandler(
    CatalogAccessAuthorizer authorizer,
    IProductStore store,
    ProductReferences references,
    TimeProvider timeProvider)
{
    private const string HashVersion = "catalog-product-create/v1";

    public async Task<CommandResult<Product>> HandleAsync(
        MerchantCaller caller,
        Guid tenantId,
        string idempotencyKey,
        ProductContentInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(input);

        var access = await authorizer.AuthorizeAsync(caller, tenantId, CatalogPermission.EditProducts, cancellationToken);
        if (access != CatalogAccess.Allowed)
        {
            return CommandResult<Product>.Of(MerchantAccess.ToStatus(access));
        }

        var hash = Convert.ToHexString(CanonicalRequestHash.Compute(HashVersion, JsonSerializer.Serialize(input)));
        if (await ReplayAsync(tenantId, caller.SubjectId, idempotencyKey, hash, cancellationToken) is { } replay)
        {
            return replay;
        }

        if (!ProductContent.TryCreate(input, [], Guid.CreateVersion7, out var content, out var errors))
        {
            return CommandResult<Product>.Invalid(errors.ToDictionary());
        }

        var check = await references.CheckAsync(tenantId, content, null, cancellationToken);
        if (!check.Errors.IsEmpty)
        {
            return CommandResult<Product>.Invalid(check.Errors.ToDictionary());
        }

        var product = Product.Create(
            Guid.CreateVersion7(), tenantId, content, check.CategoryTreeIds, new ProductCreation(caller.SubjectId, idempotencyKey, hash), CatalogClock.Now(timeProvider));
        var outcome = await store.InsertAsync(product, cancellationToken);
        if (outcome == WriteOutcome.Written)
        {
            return CommandResult<Product>.Done(product, CommandStatus.Created);
        }

        // A concurrent retry of the same request may have won the race: it is answered as a replay.
        return await ReplayAsync(tenantId, caller.SubjectId, idempotencyKey, hash, cancellationToken)
            ?? CommandResult<Product>.Of(outcome == WriteOutcome.CreationKeyTaken ? CommandStatus.ConcurrencyConflict : WriteOutcomes.ToStatus(outcome));
    }

    private async Task<CommandResult<Product>?> ReplayAsync(Guid tenantId, string subjectId, string key, string hash, CancellationToken cancellationToken) =>
        await store.FindByCreationKeyAsync(tenantId, subjectId, key, cancellationToken) is { } existing
            ? string.Equals(existing.Creation.RequestHash, hash, StringComparison.Ordinal)
                ? CommandResult<Product>.Done(existing, CommandStatus.Replayed)
                : CommandResult<Product>.Of(CommandStatus.IdempotencyKeyReused)
            : null;
}

/// <summary>
/// Replaces the editable content of a product (<see cref="CatalogPermission.EditProducts"/>), guarded by the version
/// the client read. A published product is revalidated (ADR-024 section 5); an archived one is read-only.
/// </summary>
public sealed class UpdateProductHandler(
    CatalogAccessAuthorizer authorizer,
    IProductStore store,
    ProductReferences references,
    TimeProvider timeProvider)
{
    public async Task<CommandResult<Product>> HandleAsync(
        MerchantCaller caller,
        Guid tenantId,
        Guid productId,
        long expectedVersion,
        ProductContentInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(input);

        var access = await authorizer.AuthorizeAsync(caller, tenantId, CatalogPermission.EditProducts, cancellationToken);
        if (access != CatalogAccess.Allowed)
        {
            return CommandResult<Product>.Of(MerchantAccess.ToStatus(access));
        }

        if (await store.GetAsync(tenantId, productId, cancellationToken) is not { } product)
        {
            return CommandResult<Product>.Of(CommandStatus.NotFound);
        }

        if (product.Version != expectedVersion)
        {
            return CommandResult<Product>.Of(CommandStatus.PreconditionFailed);
        }

        if (product.Status == ProductStatus.Archived)
        {
            return CommandResult<Product>.Of(CommandStatus.ProductArchived);
        }

        if (!ProductContent.TryCreate(input, product.Content.Variants, Guid.CreateVersion7, out var content, out var errors))
        {
            return CommandResult<Product>.Invalid(errors.ToDictionary());
        }

        var check = await references.CheckAsync(tenantId, content, product.Content, cancellationToken);
        if (!check.Errors.IsEmpty)
        {
            return CommandResult<Product>.Invalid(check.Errors.ToDictionary());
        }

        var loadedVersion = product.Version;
        var change = product.ReplaceContent(content, check.CategoryTreeIds, check.State, caller.SubjectId, CatalogClock.Now(timeProvider), out var unmet);
        return change switch
        {
            ProductChangeOutcome.Archived => CommandResult<Product>.Of(CommandStatus.ProductArchived),
            ProductChangeOutcome.RequirementsNotMet => CommandResult<Product>.NotMet(unmet),
            _ => await SaveAsync(store, product, loadedVersion, cancellationToken),
        };
    }

    internal static async Task<CommandResult<Product>> SaveAsync(IProductStore store, Product product, long loadedVersion, CancellationToken cancellationToken)
    {
        var outcome = await store.ReplaceAsync(product, loadedVersion, cancellationToken);
        return outcome == WriteOutcome.Written
            ? CommandResult<Product>.Done(product)
            : CommandResult<Product>.Of(WriteOutcomes.ToStatus(outcome));
    }
}

/// <summary>Replaces the ordered media references of a product (<see cref="CatalogPermission.EditProducts"/>).</summary>
public sealed class ReplaceProductMediaHandler(CatalogAccessAuthorizer authorizer, IProductStore store, TimeProvider timeProvider)
{
    public async Task<CommandResult<Product>> HandleAsync(
        MerchantCaller caller,
        Guid tenantId,
        Guid productId,
        long expectedVersion,
        IReadOnlyList<MediaInput>? media,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var access = await authorizer.AuthorizeAsync(caller, tenantId, CatalogPermission.EditProducts, cancellationToken);
        if (access != CatalogAccess.Allowed)
        {
            return CommandResult<Product>.Of(MerchantAccess.ToStatus(access));
        }

        if (await store.GetAsync(tenantId, productId, cancellationToken) is not { } product)
        {
            return CommandResult<Product>.Of(CommandStatus.NotFound);
        }

        if (product.Version != expectedVersion)
        {
            return CommandResult<Product>.Of(CommandStatus.PreconditionFailed);
        }

        if (product.Status == ProductStatus.Archived)
        {
            return CommandResult<Product>.Of(CommandStatus.ProductArchived);
        }

        if (!Product.TryCreateMedia(media, product.Media, Guid.CreateVersion7, out var references, out var errors))
        {
            return CommandResult<Product>.Invalid(errors.ToDictionary());
        }

        var loadedVersion = product.Version;
        product.ReplaceMedia(references, caller.SubjectId, CatalogClock.Now(timeProvider));
        return await UpdateProductHandler.SaveAsync(store, product, loadedVersion, cancellationToken);
    }
}

/// <summary>
/// The lifecycle commands (<see cref="CatalogPermission.PublishProducts"/>). A command whose target state is already
/// in effect succeeds without a write. Publishing checks the current state of the references.
/// </summary>
public sealed class ProductLifecycleHandler(
    CatalogAccessAuthorizer authorizer,
    IProductStore store,
    ProductReferences references,
    TimeProvider timeProvider)
{
    public async Task<CommandResult<Product>> HandleAsync(
        MerchantCaller caller,
        Guid tenantId,
        Guid productId,
        ProductCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var access = await authorizer.AuthorizeAsync(caller, tenantId, CatalogPermission.PublishProducts, cancellationToken);
        if (access != CatalogAccess.Allowed)
        {
            return CommandResult<Product>.Of(MerchantAccess.ToStatus(access));
        }

        if (await store.GetAsync(tenantId, productId, cancellationToken) is not { } product)
        {
            return CommandResult<Product>.Of(CommandStatus.NotFound);
        }

        var now = CatalogClock.Now(timeProvider);
        var loadedVersion = product.Version;
        IReadOnlyList<string> unmet = [];
        TransitionOutcome outcome;
        switch (command)
        {
            case ProductCommand.Publish:
                var check = await references.CheckAsync(tenantId, product.Content, product.Content, cancellationToken);
                outcome = product.Publish(check.State, caller.SubjectId, now, out unmet);
                break;
            case ProductCommand.Unpublish:
                outcome = product.Unpublish(caller.SubjectId, now);
                break;
            case ProductCommand.Archive:
                outcome = product.Archive(caller.SubjectId, now);
                break;
            case ProductCommand.Restore:
                outcome = product.Restore(caller.SubjectId, now);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, null);
        }

        return outcome switch
        {
            TransitionOutcome.Unchanged => CommandResult<Product>.Done(product),
            TransitionOutcome.InvalidTransition => CommandResult<Product>.Of(CommandStatus.InvalidTransition),
            TransitionOutcome.RequirementsNotMet => CommandResult<Product>.NotMet(unmet),
            _ => await UpdateProductHandler.SaveAsync(store, product, loadedVersion, cancellationToken),
        };
    }
}

/// <summary>Reads of the tenant's products for members (<see cref="CatalogPermission.ViewCatalog"/>), in every status.</summary>
public sealed class MerchantProductQueryHandler(CatalogAccessAuthorizer authorizer, IProductStore store)
{
    public async Task<CommandResult<Product>> GetAsync(MerchantCaller caller, Guid tenantId, Guid productId, CancellationToken cancellationToken)
    {
        var access = await authorizer.AuthorizeAsync(caller, tenantId, CatalogPermission.ViewCatalog, cancellationToken);
        if (access != CatalogAccess.Allowed)
        {
            return CommandResult<Product>.Of(MerchantAccess.ToStatus(access));
        }

        return await store.GetAsync(tenantId, productId, cancellationToken) is { } product
            ? CommandResult<Product>.Done(product)
            : CommandResult<Product>.Of(CommandStatus.NotFound);
    }

    public async Task<CommandResult<Page<Product>>> ListAsync(
        MerchantCaller caller,
        Guid tenantId,
        MerchantProductFilter filter,
        PageRequest page,
        CancellationToken cancellationToken)
    {
        var access = await authorizer.AuthorizeAsync(caller, tenantId, CatalogPermission.ViewCatalog, cancellationToken);
        if (access != CatalogAccess.Allowed)
        {
            return CommandResult<Page<Product>>.Of(MerchantAccess.ToStatus(access));
        }

        return CommandResult<Page<Product>>.Done(await store.ListAsync(tenantId, filter, page, cancellationToken));
    }
}

/// <summary>Maps an access decision to a command status.</summary>
public static class MerchantAccess
{
    public static CommandStatus ToStatus(CatalogAccess access) =>
        access == CatalogAccess.Forbidden ? CommandStatus.PermissionDenied : CommandStatus.TenantNotVisible;
}
