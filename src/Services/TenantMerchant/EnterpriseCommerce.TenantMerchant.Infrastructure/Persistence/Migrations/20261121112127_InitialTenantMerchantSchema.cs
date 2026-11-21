using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseCommerce.TenantMerchant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialTenantMerchantSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Slug = table.Column<string>(type: "varchar(63)", unicode: false, maxLength: 63, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ProvisionedByClientId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ProvisioningKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ProvisioningRequestHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    ProvisionedByActor = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                    table.CheckConstraint("CK_Tenants_Status", "[Status] IN (N'Active')");
                });

            migrationBuilder.CreateTable(
                name: "Memberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StatusChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Memberships", x => x.Id);
                    table.CheckConstraint("CK_Memberships_Role", "[Role] IN (N'MerchantOwner', N'MerchantAdmin', N'MerchantStaff')");
                    table.CheckConstraint("CK_Memberships_Status", "[Status] IN (N'Active', N'Suspended', N'Removed')");
                    table.ForeignKey(
                        name: "FK_Memberships_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MerchantProfiles",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantProfiles", x => x.TenantId);
                    table.ForeignKey(
                        name: "FK_MerchantProfiles_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_TenantId_CreatedAt",
                table: "Memberships",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_Memberships_TenantId_SubjectId_Current",
                table: "Memberships",
                columns: new[] { "TenantId", "SubjectId" },
                unique: true,
                filter: "[Status] <> N'Removed'");

            migrationBuilder.CreateIndex(
                name: "UX_Tenants_ProvisionedByClientId_ProvisioningKey",
                table: "Tenants",
                columns: new[] { "ProvisionedByClientId", "ProvisioningKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Tenants_Slug",
                table: "Tenants",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Memberships");

            migrationBuilder.DropTable(
                name: "MerchantProfiles");

            migrationBuilder.DropTable(
                name: "Tenants");
        }
    }
}
