using System.Text.Json;
using EnterpriseCommerce.Cart.Domain.Carts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace EnterpriseCommerce.Cart.Infrastructure.Persistence;

/// <summary>
/// The Cart service's own database (ADR-003, ADR-011, ADR-029 section 8). No other service reads or writes it, and no
/// foreign key or query reaches another service's data: product, variant and tenant IDs are references only.
/// </summary>
/// <remarks>
/// The domain rules that a row can express are CHECK constraints and unique indexes too (PHASE_6_CART.md section 4), so
/// a defect in the application cannot store a cart the domain would refuse.
/// </remarks>
internal sealed class CartDbContext(DbContextOptions<CartDbContext> options) : DbContext(options)
{
    public const string RowVersion = "RowVersion";
    public const string CartIdColumn = "CartId";

    public const string CustomerIndex = "UX_Carts_CustomerSubject";
    public const string UpdatedAtIndex = "IX_Carts_UpdatedAt";
    public const string LineIndex = "UX_CartItems_CartId_VariantId";

    private static readonly JsonSerializerOptions OptionsJson = new(JsonSerializerDefaults.Web);

    public DbSet<ShoppingCart> Carts => Set<ShoppingCart>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        MapCarts(modelBuilder);
        MapItems(modelBuilder);
    }

    private static void MapCarts(ModelBuilder modelBuilder)
    {
        var cart = modelBuilder.Entity<ShoppingCart>();
        cart.ToTable("Carts", table =>
        {
            table.HasCheckConstraint("CK_Carts_Version", "[Version] >= 1");
            table.HasCheckConstraint(
                "CK_Carts_MerchantAndCurrency",
                "([MerchantTenantId] IS NULL AND [Currency] IS NULL) OR ([MerchantTenantId] IS NOT NULL AND [Currency] IS NOT NULL)");
            table.HasCheckConstraint("CK_Carts_Currency", "[Currency] IS NULL OR [Currency] COLLATE Latin1_General_BIN2 LIKE '[A-Z][A-Z][A-Z]'");
            table.HasCheckConstraint("CK_Carts_CustomerSubject", "LEN([CustomerSubject]) > 0");
        });

        cart.HasKey(c => c.Id);
        cart.Property(c => c.Id).ValueGeneratedNever();
        cart.Property(c => c.CustomerSubject).HasMaxLength(CustomerSubject.MaxLength).IsRequired();
        cart.Property(c => c.Currency).HasColumnType("char(3)");
        cart.Property(c => c.Version).IsConcurrencyToken();
        cart.Property<byte[]>(RowVersion).IsRowVersion();

        cart.HasIndex(c => c.CustomerSubject).IsUnique().HasDatabaseName(CustomerIndex);
        cart.HasIndex(c => c.UpdatedAt).HasDatabaseName(UpdatedAtIndex);

        cart.HasMany(c => c.Items).WithOne().HasForeignKey(CartIdColumn).IsRequired().OnDelete(DeleteBehavior.Cascade);
        cart.Navigation(c => c.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static void MapItems(ModelBuilder modelBuilder)
    {
        var item = modelBuilder.Entity<CartItem>();
        item.ToTable("CartItems", table =>
        {
            table.HasCheckConstraint("CK_CartItems_Quantity", $"[Quantity] BETWEEN {CartLimits.MinQuantity} AND {CartLimits.MaxQuantityPerLine}");
            table.HasCheckConstraint("CK_CartItems_UnitPrice", "[UnitPrice] > 0 AND [UnitPrice] <= 1000000000");
            table.HasCheckConstraint("CK_CartItems_Currency", "[Currency] COLLATE Latin1_General_BIN2 LIKE '[A-Z][A-Z][A-Z]'");
            table.HasCheckConstraint("CK_CartItems_Availability", "[Availability] IN ('Available', 'Unavailable', 'CurrencyMismatch')");
            table.HasCheckConstraint("CK_CartItems_Name", "LEN([Name]) > 0");
            table.HasCheckConstraint("CK_CartItems_Sku", "LEN([Sku]) > 0");
        });

        item.HasKey(i => i.Id);
        item.Property(i => i.Id).ValueGeneratedNever();
        item.Property(i => i.Name).HasMaxLength(ProductSnapshot.MaxNameLength).IsRequired();
        item.Property(i => i.Sku).HasMaxLength(ProductSnapshot.MaxSkuLength).IsRequired();
        item.Property(i => i.ImageUrl).HasMaxLength(ProductImage.MaxUrlLength);
        item.Property(i => i.ImageAltText).HasMaxLength(ProductImage.MaxAltTextLength);
        item.Property(i => i.UnitPrice).HasPrecision(12, 2);
        item.Property(i => i.Currency).HasColumnType("char(3)").IsRequired();
        item.Property(i => i.Availability).HasConversion<string>().HasMaxLength(20).IsUnicode(false);

        // At most 10 options of at most 40 and 100 characters: a small JSON array, never queried.
        item.Property<List<VariantOption>>("_options")
            .HasColumnName("Options")
            .HasMaxLength(4000)
            .IsRequired()
            .HasConversion(OptionsConverter, OptionsComparer);
        item.Ignore(i => i.Options);
        item.Ignore(i => i.Price);

        item.HasIndex(CartIdColumn, nameof(CartItem.VariantId)).IsUnique().HasDatabaseName(LineIndex);
    }

    private static readonly ValueConverter<List<VariantOption>, string> OptionsConverter = new(
        options => JsonSerializer.Serialize(options, OptionsJson),
        json => JsonSerializer.Deserialize<List<VariantOption>>(json, OptionsJson) ?? new List<VariantOption>());

    private static readonly ValueComparer<List<VariantOption>> OptionsComparer = new(
        (left, right) => left!.SequenceEqual(right!),
        options => options.Aggregate(0, (hash, option) => HashCode.Combine(hash, option.GetHashCode())),
        options => options.ToList());
}
