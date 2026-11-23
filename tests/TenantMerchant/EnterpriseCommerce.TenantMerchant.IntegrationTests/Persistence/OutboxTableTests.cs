using EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;
using EnterpriseCommerce.TenantMerchant.Infrastructure.Messaging;
using Microsoft.Data.SqlClient;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Persistence;

/// <summary>
/// The constraints of <c>OutboxMessages</c> on a real SQL Server (ADR-023 sections 3 and 5): one row per fact and
/// version, which allows a dual publication of one fact, and the 64 KiB UTF-8 envelope limit.
/// </summary>
public sealed class OutboxTableTests(SqlServerTestDatabase database) : IClassFixture<SqlServerTestDatabase>
{
    [Fact]
    public async Task TwoVersionsOfOneFact_AreAccepted_AndTheSameVersionTwiceIsRefused()
    {
        database.RequireAvailable();
        var eventId = Guid.CreateVersion7();
        var key = Guid.CreateVersion7().ToString("D");

        var v1 = await OutboxRows.InsertAsync(database, eventId, 1, key, publishedVersions: [1, 2]);
        var v2 = await OutboxRows.InsertAsync(database, eventId, 2, key, publishedVersions: [1, 2]);

        Assert.True(v2 > v1);
        var duplicate = await Assert.ThrowsAsync<SqlException>(() => OutboxRows.InsertAsync(database, eventId, 1, key, publishedVersions: [1, 2]));
        Assert.Contains(duplicate.Number, new[] { 2601, 2627 });
        Assert.Contains("UX_OutboxMessages_EventId_EventVersion", duplicate.Message, StringComparison.Ordinal);
        Assert.Equal(2, await database.ScalarAsync<int>($"SELECT COUNT(*) FROM OutboxMessages WHERE EventId = '{eventId}'"));
    }

    [Fact]
    public async Task TheEnvelopeLimit_CountsUtf8Bytes()
    {
        database.RequireAvailable();

        // '€' is 3 bytes in UTF-8 and 2 in UTF-16, so only a UTF-8 count refuses the second value.
        var exactly = new string('€', 21845) + "a";
        var oneOver = new string('€', 21845) + "aa";

        await OutboxRows.InsertAsync(database, Guid.CreateVersion7(), 1, envelope: exactly);
        var refusal = await Assert.ThrowsAsync<SqlException>(() => OutboxRows.InsertAsync(database, Guid.CreateVersion7(), 1, envelope: oneOver));

        Assert.Equal(547, refusal.Number);
        Assert.Contains(EnterpriseCommerce.TenantMerchant.Infrastructure.Persistence.TenantMerchantDbContext.OutboxEnvelopeSizeConstraint, refusal.Message, StringComparison.Ordinal);
        Assert.Equal(OutboxMessage.EnvelopeMaxBytes, System.Text.Encoding.UTF8.GetByteCount(exactly));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ANonPositiveVersion_IsRefused(int version)
    {
        database.RequireAvailable();

        var refusal = await Assert.ThrowsAsync<SqlException>(() => OutboxRows.InsertAsync(database, Guid.CreateVersion7(), version, envelope: "{}"));

        Assert.Equal(547, refusal.Number);
    }
}
