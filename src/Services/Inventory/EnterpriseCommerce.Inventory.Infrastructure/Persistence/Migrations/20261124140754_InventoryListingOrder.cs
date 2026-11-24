using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseCommerce.Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InventoryListingOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InventoryItems_TenantId_Id",
                table: "InventoryItems");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_TenantId_CreatedAt_Id",
                table: "InventoryItems",
                columns: new[] { "TenantId", "CreatedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InventoryItems_TenantId_CreatedAt_Id",
                table: "InventoryItems");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_TenantId_Id",
                table: "InventoryItems",
                columns: new[] { "TenantId", "Id" });
        }
    }
}
