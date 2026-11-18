using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseCommerce.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityEmailDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SecurityEmailDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    ClaimedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ErrorCategory = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SecurityTokenId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityEmailDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SecurityEmailDeliveries_AccountSecurityTokens_SecurityTokenId",
                        column: x => x.SecurityTokenId,
                        principalTable: "AccountSecurityTokens",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SecurityEmailDeliveries_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEmailDeliveries_SecurityTokenId",
                table: "SecurityEmailDeliveries",
                column: "SecurityTokenId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEmailDeliveries_Status_ClaimedAt",
                table: "SecurityEmailDeliveries",
                columns: new[] { "Status", "ClaimedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEmailDeliveries_Status_CreatedAt",
                table: "SecurityEmailDeliveries",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEmailDeliveries_UserId_Purpose_CreatedAt",
                table: "SecurityEmailDeliveries",
                columns: new[] { "UserId", "Purpose", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SecurityEmailDeliveries");
        }
    }
}
