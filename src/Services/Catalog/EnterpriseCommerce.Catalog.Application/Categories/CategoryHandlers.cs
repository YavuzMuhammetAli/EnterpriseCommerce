using EnterpriseCommerce.Catalog.Application.Common;
using EnterpriseCommerce.Catalog.Domain.Categories;
using EnterpriseCommerce.Catalog.Domain.Common;

namespace EnterpriseCommerce.Catalog.Application.Categories;

/// <summary>A category as received for creation, before validation.</summary>
public sealed record CategoryInput(string? Slug, string? Name, string? Description, Guid? ParentId);

/// <summary>The editable details of a category as received for an update. The parent is not editable.</summary>
public sealed record CategoryDetailsInput(string? Slug, string? Name, string? Description);

/// <summary>The category state commands.</summary>
public enum CategoryCommand
{
    Retire,
    Reactivate,
}

/// <summary>
/// The platform taxonomy (ADR-024 section 6). Writes come only from the internal API, whose callers are allow-listed
/// service clients authorized by the host's <c>taxonomy-service</c> policy; there is no merchant or user path.
/// </summary>
public sealed class CategoryHandlers(ICategoryStore store, TimeProvider timeProvider)
{
    public async Task<CommandResult<Category>> CreateAsync(CategoryInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!Category.TryCreateDetails(input.Slug, input.Name, input.Description, out var details, out var errors))
        {
            return CommandResult<Category>.Invalid(errors.ToDictionary());
        }

        if (input.ParentId == Guid.Empty)
        {
            return CommandResult<Category>.Invalid(new Dictionary<string, string[]> { ["parentId"] = ["The parent ID is not valid."] });
        }

        var parent = input.ParentId is { } parentId ? await store.GetAsync(parentId, cancellationToken) : null;
        var outcome = Category.TryCreate(Guid.CreateVersion7(), details, input.ParentId is not null, parent, CatalogClock.Now(timeProvider), out var category);
        if (outcome != CategoryOutcome.Changed)
        {
            return CommandResult<Category>.Of(ToStatus(outcome));
        }

        var written = await store.InsertAsync(category!, cancellationToken);
        return written == WriteOutcome.Written
            ? CommandResult<Category>.Done(category!, CommandStatus.Created)
            : CommandResult<Category>.Of(WriteOutcomes.ToStatus(written));
    }

    public async Task<CommandResult<Category>> UpdateAsync(Guid categoryId, long expectedVersion, CategoryDetailsInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (await store.GetAsync(categoryId, cancellationToken) is not { } category)
        {
            return CommandResult<Category>.Of(CommandStatus.NotFound);
        }

        if (category.Version != expectedVersion)
        {
            return CommandResult<Category>.Of(CommandStatus.PreconditionFailed);
        }

        if (!Category.TryCreateDetails(input.Slug, input.Name, input.Description, out var details, out var errors))
        {
            return CommandResult<Category>.Invalid(errors.ToDictionary());
        }

        var loadedVersion = category.Version;
        category.Update(details, CatalogClock.Now(timeProvider));
        return await SaveAsync(category, loadedVersion, cancellationToken);
    }

    public async Task<CommandResult<Category>> ChangeStateAsync(Guid categoryId, CategoryCommand command, CancellationToken cancellationToken)
    {
        if (await store.GetAsync(categoryId, cancellationToken) is not { } category)
        {
            return CommandResult<Category>.Of(CommandStatus.NotFound);
        }

        var loadedVersion = category.Version;
        var now = CatalogClock.Now(timeProvider);
        var outcome = command == CategoryCommand.Retire
            ? category.Retire(category.Status == CatalogStatus.Active && await store.HasActiveChildrenAsync(categoryId, cancellationToken), now)
            : category.Reactivate(category.ParentId is { } parentId ? await store.GetAsync(parentId, cancellationToken) : null, now);

        return outcome switch
        {
            CategoryOutcome.Unchanged => CommandResult<Category>.Done(category),
            CategoryOutcome.Changed => await SaveAsync(category, loadedVersion, cancellationToken),
            _ => CommandResult<Category>.Of(ToStatus(outcome)),
        };
    }

    /// <summary>A category in any status (internal), or only an active one (public).</summary>
    public async Task<CommandResult<Category>> GetAsync(Guid categoryId, bool activeOnly, CancellationToken cancellationToken) =>
        await store.GetAsync(categoryId, cancellationToken) is { } category && (!activeOnly || category.Status == CatalogStatus.Active)
            ? CommandResult<Category>.Done(category)
            : CommandResult<Category>.Of(CommandStatus.NotFound);

    public async Task<Page<Category>> ListAsync(bool includeRetired, PageRequest page, CancellationToken cancellationToken) =>
        await store.ListAsync(includeRetired, page, cancellationToken);

    private static CommandStatus ToStatus(CategoryOutcome outcome) => outcome switch
    {
        CategoryOutcome.ParentUnavailable => CommandStatus.ParentUnavailable,
        CategoryOutcome.TooDeep => CommandStatus.CategoryTooDeep,
        CategoryOutcome.HasActiveChildren => CommandStatus.HasActiveChildren,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

    private async Task<CommandResult<Category>> SaveAsync(Category category, long loadedVersion, CancellationToken cancellationToken)
    {
        var outcome = await store.ReplaceAsync(category, loadedVersion, cancellationToken);
        return outcome == WriteOutcome.Written ? CommandResult<Category>.Done(category) : CommandResult<Category>.Of(WriteOutcomes.ToStatus(outcome));
    }
}
