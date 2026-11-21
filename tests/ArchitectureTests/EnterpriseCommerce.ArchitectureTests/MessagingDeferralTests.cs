using System.Reflection;

namespace EnterpriseCommerce.ArchitectureTests;

/// <summary>
/// No messaging or outbox code before its design is approved (ADR-012, PROJECT_DECISIONS section 9). The Kafka client
/// library is not selected, so no service declares a messaging package, and no service type is an outbox or an
/// integration event. When the event-flow phase starts, this test is replaced by the rules of its accepted design.
/// </summary>
public sealed class MessagingDeferralTests
{
    private static readonly string[] MessagingPackagePrefixes =
    [
        "Confluent.Kafka",
        "Confluent.SchemaRegistry",
        "MassTransit",
        "NServiceBus",
        "Rebus",
        "Wolverine",
        "DotNetCore.CAP",
        "KafkaFlow",
    ];

    private static readonly string[] DeferredTypeWords = ["Outbox", "IntegrationEvent", "EventEnvelope", "KafkaProducer", "KafkaConsumer"];

    public static TheoryData<string> AllSourceProjects() => [.. Repository.SourceProjects.Select(project => project.Name)];

    [Theory]
    [MemberData(nameof(AllSourceProjects))]
    public void NoProject_DeclaresAMessagingPackage(string projectName)
    {
        var project = Repository.SourceProjects.Single(candidate => candidate.Name == projectName);

        Assert.DoesNotContain(project.PackageReferences, package =>
            MessagingPackagePrefixes.Any(prefix => package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [MemberData(nameof(CompiledDependencyTests.ServiceAssemblies), MemberType = typeof(CompiledDependencyTests))]
    public void NoServiceType_IsAnOutboxOrAnIntegrationEvent(string assemblyName)
    {
        var violations = Assembly.Load(assemblyName).GetTypes()
            .Where(type => DeferredTypeWords.Any(word => type.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Select(type => type.FullName);

        Assert.Empty(violations);
    }
}
