using System.Globalization;
using EnterpriseCommerce.Inventory.Application.Authorization;
using EnterpriseCommerce.Inventory.Application.Catalog;
using EnterpriseCommerce.Inventory.Application.Common;
using EnterpriseCommerce.Inventory.Domain.Common;
using EnterpriseCommerce.Inventory.Domain.Items;

namespace EnterpriseCommerce.Inventory.Application.Items;

/// <summary>A request to track a Catalog variant with an initial stock, as received.</summary>
public sealed record CreateInventoryItemCommand(MerchantCaller Caller, Guid TenantId, Guid? ProductId, Guid? VariantId, int? InitialOnHand, string IdempotencyKey);

/// <summary>A merchant adjustment as received: a signed change and a reason name.</summary>
public sealed record AdjustStockCommand(MerchantCaller Caller, Guid TenantId, Guid ItemId, int? QuantityChange, string? Reason, string IdempotencyKey);

/// <summary>An adjustment's movement and the item's balance after it.</summary>
public sealed record AdjustmentOutcome(StockMovement Movement, InventoryItem Item);

/// <summary>
/// Creates an inventory item (ADR-026 sections 2, 8 and 11): <c>ManageInventory</c>, idempotent per (tenant, subject,
/// key), and only for a variant Catalog confirms in the tenant's product, which must not be archived. Catalog is
/// asked before the transaction, so no database lock is held during the call.
/// </summary>
public sealed class CreateInventoryItemHandler(
    InventoryAccessAuthorizer authorizer,
    ICatalogVariantVerifier catalog,
    IInventoryQueries queries,
    IInventoryWriteGate gate)
{
    public const string RequestHashVersion = "inventory-item-create/v1";

    public async Task<CommandResult<InventoryItem>> HandleAsync(CreateInventoryItemCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var access = await authorizer.AuthorizeAsync(command.Caller, command.TenantId, InventoryPermission.ManageInventory, cancellationToken);
        if (access != InventoryAccess.Allowed)
        {
            return CommandResult<InventoryItem>.Of(InventoryAccessAuthorizer.StatusOf(access));
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (command.ProductId is not { } productId || productId == Guid.Empty)
        {
            errors["productId"] = ["A product ID is required."];
        }

        if (command.VariantId is not { } variantId || variantId == Guid.Empty)
        {
            errors["variantId"] = ["A variant ID is required."];
        }

        if (command.InitialOnHand is not { } initial || !Quantities.IsValidInitialOnHand(initial))
        {
            errors["initialOnHand"] = [$"initialOnHand must be a whole number between 0 and {Quantities.MaxOnHand}."];
        }

        if (errors.Count > 0)
        {
            return CommandResult<InventoryItem>.Invalid(errors);
        }

        var key = new IdempotencyRecord(command.IdempotencyKey, CanonicalRequestHash.Compute(
            RequestHashVersion, command.TenantId.ToString("D"), command.ProductId!.Value.ToString("D"), command.VariantId!.Value.ToString("D"),
            command.InitialOnHand!.Value.ToString(CultureInfo.InvariantCulture)));

        if (await ReplayAsync(command, key, cancellationToken) is { } replay)
        {
            return replay;
        }

        var verdict = await VerifyAsync(command, cancellationToken);
        if (!verdict.Found)
        {
            return CommandResult<InventoryItem>.Of(CommandStatus.VariantNotFound);
        }

        if (!verdict.IsEligible || SkuSnapshot.TryCreate(verdict.Sku) is not { } sku)
        {
            InventoryMetrics.RecordCatalogVerification("not_eligible");
            return CommandResult<InventoryItem>.Of(CommandStatus.VariantNotEligible);
        }

        InventoryMetrics.RecordCatalogVerification("eligible");
        try
        {
            return await gate.ExecuteAsync(
                "create-inventory-item",
                (session, _) =>
                {
                    var (item, error) = InventoryItem.Create(
                        command.TenantId, command.ProductId.Value, command.VariantId.Value, sku, command.InitialOnHand.Value,
                        command.Caller.SubjectId, key, command.Caller.CorrelationId, session.Now);
                    if (item is null)
                    {
                        return Task.FromResult(WriteDecision.Rollback(CommandResult<InventoryItem>.Invalid(new Dictionary<string, string[]> { ["initialOnHand"] = [error.ToString()] })));
                    }

                    session.UseMessageMetadata(MessageMetadata.ForRequest(command.Caller.CorrelationId)!);
                    session.Add(item);
                    return Task.FromResult(WriteDecision.Commit(new CommandResult<InventoryItem>(CommandStatus.Created, item)));
                },
                cancellationToken);
        }
        catch (InventoryWriteConflictException exception) when (exception.Conflict == InventoryWriteConflict.UniqueConstraint)
        {
            // A concurrent retry of the same key committed first: answer it as a replay. Otherwise another request
            // created the item of this variant.
            return await ReplayAsync(command, key, cancellationToken) ?? CommandResult<InventoryItem>.Of(CommandStatus.ItemExists);
        }
    }

    private async Task<CommandResult<InventoryItem>?> ReplayAsync(CreateInventoryItemCommand command, IdempotencyRecord key, CancellationToken cancellationToken)
    {
        var existing = await queries.FindItemByCreationKeyAsync(command.TenantId, command.Caller.SubjectId, key.Key, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        return existing.CreationRequestHash.AsSpan().SequenceEqual(key.RequestHash)
            ? new CommandResult<InventoryItem>(CommandStatus.Replayed, existing)
            : CommandResult<InventoryItem>.Of(CommandStatus.IdempotencyKeyReused);
    }

    private async Task<CatalogVariantVerdict> VerifyAsync(CreateInventoryItemCommand command, CancellationToken cancellationToken)
    {
        try
        {
            var verdict = await catalog.VerifyAsync(command.TenantId, command.ProductId!.Value, command.VariantId!.Value, command.Caller.CorrelationId, cancellationToken);
            if (!verdict.Found)
            {
                InventoryMetrics.RecordCatalogVerification("not_found");
            }

            return verdict;
        }
        catch (CatalogUnavailableException)
        {
            InventoryMetrics.RecordCatalogVerification("unavailable");
            throw;
        }
    }
}

/// <summary>
/// Adjusts an item's on-hand stock (ADR-026 section 4): <c>ManageInventory</c>, an explicit delta with a reason,
/// idempotent per (tenant, subject, key), under the item's row lock, with its movement and event in the same
/// transaction. Catalog is never asked: stock operations do not depend on it (ADR-026 section 11).
/// </summary>
public sealed class AdjustStockHandler(InventoryAccessAuthorizer authorizer, IInventoryQueries queries, IInventoryWriteGate gate)
{
    public const string RequestHashVersion = "inventory-adjustment/v1";

    public async Task<CommandResult<AdjustmentOutcome>> HandleAsync(AdjustStockCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var access = await authorizer.AuthorizeAsync(command.Caller, command.TenantId, InventoryPermission.ManageInventory, cancellationToken);
        if (access != InventoryAccess.Allowed)
        {
            return CommandResult<AdjustmentOutcome>.Of(InventoryAccessAuthorizer.StatusOf(access));
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (command.QuantityChange is not { } delta || !Quantities.IsValidAdjustment(delta))
        {
            errors["quantityChange"] = [$"quantityChange must be a non-zero whole number between -{Quantities.MaxAdjustment} and {Quantities.MaxAdjustment}."];
        }

        if (!TryParseReason(command.Reason, out var reason))
        {
            errors["reason"] = [$"reason must be one of {string.Join(", ", Enum.GetNames<AdjustmentReason>())}."];
        }
        else if (command.QuantityChange is { } change && Quantities.IsValidAdjustment(change) && !AdjustmentReasons.AllowsChange(reason, change))
        {
            errors["quantityChange"] = [$"The reason {reason} does not allow a change of {change}: Receipt and Return add stock, Damage and Loss remove it."];
        }

        if (errors.Count > 0)
        {
            InventoryMetrics.RecordAdjustment("refused");
            return CommandResult<AdjustmentOutcome>.Invalid(errors);
        }

        var key = new IdempotencyRecord(command.IdempotencyKey, CanonicalRequestHash.Compute(
            RequestHashVersion, command.TenantId.ToString("D"), command.ItemId.ToString("D"),
            command.QuantityChange!.Value.ToString(CultureInfo.InvariantCulture), reason.ToString()));

        try
        {
            var result = await gate.ExecuteAsync(
                "adjust-stock",
                async (session, token) =>
                {
                    var item = await session.LockItemAsync(command.TenantId, command.ItemId, token);
                    if (item is null)
                    {
                        return WriteDecision.Rollback(CommandResult<AdjustmentOutcome>.Of(CommandStatus.NotFound));
                    }

                    // Under the item's lock: a concurrent retry of the same key has committed, or will see this one.
                    if (await session.FindAdjustmentByKeyAsync(command.TenantId, command.Caller.SubjectId, key.Key, token) is { } previous)
                    {
                        return WriteDecision.Rollback(ReplayOf(previous, key, item));
                    }

                    var adjustment = item.Adjust(command.QuantityChange.Value, reason, Actor.User(command.Caller.SubjectId), key, command.Caller.CorrelationId, session.Now);
                    if (!adjustment.Succeeded)
                    {
                        return WriteDecision.Rollback(CommandResult<AdjustmentOutcome>.Of(adjustment.Error switch
                        {
                            StockChangeError.InsufficientUnreservedStock => CommandStatus.InsufficientUnreservedStock,
                            StockChangeError.AboveMaximum => CommandStatus.StockLimitExceeded,
                            _ => throw new InvalidOperationException($"A validated adjustment was refused: {adjustment.Error}."),
                        }));
                    }

                    session.UseMessageMetadata(MessageMetadata.ForRequest(command.Caller.CorrelationId)!);
                    return WriteDecision.Commit(new CommandResult<AdjustmentOutcome>(CommandStatus.Created, new AdjustmentOutcome(adjustment.Movement!, item)));
                },
                cancellationToken);
            InventoryMetrics.RecordAdjustment(result.Status switch
            {
                CommandStatus.Created => "applied",
                CommandStatus.Replayed => "replayed",
                _ => "refused",
            });
            return result;
        }
        catch (InventoryWriteConflictException exception) when (exception.Conflict == InventoryWriteConflict.UniqueConstraint)
        {
            // The same key was used concurrently for another item: the request hash names the item, so it never matches.
            var previous = await queries.FindAdjustmentByKeyAsync(command.TenantId, command.Caller.SubjectId, key.Key, cancellationToken);
            var item = await queries.GetItemAsync(command.TenantId, command.ItemId, cancellationToken);
            return previous is not null && item is not null ? ReplayOf(previous, key, item) : CommandResult<AdjustmentOutcome>.Of(CommandStatus.IdempotencyKeyReused);
        }
    }

    /// <summary>The adjustment reasons are a closed set at the API: exact names only (ADR-026 section 4).</summary>
    public static bool TryParseReason(string? value, out AdjustmentReason reason)
    {
        reason = default;
        return value is { Length: > 0 } && Enum.GetNames<AdjustmentReason>().Contains(value, StringComparer.Ordinal) && Enum.TryParse(value, out reason);
    }

    private static CommandResult<AdjustmentOutcome> ReplayOf(StockMovement previous, IdempotencyRecord key, InventoryItem item) =>
        previous.RequestHash is { } hash && hash.AsSpan().SequenceEqual(key.RequestHash) && previous.InventoryItemId == item.Id
            ? new CommandResult<AdjustmentOutcome>(CommandStatus.Replayed, new AdjustmentOutcome(previous, item))
            : CommandResult<AdjustmentOutcome>.Of(CommandStatus.IdempotencyKeyReused);
}

/// <summary>The merchant reads (ADR-026 section 12): <c>ViewInventory</c>, any member role, tenant-scoped, untracked.</summary>
public sealed class InventoryQueryHandler(InventoryAccessAuthorizer authorizer, IInventoryQueries queries)
{
    public async Task<CommandResult<InventoryItem>> GetItemAsync(MerchantCaller caller, Guid tenantId, Guid itemId, CancellationToken cancellationToken)
    {
        var access = await authorizer.AuthorizeAsync(caller, tenantId, InventoryPermission.ViewInventory, cancellationToken);
        if (access != InventoryAccess.Allowed)
        {
            return CommandResult<InventoryItem>.Of(InventoryAccessAuthorizer.StatusOf(access));
        }

        var item = await queries.GetItemAsync(tenantId, itemId, cancellationToken);
        return item is null ? CommandResult<InventoryItem>.Of(CommandStatus.NotFound) : new CommandResult<InventoryItem>(CommandStatus.Ok, item);
    }

    public async Task<CommandResult<Page<InventoryItem>>> ListItemsAsync(MerchantCaller caller, Guid tenantId, InventoryItemFilter filter, PageRequest page, CancellationToken cancellationToken)
    {
        var access = await authorizer.AuthorizeAsync(caller, tenantId, InventoryPermission.ViewInventory, cancellationToken);
        return access != InventoryAccess.Allowed
            ? CommandResult<Page<InventoryItem>>.Of(InventoryAccessAuthorizer.StatusOf(access))
            : new CommandResult<Page<InventoryItem>>(CommandStatus.Ok, await queries.ListItemsAsync(tenantId, filter, page, cancellationToken));
    }

    public async Task<CommandResult<Page<StockMovement>>> ListMovementsAsync(MerchantCaller caller, Guid tenantId, Guid itemId, PageRequest page, CancellationToken cancellationToken)
    {
        var access = await authorizer.AuthorizeAsync(caller, tenantId, InventoryPermission.ViewInventory, cancellationToken);
        if (access != InventoryAccess.Allowed)
        {
            return CommandResult<Page<StockMovement>>.Of(InventoryAccessAuthorizer.StatusOf(access));
        }

        if (await queries.GetItemAsync(tenantId, itemId, cancellationToken) is null)
        {
            return CommandResult<Page<StockMovement>>.Of(CommandStatus.NotFound);
        }

        return new CommandResult<Page<StockMovement>>(CommandStatus.Ok, await queries.ListMovementsAsync(tenantId, itemId, page, cancellationToken));
    }
}
