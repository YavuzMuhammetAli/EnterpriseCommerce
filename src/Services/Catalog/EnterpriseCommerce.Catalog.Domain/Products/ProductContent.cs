using System.Diagnostics.CodeAnalysis;
using EnterpriseCommerce.Catalog.Domain.Common;

namespace EnterpriseCommerce.Catalog.Domain.Products;

/// <summary>
/// The validated, editable content of a product: everything a merchant may set except the lifecycle and media. The
/// tenant is not part of it, so no update can change a product's owner (ADR-024 section 2).
/// </summary>
public sealed record ProductContent
{
    public const int MaxNameLength = 200;
    public const int MaxShortDescriptionLength = 500;
    public const int MaxDescriptionLength = 10_000;
    public const int MaxCategories = 5;
    public const int MaxAttributes = 50;
    public const int MaxVariants = 100;

    private ProductContent(
        Slug slug,
        string name,
        string? shortDescription,
        string? description,
        Guid? brandId,
        IReadOnlyList<Guid> categoryIds,
        IReadOnlyList<CatalogAttribute> attributes,
        IReadOnlyList<ProductVariant> variants)
    {
        Slug = slug;
        Name = name;
        ShortDescription = shortDescription;
        Description = description;
        BrandId = brandId;
        CategoryIds = categoryIds;
        Attributes = attributes;
        Variants = variants;
    }

    public Slug Slug { get; }

    public string Name { get; }

    public string? ShortDescription { get; }

    public string? Description { get; }

    public Guid? BrandId { get; }

    public IReadOnlyList<Guid> CategoryIds { get; }

    public IReadOnlyList<CatalogAttribute> Attributes { get; }

    public IReadOnlyList<ProductVariant> Variants { get; }

    /// <summary>
    /// Validates the input. <paramref name="existingVariants"/> are the product's current variants: an input variant
    /// with an ID must name one of them and keeps that ID; an input variant without an ID gets a new one from
    /// <paramref name="newId"/>. Every error is collected, keyed by its request field.
    /// </summary>
    public static bool TryCreate(
        ProductContentInput input,
        IReadOnlyList<ProductVariant> existingVariants,
        Func<Guid> newId,
        [NotNullWhen(true)] out ProductContent? content,
        out ValidationErrors errors)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(existingVariants);
        ArgumentNullException.ThrowIfNull(newId);

        errors = new ValidationErrors();

        if (!Slug.TryCreate(input.Slug, out var slug))
        {
            errors.Add("slug", $"The slug must be {Slug.Rule}");
        }

        if (!CatalogText.TryRequired(input.Name, MaxNameLength, out var name))
        {
            errors.Add("name", $"The name is required: 1 to {MaxNameLength} characters on one line.");
        }

        if (!CatalogText.TryOptional(input.ShortDescription, MaxShortDescriptionLength, multiLine: false, out var shortDescription))
        {
            errors.Add("shortDescription", $"The short description is at most {MaxShortDescriptionLength} characters on one line.");
        }

        if (!CatalogText.TryOptional(input.Description, MaxDescriptionLength, multiLine: true, out var description))
        {
            errors.Add("description", $"The description is at most {MaxDescriptionLength} characters of plain text.");
        }

        if (input.BrandId == Guid.Empty)
        {
            errors.Add("brandId", "The brand ID is not valid.");
        }

        var categoryIds = ParseCategories(input.CategoryIds, errors);
        var attributes = ParseAttributes(input.Attributes, "attributes", MaxAttributes, errors);
        var variants = ParseVariants(input.Variants, existingVariants, newId, errors);

        content = errors.IsEmpty
            ? new ProductContent(slug!, name, shortDescription, description, input.BrandId, categoryIds, attributes, variants)
            : null;
        return content is not null;
    }

    /// <summary>Restores stored content, which was valid when it was written.</summary>
    public static ProductContent Rehydrate(
        Slug slug,
        string name,
        string? shortDescription,
        string? description,
        Guid? brandId,
        IReadOnlyList<Guid> categoryIds,
        IReadOnlyList<CatalogAttribute> attributes,
        IReadOnlyList<ProductVariant> variants) =>
        new(slug, name, shortDescription, description, brandId, categoryIds, attributes, variants);

    private static List<Guid> ParseCategories(IReadOnlyList<Guid>? input, ValidationErrors errors)
    {
        var ids = input ?? [];
        if (ids.Count > MaxCategories)
        {
            errors.Add("categoryIds", $"A product has at most {MaxCategories} categories.");
        }

        if (ids.Any(id => id == Guid.Empty))
        {
            errors.Add("categoryIds", "A category ID is not valid.");
        }

        if (ids.Distinct().Count() != ids.Count)
        {
            errors.Add("categoryIds", "A category is listed more than once.");
        }

        return [.. ids];
    }

    private static List<CatalogAttribute> ParseAttributes(IReadOnlyList<KeyValueInput>? input, string field, int max, ValidationErrors errors)
    {
        var entries = input ?? [];
        var attributes = new List<CatalogAttribute>(entries.Count);
        if (entries.Count > max)
        {
            errors.Add(field, $"At most {max} entries are allowed.");
            return attributes;
        }

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry is null)
            {
                errors.Add($"{field}[{index}]", "The entry is required.");
                continue;
            }

            if (CatalogAttribute.TryCreate(entry.Key, entry.Value, out var attribute, out var keyError, out var valueError))
            {
                attributes.Add(attribute);
                continue;
            }

            if (keyError is not null)
            {
                errors.Add($"{field}[{index}].key", keyError);
            }

            if (valueError is not null)
            {
                errors.Add($"{field}[{index}].value", valueError);
            }
        }

        if (attributes.GroupBy(attribute => attribute.Key, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            errors.Add(field, "Each key may appear only once.");
        }

        return attributes;
    }

    private static List<ProductVariant> ParseVariants(
        IReadOnlyList<VariantInput>? input,
        IReadOnlyList<ProductVariant> existing,
        Func<Guid> newId,
        ValidationErrors errors)
    {
        var entries = input ?? [];
        var variants = new List<ProductVariant>(entries.Count);
        if (entries.Count > MaxVariants)
        {
            errors.Add("variants", $"A product has at most {MaxVariants} variants.");
            return variants;
        }

        var existingIds = existing.Select(variant => variant.Id).ToHashSet();
        var seenIds = new HashSet<Guid>();
        for (var index = 0; index < entries.Count; index++)
        {
            var field = $"variants[{index}]";
            var entry = entries[index];
            if (entry is null)
            {
                errors.Add(field, "The variant is required.");
                continue;
            }

            var valid = true;
            if (entry.Id is { } id && (!existingIds.Contains(id) || !seenIds.Add(id)))
            {
                errors.Add($"{field}.id", "The variant ID is not a variant of this product, or is listed more than once.");
                valid = false;
            }

            if (!Sku.TryCreate(entry.Sku, out var sku))
            {
                errors.Add($"{field}.sku", $"The SKU must be {Sku.Rule}");
                valid = false;
            }

            if (!Money.TryCreate(entry.Amount, entry.Currency, out var price, out var amountError, out var currencyError))
            {
                if (amountError is not null)
                {
                    errors.Add($"{field}.amount", amountError);
                }

                if (currencyError is not null)
                {
                    errors.Add($"{field}.currency", currencyError);
                }

                valid = false;
            }

            var optionErrors = new ValidationErrors();
            var options = ParseAttributes(entry.Options, $"{field}.options", ProductVariant.MaxOptions, optionErrors);
            if (!optionErrors.IsEmpty)
            {
                foreach (var (optionField, messages) in optionErrors.ToDictionary())
                {
                    foreach (var message in messages)
                    {
                        errors.Add(optionField, message);
                    }
                }

                valid = false;
            }

            if (valid)
            {
                variants.Add(new ProductVariant(entry.Id ?? newId(), sku!, options, price!, entry.IsActive ?? true));
            }
        }

        if (variants.GroupBy(variant => variant.Sku.Key, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            errors.Add("variants", "Each SKU may appear only once (compared without regard to case).");
        }

        if (variants.GroupBy(variant => variant.OptionSignature, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            errors.Add("variants", "Each variant needs a distinct set of options.");
        }

        if (variants.Select(variant => variant.Price.Currency).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            errors.Add("variants", "All variants of a product use the same currency.");
        }

        return variants;
    }
}
