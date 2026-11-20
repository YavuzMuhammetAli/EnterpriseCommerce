using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.RegularExpressions;
using EnterpriseCommerce.Identity.Application.Diagnostics;
using EnterpriseCommerce.Identity.Infrastructure.Retention;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.Identity.IntegrationTests.Protocol;

/// <summary>
/// Identity's operational metrics (Phase 2.3 S6.6): the security outcomes are counted, and every tag has a bounded value
/// set: no user, session, client or delivery identifier, email address or token ever becomes a label.
/// </summary>
/// <remarks>
/// The listener sees every measurement of the meter in the test process. Tests that run in parallel record their own
/// test-only operation names (for example <c>test-deadlock-exhausted-{guid}</c>), so this test runs alone.
/// </remarks>
[Collection(nameof(MetricsCollection))]
public sealed partial class MetricsTests(IdentityServerFixture identity) : IClassFixture<IdentityServerFixture>
{
    private static readonly HashSet<string> AllowedTagKeys = new(StringComparer.Ordinal)
    {
        "operation", "reason", "outcome", "category", "grant_type", "purpose", "source", "action", "use", "state",
    };

    [Fact]
    public async Task SecurityOutcomes_AreCounted_WithBoundedTagsOnly()
    {
        identity.RequireAvailable();
        using var metrics = new TagCollector(IdentityMetrics.MeterName);
        var email = await identity.CreateUserAsync();
        var userId = await identity.GetUserIdAsync(email);

        Assert.False(await identity.PasswordWorksAsync(email, "Wrong-Password-For-Metrics-1"));
        var session = await identity.SignInAsync(identity.Storefront, email);
        var rotated = await identity.RefreshAsync(identity.Storefront, session.RefreshToken!);
        Assert.Equal(HttpStatusCode.OK, rotated.Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await identity.RefreshAsync(identity.Storefront, session.RefreshToken!)).Status);
        Assert.Equal(HttpStatusCode.OK, (await identity.DisableThroughApiAsync(userId)).Status);
        Assert.Equal(HttpStatusCode.OK, (await identity.ReactivateThroughApiAsync(userId)).Status);
        await identity.Services.GetRequiredService<IdentityRetentionRunner>().RunOnceAsync(TestContext.Current.CancellationToken);
        metrics.RecordObservables();

        Assert.True(metrics.Has("identity.authentication.attempts", ("outcome", "failed")));
        Assert.True(metrics.Has("identity.authentication.attempts", ("outcome", "succeeded")));
        Assert.True(metrics.Has("identity.token.grants", ("grant_type", "authorization_code"), ("outcome", "issued")));
        Assert.True(metrics.Has("identity.token.grants", ("grant_type", "client_credentials"), ("outcome", "issued")));
        Assert.True(metrics.Has("identity.refresh.reuse_detected", ("action", "revoke_session")));
        Assert.True(metrics.Has("identity.account.lifecycle", ("action", "disable"), ("outcome", "changed")));
        Assert.True(metrics.Has("identity.account.lifecycle", ("action", "reactivate"), ("outcome", "changed")));
        Assert.True(metrics.Has("identity.retention.runs", ("outcome", "completed")));
        Assert.True(metrics.Has("identity.keys.configured", ("use", "signing"), ("state", "active")));

        foreach (var (key, value) in metrics.AllTags())
        {
            Assert.Contains(key, AllowedTagKeys);
            Assert.True(value.Length <= 40, $"{key}={value}");
            Assert.DoesNotContain("@", value, StringComparison.Ordinal);
            Assert.False(Guid.TryParse(value, out _), $"{key}={value}");
            Assert.False(LongHex().IsMatch(value), $"{key}={value}");
            Assert.NotEqual(userId, value);
        }
    }

    [GeneratedRegex("^[0-9A-Fa-f]{16,}$")]
    private static partial Regex LongHex();

    /// <summary>Collects every measurement of one meter with all of its tags.</summary>
    private sealed class TagCollector : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<(string Instrument, KeyValuePair<string, string>[] Tags)> _measurements = new();

        public TagCollector(string meterName)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == meterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => Add(instrument, tags));
            _listener.SetMeasurementEventCallback<int>((instrument, _, tags, _) => Add(instrument, tags));
            _listener.Start();
        }

        public void RecordObservables() => _listener.RecordObservableInstruments();

        public bool Has(string instrument, params (string Key, string Value)[] tags) =>
            _measurements.Any(m => m.Instrument == instrument
                && tags.All(tag => m.Tags.Any(t => t.Key == tag.Key && t.Value == tag.Value)));

        public IEnumerable<(string Key, string Value)> AllTags() =>
            _measurements.SelectMany(m => m.Tags).Select(t => (t.Key, t.Value)).Distinct();

        public void Dispose() => _listener.Dispose();

        private void Add(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var copy = new KeyValuePair<string, string>[tags.Length];
            for (var index = 0; index < tags.Length; index++)
            {
                copy[index] = new(tags[index].Key, tags[index].Value?.ToString() ?? string.Empty);
            }

            _measurements.Enqueue((instrument.Name, copy));
        }
    }
}

/// <summary>Runs the metrics test without other tests in parallel: its listener is process-wide.</summary>
[CollectionDefinition(nameof(MetricsCollection), DisableParallelization = true)]
public sealed class MetricsCollection;
