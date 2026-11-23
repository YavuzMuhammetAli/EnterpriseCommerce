using System.Diagnostics;
using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;

/// <summary>
/// The real Kafka broker of the integration tests (PHASE_3_S4_EVENT_FLOW.md section 8). <c>EC_TEST_KAFKA</c> names its
/// bootstrap servers (for example <c>127.0.0.1:9094</c> with the Compose <c>kafka</c> profile). Every test creates its own
/// topic with AdminClient and deletes it afterwards; the broker never creates one by itself.
/// </summary>
public static class KafkaTestEnvironment
{
    public const string ServerVariable = "EC_TEST_KAFKA";
    public const string RestartContainerVariable = "EC_TEST_KAFKA_RESTART_CONTAINER";

    public static string? BootstrapServers => Environment.GetEnvironmentVariable(ServerVariable) is { Length: > 0 } value ? value : null;

    /// <summary>Skips the calling test without a broker, or fails it when infrastructure tests are required.</summary>
    public static void RequireAvailable()
    {
        if (BootstrapServers is not null)
        {
            return;
        }

        var message = $"Kafka integration test: set {ServerVariable} to the bootstrap servers of a Kafka broker (docker compose --profile kafka).";
        if (string.Equals(Environment.GetEnvironmentVariable(SqlServerTestDatabase.RequireVariable), "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Fail($"{SqlServerTestDatabase.RequireVariable} is set but {ServerVariable} is not. {message}");
        }

        Assert.Skip(message);
    }

    public static async Task<KafkaTestTopic> CreateTopicAsync(int partitions = 3)
    {
        var name = $"test.tenant-merchant.events.{Guid.NewGuid():N}";
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = BootstrapServers }).Build();
        await admin.CreateTopicsAsync([new TopicSpecification { Name = name, NumPartitions = partitions, ReplicationFactor = 1 }]);
        return new KafkaTestTopic(name, partitions);
    }

    /// <summary>Whether the topic exists on the broker (metadata only; nothing is created).</summary>
    public static bool TopicExists(string topic)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = BootstrapServers }).Build();
        var metadata = admin.GetMetadata(topic, TimeSpan.FromSeconds(10));
        return metadata.Topics.Any(candidate => candidate.Topic == topic && candidate.Error.Code == ErrorCode.NoError && candidate.Partitions.Count > 0);
    }

    /// <summary>Runs <c>docker</c> against the broker's container (the outage tests) and asserts success.</summary>
    public static async Task DockerAsync(string command, string container)
    {
        using var process = Process.Start(new ProcessStartInfo("docker", [command, container]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, $"docker {command} {container} failed: {await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken)}");
    }
}

/// <summary>A test's own topic. It reads every partition from the beginning, without a consumer group.</summary>
public sealed class KafkaTestTopic(string name, int partitions) : IAsyncDisposable
{
    public string Name { get; } = name;

    /// <summary>Reads until <paramref name="done"/> holds for everything read so far, or fails at the deadline.</summary>
    public IReadOnlyList<ConsumeResult<string, byte[]>> ReadUntil(Func<IReadOnlyList<ConsumeResult<string, byte[]>>, bool> done, TimeSpan deadline)
    {
        using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = KafkaTestEnvironment.BootstrapServers,
            GroupId = $"test-reader-{Guid.NewGuid():N}",
            EnableAutoCommit = false,
            EnablePartitionEof = false,
        }).Build();
        consumer.Assign(Enumerable.Range(0, partitions).Select(partition => new TopicPartitionOffset(Name, partition, Offset.Beginning)));

        var messages = new List<ConsumeResult<string, byte[]>>();
        var expires = DateTimeOffset.UtcNow + deadline;
        while (!done(messages))
        {
            if (DateTimeOffset.UtcNow >= expires)
            {
                Assert.Fail($"Topic {Name}: the expected messages did not arrive within {deadline}; read {messages.Count}.");
            }

            if (consumer.Consume(TimeSpan.FromMilliseconds(250)) is { Message: not null } result)
            {
                messages.Add(result);
            }
        }

        consumer.Close();
        return messages;
    }

    /// <summary>Reads exactly what arrives until at least <paramref name="count"/> messages are there.</summary>
    public IReadOnlyList<ConsumeResult<string, byte[]>> ReadAtLeast(int count, TimeSpan deadline) => ReadUntil(messages => messages.Count >= count, deadline);

    public async ValueTask DisposeAsync()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = KafkaTestEnvironment.BootstrapServers }).Build();
        try
        {
            await admin.DeleteTopicsAsync([Name]);
        }
        catch (DeleteTopicsException)
        {
            // Best effort: a topic the test could not create, or one already gone.
        }
    }
}
