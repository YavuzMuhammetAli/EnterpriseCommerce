using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseCommerce.Cart.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCartSchema : Migration
    {
        /// <summary>The error Down raises instead of discarding customers' carts.</summary>
        public const int CartDataExistsError = 50200;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Carts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerSubject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MerchantTenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Currency = table.Column<string>(type: "char(3)", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Carts", x => x.Id);
                    table.CheckConstraint("CK_Carts_Currency", "[Currency] IS NULL OR [Currency] COLLATE Latin1_General_BIN2 LIKE '[A-Z][A-Z][A-Z]'");
                    table.CheckConstraint("CK_Carts_CustomerSubject", "LEN([CustomerSubject]) > 0");
                    table.CheckConstraint("CK_Carts_MerchantAndCurrency", "([MerchantTenantId] IS NULL AND [Currency] IS NULL) OR ([MerchantTenantId] IS NOT NULL AND [Currency] IS NOT NULL)");
                    table.CheckConstraint("CK_Carts_Version", "[Version] >= 1");
                });

            migrationBuilder.CreateTable(
                name: "CartItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VariantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ImageUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    ImageAltText = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", nullable: false),
                    Availability = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    PriceCheckedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CartId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Options = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CartItems", x => x.Id);
                    table.CheckConstraint("CK_CartItems_Availability", "[Availability] IN ('Available', 'Unavailable', 'CurrencyMismatch')");
                    table.CheckConstraint("CK_CartItems_Currency", "[Currency] COLLATE Latin1_General_BIN2 LIKE '[A-Z][A-Z][A-Z]'");
                    table.CheckConstraint("CK_CartItems_Name", "LEN([Name]) > 0");
                    table.CheckConstraint("CK_CartItems_Quantity", "[Quantity] BETWEEN 1 AND 99");
                    table.CheckConstraint("CK_CartItems_Sku", "LEN([Sku]) > 0");
                    table.CheckConstraint("CK_CartItems_UnitPrice", "[UnitPrice] > 0 AND [UnitPrice] <= 1000000000");
                    table.ForeignKey(
                        name: "FK_CartItems_Carts_CartId",
                        column: x => x.CartId,
                        principalTable: "Carts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_CartItems_CartId_VariantId",
                table: "CartItems",
                columns: new[] { "CartId", "VariantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Carts_UpdatedAt",
                table: "Carts",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "UX_Carts_CustomerSubject",
                table: "Carts",
                column: "CustomerSubject",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Down is a development rollback of an empty database only. Customers' carts are never discarded by a
            // migration (CART_OPERATIONS.md section 6): Down refuses instead.
            migrationBuilder.Sql(
                "IF EXISTS (SELECT 1 FROM [Carts]) "
                + $"THROW {CartDataExistsError}, N'The Cart database holds carts; it is never rolled back by a migration.', 1;");

            migrationBuilder.DropTable(
                name: "CartItems");

            migrationBuilder.DropTable(
                name: "Carts");
        }
    }
}
