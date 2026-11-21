using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseCommerce.TenantMerchant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MerchantMembershipAdministration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MembershipAuditRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ActorKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    MembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InvitationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SubjectId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    PreviousRole = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    NewRole = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    PreviousStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    NewStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MembershipAuditRecords", x => x.Id);
                    table.CheckConstraint("CK_MembershipAuditRecords_Action", "[Action] IN (N'OwnerMembershipProvisioned', N'InvitationCreated', N'InvitationCancelled', N'InvitationAccepted', N'InvitationExpired', N'MembershipSuspended', N'MembershipReactivated', N'MembershipRemoved', N'MembershipRoleChanged')");
                    table.CheckConstraint("CK_MembershipAuditRecords_ActorKind", "[ActorKind] IN (N'User', N'Service', N'System')");
                    table.ForeignKey(
                        name: "FK_MembershipAuditRecords_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MerchantInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    InvitedBySubjectId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AcceptedBySubjectId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    AcceptedMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledBySubjectId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ExpiredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TokenHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantInvitations", x => x.Id);
                    table.CheckConstraint("CK_MerchantInvitations_Accepted", "([Status] = N'Accepted' AND [AcceptedAt] IS NOT NULL AND [AcceptedBySubjectId] IS NOT NULL AND [AcceptedMembershipId] IS NOT NULL) OR ([Status] <> N'Accepted' AND [AcceptedAt] IS NULL AND [AcceptedBySubjectId] IS NULL AND [AcceptedMembershipId] IS NULL)");
                    table.CheckConstraint("CK_MerchantInvitations_Cancelled", "([Status] = N'Cancelled' AND [CancelledAt] IS NOT NULL AND [CancelledBySubjectId] IS NOT NULL) OR ([Status] <> N'Cancelled' AND [CancelledAt] IS NULL AND [CancelledBySubjectId] IS NULL)");
                    table.CheckConstraint("CK_MerchantInvitations_Expired", "([Status] = N'Expired' AND [ExpiredAt] IS NOT NULL) OR ([Status] <> N'Expired' AND [ExpiredAt] IS NULL)");
                    table.CheckConstraint("CK_MerchantInvitations_Expiry", "[ExpiresAt] > [CreatedAt]");
                    table.CheckConstraint("CK_MerchantInvitations_Role", "[Role] IN (N'MerchantAdmin', N'MerchantStaff')");
                    table.CheckConstraint("CK_MerchantInvitations_Status", "[Status] IN (N'Pending', N'Accepted', N'Cancelled', N'Expired')");
                    table.ForeignKey(
                        name: "FK_MerchantInvitations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_SubjectId_Status",
                table: "Memberships",
                columns: new[] { "SubjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_TenantId_ActiveOwner",
                table: "Memberships",
                column: "TenantId",
                filter: "[Role] = N'MerchantOwner' AND [Status] = N'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_MembershipAuditRecords_TenantId_OccurredAt",
                table: "MembershipAuditRecords",
                columns: new[] { "TenantId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MerchantInvitations_TenantId_CreatedAt",
                table: "MerchantInvitations",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_MerchantInvitations_TenantId_NormalizedEmail_Pending",
                table: "MerchantInvitations",
                columns: new[] { "TenantId", "NormalizedEmail" },
                unique: true,
                filter: "[Status] = N'Pending'");

            migrationBuilder.CreateIndex(
                name: "UX_MerchantInvitations_TokenHash",
                table: "MerchantInvitations",
                column: "TokenHash",
                unique: true);

            // ADR-021 backstop: no statement may leave a tenant that had an active owner without one. The application
            // serializes membership changes per tenant and never relies on this; it stops any path that bypasses that.
            // READCOMMITTEDLOCK keeps the check a locking read even if row versioning is ever enabled.
            migrationBuilder.Sql("""
                CREATE TRIGGER [dbo].[TR_Memberships_RequireActiveOwner]
                ON [dbo].[Memberships]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT 1
                        FROM deleted AS d
                        WHERE d.[Role] = N'MerchantOwner'
                          AND d.[Status] = N'Active'
                          AND NOT EXISTS (
                              SELECT 1
                              FROM [dbo].[Memberships] AS m WITH (READCOMMITTEDLOCK)
                              WHERE m.[TenantId] = d.[TenantId]
                                AND m.[Role] = N'MerchantOwner'
                                AND m.[Status] = N'Active'))
                        THROW 50001, N'A tenant must keep at least one active MerchantOwner membership.', 1;
                END
                """);

            // ADR-021: audit records are append-only.
            migrationBuilder.Sql("""
                CREATE TRIGGER [dbo].[TR_MembershipAuditRecords_AppendOnly]
                ON [dbo].[MembershipAuditRecords]
                INSTEAD OF UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 50002, N'Membership audit records are append-only.', 1;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER [dbo].[TR_Memberships_RequireActiveOwner];");

            migrationBuilder.DropTable(
                name: "MembershipAuditRecords");

            migrationBuilder.DropTable(
                name: "MerchantInvitations");

            migrationBuilder.DropIndex(
                name: "IX_Memberships_SubjectId_Status",
                table: "Memberships");

            migrationBuilder.DropIndex(
                name: "IX_Memberships_TenantId_ActiveOwner",
                table: "Memberships");
        }
    }
}
