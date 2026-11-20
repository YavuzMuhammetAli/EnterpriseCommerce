using System.Security.Cryptography;
using EnterpriseCommerce.Identity.Api.Diagnostics;
using EnterpriseCommerce.Identity.Api.Protocol.Keys;
using EnterpriseCommerce.Identity.Infrastructure.Diagnostics;
using EnterpriseCommerce.Identity.Infrastructure.Retention;
using EnterpriseCommerce.Identity.Infrastructure.SecurityEmails;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseCommerce.Identity.UnitTests.Api;

/// <summary>Identity's readiness checks (Phase 2.3 S6.5).</summary>
public sealed class HealthCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(400, HealthStatus.Healthy)]
    [InlineData(10, HealthStatus.Degraded)]
    [InlineData(-1, HealthStatus.Unhealthy)]
    public async Task SigningKeys_ReportTheActiveKeyExpiry(int daysLeft, HealthStatus expected)
    {
        var ring = Ring(Now.AddDays(daysLeft));

        var result = await new SigningKeysHealthCheck(ring, new ManualTime(Now)).CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task EphemeralDevelopmentKeys_AreHealthy()
    {
        var ring = new KeyRing([Key(KeyUse.Signing, null), Key(KeyUse.Encryption, null)], [], isEphemeral: true);

        var result = await new SigningKeysHealthCheck(ring, new ManualTime(Now)).CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task BackgroundWorkers_AreDegradedNeverUnhealthy_WhenTheyMissThreeIntervals()
    {
        var time = new ManualTime(Now);
        var heartbeats = new BackgroundWorkerHeartbeats(time);
        var retention = Options.Create(new IdentityRetentionOptions { InitialDelay = TimeSpan.FromMinutes(5), Interval = TimeSpan.FromHours(1) });
        var email = Options.Create(new SecurityEmailDeliveryOptions { ReconcileInterval = TimeSpan.FromMinutes(5) });
        var check = new BackgroundWorkersHealthCheck(heartbeats, retention, email, time);

        heartbeats.SecurityEmailReconciliation.Record(succeeded: true, time.GetUtcNow());
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status);

        // The reconciler last succeeded 16 minutes ago (more than three 5-minute intervals).
        time.Advance(TimeSpan.FromMinutes(16));
        heartbeats.Retention.Record(succeeded: true, time.GetUtcNow());
        Assert.Equal(HealthStatus.Degraded, (await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status);

        heartbeats.SecurityEmailReconciliation.Record(succeeded: true, time.GetUtcNow());
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status);

        // Failed runs do not count as progress: retention fails for more than three hours.
        time.Advance(TimeSpan.FromHours(3.5));
        heartbeats.SecurityEmailReconciliation.Record(succeeded: true, time.GetUtcNow());
        heartbeats.Retention.Record(succeeded: false, time.GetUtcNow());
        Assert.Equal(HealthStatus.Degraded, (await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task ADisabledRetentionWorker_IsNotReportedAsOverdue()
    {
        var time = new ManualTime(Now);
        var heartbeats = new BackgroundWorkerHeartbeats(time);
        var check = new BackgroundWorkersHealthCheck(
            heartbeats,
            Options.Create(new IdentityRetentionOptions { Enabled = false }),
            Options.Create(new SecurityEmailDeliveryOptions()),
            time);

        time.Advance(TimeSpan.FromDays(1));
        heartbeats.SecurityEmailReconciliation.Record(succeeded: true, time.GetUtcNow());

        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status);
    }

    private static KeyRing Ring(DateTimeOffset notAfter) =>
        new([Key(KeyUse.Signing, notAfter), Key(KeyUse.Encryption, Now.AddYears(1))], [], isEphemeral: false);

    private static RingKey Key(KeyUse use, DateTimeOffset? notAfter) =>
        new(Guid.NewGuid().ToString("N"), use, KeyStatus.Active, new RsaSecurityKey(RSA.Create(2048)), Now.AddYears(-1), notAfter, null);
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
