using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseCommerce.TenantMerchant.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Phase 3 S3: self-service onboarding. Adds the tenant origin and the creating subject, splits the idempotency
    /// key index into a per-client (provisioned) and a per-subject (self-service) namespace, and allows the new audit
    /// action. Every existing tenant was provisioned, and the column default fills them with that origin.
    /// </summary>
    public partial class SelfServiceOnboarding : Migration
    {
        /// <summary>The error Down raises instead of discarding self-service data.</summary>
        public const int SelfServiceDataExistsError = 50003;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Tenants_ProvisionedByClientId_ProvisioningKey",
                table: "Tenants");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MembershipAuditRecords_Action",
                table: "MembershipAuditRecords");

            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "Tenants",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Provisioned");

            migrationBuilder.AddColumn<string>(
                name: "SelfServiceSubjectId",
                table: "Tenants",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_Tenants_ProvisionedByClientId_ProvisioningKey",
                table: "Tenants",
                columns: new[] { "ProvisionedByClientId", "ProvisioningKey" },
                unique: true,
                filter: "[Origin] = N'Provisioned'");

            migrationBuilder.CreateIndex(
                name: "UX_Tenants_SelfServiceSubjectId_ProvisioningKey",
                table: "Tenants",
                columns: new[] { "SelfServiceSubjectId", "ProvisioningKey" },
                unique: true,
                filter: "[Origin] = N'SelfService'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tenants_Origin",
                table: "Tenants",
                sql: "[Origin] IN (N'Provisioned', N'SelfService')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tenants_Origin_SelfServiceSubject",
                table: "Tenants",
                sql: "([Origin] = N'SelfService' AND [SelfServiceSubjectId] IS NOT NULL) OR ([Origin] = N'Provisioned' AND [SelfServiceSubjectId] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MembershipAuditRecords_Action",
                table: "MembershipAuditRecords",
                sql: "[Action] IN (N'OwnerMembershipProvisioned', N'InvitationCreated', N'InvitationCancelled', N'InvitationAccepted', N'InvitationExpired', N'MembershipSuspended', N'MembershipReactivated', N'MembershipRemoved', N'MembershipRoleChanged', N'SelfServiceOwnerMembershipCreated')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Down is a development rollback. With self-service tenants it would lose their origin, break the old
            // per-client key index, and meet audit records it may not delete (append-only): it refuses instead.
            migrationBuilder.Sql(
                "IF EXISTS (SELECT 1 FROM [Tenants] WHERE [Origin] = N'SelfService') "
                + $"THROW {SelfServiceDataExistsError}, N'Self-service tenants exist: the SelfServiceOnboarding migration cannot be reverted without losing them.', 1;");

            migrationBuilder.DropIndex(
                name: "UX_Tenants_ProvisionedByClientId_ProvisioningKey",
                table: "Tenants");

            migrationBuilder.DropIndex(
                name: "UX_Tenants_SelfServiceSubjectId_ProvisioningKey",
                table: "Tenants");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Tenants_Origin",
                table: "Tenants");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Tenants_Origin_SelfServiceSubject",
                table: "Tenants");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MembershipAuditRecords_Action",
                table: "MembershipAuditRecords");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SelfServiceSubjectId",
                table: "Tenants");

            migrationBuilder.CreateIndex(
                name: "UX_Tenants_ProvisionedByClientId_ProvisioningKey",
                table: "Tenants",
                columns: new[] { "ProvisionedByClientId", "ProvisioningKey" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_MembershipAuditRecords_Action",
                table: "MembershipAuditRecords",
                sql: "[Action] IN (N'OwnerMembershipProvisioned', N'InvitationCreated', N'InvitationCancelled', N'InvitationAccepted', N'InvitationExpired', N'MembershipSuspended', N'MembershipReactivated', N'MembershipRemoved', N'MembershipRoleChanged')");
        }
    }
}
