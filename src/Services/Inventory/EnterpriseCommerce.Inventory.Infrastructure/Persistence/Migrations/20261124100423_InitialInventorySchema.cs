using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseCommerce.Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialInventorySchema : Migration
    {
        /// <summary>The error Down raises instead of discarding inventory, its history or unpublished events.</summary>
        public const int InventoryDataExistsError = 50100;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventoryItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VariantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sku = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    OnHand = table.Column<int>(type: "int", nullable: false),
                    Reserved = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBySubject = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CreationKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreationRequestHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryItems", x => x.Id);
                    table.CheckConstraint("CK_InventoryItems_OnHand", "[OnHand] >= 0 AND [OnHand] <= 1000000000");
                    table.CheckConstraint("CK_InventoryItems_Reserved", "[Reserved] >= 0 AND [Reserved] <= [OnHand]");
                    table.CheckConstraint("CK_InventoryItems_Version", "[Version] >= 1");
                });

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

            migrationBuilder.CreateTable(
                name: "Reservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerClientId = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RequestHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    Reference = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reservations", x => x.Id);
                    table.CheckConstraint("CK_Reservations_Completion", "([Status] = N'Active' AND [CompletedAt] IS NULL) OR ([Status] <> N'Active' AND [CompletedAt] IS NOT NULL)");
                    table.CheckConstraint("CK_Reservations_Expiry", "[ExpiresAt] > [CreatedAt]");
                    table.CheckConstraint("CK_Reservations_Status", "[Status] IN (N'Active', N'Committed', N'Released', N'Expired')");
                    table.CheckConstraint("CK_Reservations_Version", "[Version] >= 1");
                });

            migrationBuilder.CreateTable(
                name: "ReservationLines",
                columns: table => new
                {
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InventoryItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VariantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReservationLines", x => new { x.ReservationId, x.InventoryItemId });
                    table.CheckConstraint("CK_ReservationLines_Quantity", "[Quantity] >= 1 AND [Quantity] <= 1000000");
                    table.ForeignKey(
                        name: "FK_ReservationLines_InventoryItems_InventoryItemId",
                        column: x => x.InventoryItemId,
                        principalTable: "InventoryItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReservationLines_Reservations_ReservationId",
                        column: x => x.ReservationId,
                        principalTable: "Reservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockMovements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InventoryItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemVersion = table.Column<long>(type: "bigint", nullable: false),
                    Type = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    OnHandDelta = table.Column<int>(type: "int", nullable: false),
                    ReservedDelta = table.Column<int>(type: "int", nullable: false),
                    OnHandBefore = table.Column<int>(type: "int", nullable: false),
                    ReservedBefore = table.Column<int>(type: "int", nullable: false),
                    OnHandAfter = table.Column<int>(type: "int", nullable: false),
                    ReservedAfter = table.Column<int>(type: "int", nullable: false),
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorKind = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RequestHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockMovements", x => x.Id);
                    table.CheckConstraint("CK_StockMovements_ActorKind", "[ActorKind] IN (N'User', N'Service', N'System')");
                    table.CheckConstraint("CK_StockMovements_Balance", "[OnHandAfter] = [OnHandBefore] + [OnHandDelta] AND [ReservedAfter] = [ReservedBefore] + [ReservedDelta] AND [OnHandAfter] >= 0 AND [OnHandAfter] <= 1000000000 AND [ReservedAfter] >= 0 AND [ReservedAfter] <= [OnHandAfter]");
                    table.CheckConstraint("CK_StockMovements_ItemVersion", "[ItemVersion] >= 1");
                    table.CheckConstraint("CK_StockMovements_Reason", "[Reason] IS NULL OR [Reason] IN (N'Receipt', N'Return', N'Damage', N'Loss', N'Correction', N'CycleCount')");
                    table.CheckConstraint("CK_StockMovements_Shape", "([Type] = N'Adjustment' AND [Reason] IS NOT NULL AND [IdempotencyKey] IS NOT NULL AND [RequestHash] IS NOT NULL AND [ReservationId] IS NULL) OR ([Type] = N'InitialStock' AND [Reason] IS NULL AND [IdempotencyKey] IS NULL AND [ReservationId] IS NULL) OR ([Type] IN (N'Reservation', N'ReservationRelease', N'ReservationExpiry', N'ReservationCommit') AND [Reason] IS NULL AND [IdempotencyKey] IS NULL AND [ReservationId] IS NOT NULL)");
                    table.CheckConstraint("CK_StockMovements_Type", "[Type] IN (N'InitialStock', N'Adjustment', N'Reservation', N'ReservationRelease', N'ReservationExpiry', N'ReservationCommit')");
                    table.ForeignKey(
                        name: "FK_StockMovements_InventoryItems_InventoryItemId",
                        column: x => x.InventoryItemId,
                        principalTable: "InventoryItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockMovements_Reservations_ReservationId",
                        column: x => x.ReservationId,
                        principalTable: "Reservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_TenantId_Id",
                table: "InventoryItems",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_TenantId_ProductId",
                table: "InventoryItems",
                columns: new[] { "TenantId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_TenantId_Sku",
                table: "InventoryItems",
                columns: new[] { "TenantId", "Sku" });

            migrationBuilder.CreateIndex(
                name: "UX_InventoryItems_TenantId_CreatedBySubject_CreationKey",
                table: "InventoryItems",
                columns: new[] { "TenantId", "CreatedBySubject", "CreationKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_InventoryItems_TenantId_VariantId",
                table: "InventoryItems",
                columns: new[] { "TenantId", "VariantId" },
                unique: true);

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

            migrationBuilder.CreateIndex(
                name: "IX_ReservationLines_InventoryItemId",
                table: "ReservationLines",
                column: "InventoryItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_Active_ExpiresAt",
                table: "Reservations",
                column: "ExpiresAt",
                filter: "[Status] = 'Active'");

            migrationBuilder.CreateIndex(
                name: "UX_Reservations_OwnerClientId_TenantId_IdempotencyKey",
                table: "Reservations",
                columns: new[] { "OwnerClientId", "TenantId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_ReservationId",
                table: "StockMovements",
                column: "ReservationId",
                filter: "[ReservationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_StockMovements_InventoryItemId_ItemVersion",
                table: "StockMovements",
                columns: new[] { "InventoryItemId", "ItemVersion" },
                unique: true)
                .Annotation("SqlServer:Include", new[] { "TenantId" });

            migrationBuilder.CreateIndex(
                name: "UX_StockMovements_TenantId_ActorId_IdempotencyKey",
                table: "StockMovements",
                columns: new[] { "TenantId", "ActorId", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            // The ledger is append-only and reservation lines never change (ADR-026 sections 5 and 6). The application
            // never relies on these triggers; they stop a path that bypasses its rules.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER [dbo].[TR_StockMovements_AppendOnly]
                ON [dbo].[StockMovements]
                INSTEAD OF UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 50101, N'Stock movements are append-only.', 1;
                END
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER [dbo].[TR_ReservationLines_Immutable]
                ON [dbo].[ReservationLines]
                INSTEAD OF UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 50102, N'Reservation lines are immutable.', 1;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Down is a development rollback of an empty database only. Stock balances, their movement history,
            // reservations and committed but unpublished events are never discarded by a migration (ADR-026, ADR-023
            // section 5): Down refuses instead.
            migrationBuilder.Sql(
                "IF EXISTS (SELECT 1 FROM [InventoryItems]) OR EXISTS (SELECT 1 FROM [Reservations]) OR EXISTS (SELECT 1 FROM [OutboxMessages] WHERE [PublishedAt] IS NULL) "
                + $"THROW {InventoryDataExistsError}, N'The Inventory database holds stock, history or unpublished events; it is never rolled back by a migration.', 1;");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropTable(
                name: "ReservationLines");

            migrationBuilder.DropTable(
                name: "StockMovements");

            migrationBuilder.DropTable(
                name: "InventoryItems");

            migrationBuilder.DropTable(
                name: "Reservations");
        }
    }
}
