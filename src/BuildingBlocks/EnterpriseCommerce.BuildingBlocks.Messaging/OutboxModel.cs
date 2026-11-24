using Microsoft.EntityFrameworkCore;

namespace EnterpriseCommerce.BuildingBlocks.Messaging;

/// <summary>
/// The EF Core mapping of the <c>OutboxMessages</c> table (ADR-023 section 5), shared so that every SQL Server publisher
/// has exactly the same table:
/// <list type="bullet">
/// <item>a <c>bigint</c> identity as the clustered key and the publication order</item>
/// <item>the unique (event, version) pair of a dual publication</item>
/// <item>the 64 KiB envelope bound</item>
/// <item>the filtered indexes of the relay's poll and of the cleanup</item>
/// </list>
/// No foreign key: the table is technical. Each service creates it with its own migration.
/// </summary>
public static class OutboxModel
{
    public const string TableName = "OutboxMessages";
    public const string EventVersionIndex = "UX_OutboxMessages_EventId_EventVersion";
    public const string EnvelopeSizeConstraint = "CK_OutboxMessages_EnvelopeSize";
    public const string PendingIndex = "IX_OutboxMessages_Pending";
    public const string PublishedIndex = "IX_OutboxMessages_Published";

    /// <summary>Maps <see cref="OutboxMessage"/> into the service's model.</summary>
    public static ModelBuilder MapOutboxMessages(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        return modelBuilder.Entity<OutboxMessage>(message =>
        {
            message.ToTable(TableName, table =>
            {
                table.HasCheckConstraint("CK_OutboxMessages_EventVersion", "[EventVersion] > 0");
                table.HasCheckConstraint("CK_OutboxMessages_Attempts", "[Attempts] >= 0");

                // ADR-023 section 3: at most 64 KiB as UTF-8. The serializer checks it before the write as well.
                table.HasCheckConstraint(
                    EnvelopeSizeConstraint,
                    $"DATALENGTH(CAST([Envelope] COLLATE Latin1_General_100_BIN2_UTF8 AS varchar(max))) <= {OutboxMessage.EnvelopeMaxBytes}");
            });

            // The identity is the publication order. No foreign key: the table is technical.
            message.HasKey(m => m.Sequence).IsClustered();
            message.Property(m => m.Sequence).UseIdentityColumn();
            message.Property(m => m.Topic).HasMaxLength(OutboxMessage.TopicMaxLength).IsUnicode(false);
            message.Property(m => m.MessageKey).HasMaxLength(OutboxMessage.MessageKeyMaxLength).IsUnicode(false);
            message.Property(m => m.EventType).HasMaxLength(OutboxMessage.EventTypeMaxLength).IsUnicode(false);
            message.Property(m => m.Envelope);
            message.Property(m => m.Attempts).HasDefaultValue(0);
            message.Property(m => m.LastError).HasMaxLength(OutboxMessage.LastErrorMaxLength);

            // One message per (fact, version): a dual publication writes two versions of one event ID (ADR-023 section 3).
            message.HasIndex(m => new { m.EventId, m.EventVersion }).IsUnique().HasDatabaseName(EventVersionIndex);

            // The relay's poll and the cleanup.
            message.HasIndex(m => m.Sequence).HasFilter("[PublishedAt] IS NULL").HasDatabaseName(PendingIndex);
            message.HasIndex(m => m.PublishedAt).HasFilter("[PublishedAt] IS NOT NULL").HasDatabaseName(PublishedIndex);
        });
    }
}
