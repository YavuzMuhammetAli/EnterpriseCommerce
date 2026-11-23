using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseCommerce.TenantMerchant.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Phase 3 S4: the transactional outbox of ADR-023 section 5. Rows are written in the business transaction and
    /// published by the relay after the commit. No foreign key: the table is technical.
    /// </summary>
    public partial class IntegrationEventOutbox : Migration
    {
        /// <summary>The error Down raises instead of discarding committed, unpublished events.</summary>
        public const int PendingEventsExistError = 50004;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Topic = table.Column<string>(type: "varchar(249)", unicode: false, maxLength: 249, nullable: false),
                    MessageKey = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    EventType = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    EventVersion = table.Column<int>(type: "int", nullable: false),
                    Envelope = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Sequence)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_OutboxMessages_Attempts", "[Attempts] >= 0");
                    table.CheckConstraint("CK_OutboxMessages_EnvelopeSize", "DATALENGTH(CAST([Envelope] COLLATE Latin1_General_100_BIN2_UTF8 AS varchar(max))) <= 65536");
                    table.CheckConstraint("CK_OutboxMessages_EventVersion", "[EventVersion] > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Pending",
                table: "OutboxMessages",
                column: "Sequence",
                filter: "[PublishedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Published",
                table: "OutboxMessages",
                column: "PublishedAt",
                filter: "[PublishedAt] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_OutboxMessages_EventId_EventVersion",
                table: "OutboxMessages",
                columns: new[] { "EventId", "EventVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Down is a development rollback. A pending row is a committed fact that was never published, and is never
            // deleted (ADR-023 section 5): Down refuses instead. Published rows are history and may go.
            migrationBuilder.Sql(
                "IF EXISTS (SELECT 1 FROM [OutboxMessages] WHERE [PublishedAt] IS NULL) "
                + $"THROW {PendingEventsExistError}, N'OutboxMessages holds unpublished events; publish them before rolling back.', 1;");

            migrationBuilder.DropTable(
                name: "OutboxMessages");
        }
    }
}
