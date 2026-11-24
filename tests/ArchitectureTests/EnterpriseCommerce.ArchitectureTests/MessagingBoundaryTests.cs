using System.Reflection;

namespace EnterpriseCommerce.ArchitectureTests;

/// <summary>
/// The messaging and outbox boundaries of ADR-023 (replacing the S1 to S3 deferral rules of ADR-012), with the shared
/// block of ADR-028 section 4:
/// <list type="bullet">
/// <item>Confluent.Kafka is the only Kafka client, and only <c>EnterpriseCommerce.BuildingBlocks.Messaging</c> declares
/// it. No service assembly references it: Kafka code lives only in that block (ADR-005, ADR-028).</item>
/// <item>The messaging block is referenced only by Infrastructure projects (ADR-028 section 4).</item>
/// <item>No messaging framework, Schema Registry, Avro or CloudEvents package (ADR-023, alternatives considered).</item>
/// <item>Outbox, relay, envelope and integration-event types exist only in an allow-listed publishing service's
/// Infrastructure assembly (its mapper) and in the block; Domain and Application stay free of them (ADR-023 section 5).
/// A service is added to the publisher allow-list when its own design introduces messaging, so that every new
/// publisher is a reviewed decision.</item>
/// </list>
/// Domain and Application are also covered by the general infrastructure-technology rules of
/// <see cref="DependencyRuleTests"/> and <see cref="CompiledDependencyTests"/>.
/// </summary>
public sealed class MessagingBoundaryTests
{
    private const string KafkaClientPackage = "Confluent.Kafka";

    /// <summary>The shared outbox and relay block (ADR-028 section 4): the only project that declares Confluent.Kafka.</summary>
    private const string MessagingBlock = "EnterpriseCommerce.BuildingBlocks.Messaging";

    /// <summary>The projects that may declare Confluent.Kafka.</summary>
    private static readonly HashSet<string> KafkaProjects = new(StringComparer.Ordinal) { MessagingBlock };

    /// <summary>
    /// The publishing services' Infrastructure projects, whose designs introduce Kafka publication (ADR-023 for Tenant &amp;
    /// Merchant, ADR-028 for Inventory). Only they hold an integration-event mapper.
    /// </summary>
    private static readonly HashSet<string> PublisherProjects = new(StringComparer.Ordinal)
    {
        "EnterpriseCommerce.TenantMerchant.Infrastructure",
        "EnterpriseCommerce.Inventory.Infrastructure",
    };

    /// <summary>
    /// Packages that ADR-023 rejects: messaging frameworks, Schema Registry clients and serializers (any other
    /// <c>Confluent.*</c> package), Avro and the CloudEvents SDK.
    /// </summary>
    private static readonly string[] RejectedPackagePrefixes =
    [
        "MassTransit",
        "NServiceBus",
        "Rebus",
        "Wolverine",
        "WolverineFx",
        "DotNetCore.CAP",
        "KafkaFlow",
        "Confluent.",
        "Apache.Avro",
        "CloudNative.CloudEvents",
    ];

    /// <summary>Type-name words that mark messaging plumbing, which belongs only in an allow-listed Infrastructure assembly.</summary>
    private static readonly string[] MessagingTypeWords = ["Outbox", "IntegrationEvent", "EventEnvelope", "KafkaProducer", "KafkaConsumer"];

    public static TheoryData<string> AllSourceProjects() => [.. Repository.SourceProjects.Select(project => project.Name)];

    [Theory]
    [MemberData(nameof(AllSourceProjects))]
    public void KafkaClient_IsDeclaredOnlyByAllowListedInfrastructureProjects(string projectName)
    {
        var project = Repository.SourceProjects.Single(candidate => candidate.Name == projectName);
        var declaresKafka = project.PackageReferences.Any(IsKafkaClient);

        Assert.False(
            declaresKafka && !KafkaProjects.Contains(project.Name),
            $"{projectName} declares {KafkaClientPackage}. Only the messaging building block may: {string.Join(", ", KafkaProjects)}.");
    }

    [Fact]
    public void AllowLists_NameOnlyExistingProjectsOfTheRightKind()
    {
        // A stale or mistyped entry would silently allow nothing, or the wrong layer.
        var block = Repository.SourceProjects.SingleOrDefault(candidate => candidate.Name == MessagingBlock);
        Assert.True(block is { Kind: ProjectKind.BuildingBlock }, $"{MessagingBlock} is not a building block under src/BuildingBlocks.");

        foreach (var name in PublisherProjects)
        {
            var project = Repository.SourceProjects.SingleOrDefault(candidate => candidate.Name == name);
            Assert.True(project is not null, $"The publisher allow-list names {name}, which is not a project under src/.");
            Assert.True(IsInfrastructureLayer(project), $"The publisher allow-list names {name}, which is not an Infrastructure layer project.");
        }
    }

    /// <summary>
    /// The messaging block brings in EF Core, SQL Server and Kafka: only a service's Infrastructure layer may declare a
    /// reference to it (ADR-028 section 4), never Domain, Application or Api, and never the Gateway or the BFF.
    /// </summary>
    [Fact]
    public void MessagingBuildingBlock_IsReferencedOnlyByInfrastructureProjects()
    {
        var violations = Repository.SourceProjects
            .Where(project => project.ProjectReferences.Contains(MessagingBlock))
            .Where(project => !IsInfrastructureLayer(project))
            .Select(project => project.Name);

        Assert.Empty(violations);
    }

    [Fact]
    public void EveryPublisher_UsesTheMessagingBuildingBlock()
    {
        foreach (var name in PublisherProjects)
        {
            var project = Repository.SourceProjects.Single(candidate => candidate.Name == name);
            Assert.Contains(MessagingBlock, project.ProjectReferences);
        }
    }

    [Theory]
    [MemberData(nameof(AllSourceProjects))]
    public void NoProject_DeclaresARejectedMessagingPackage(string projectName)
    {
        var project = Repository.SourceProjects.Single(candidate => candidate.Name == projectName);

        var rejected = project.PackageReferences
            .Where(package => !IsKafkaClient(package))
            .Where(package => RejectedPackagePrefixes.Any(prefix => package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(rejected.Count == 0, $"{projectName} declares packages rejected by ADR-023: {string.Join(", ", rejected)}");
    }

    [Theory]
    [MemberData(nameof(CompiledDependencyTests.ServiceAssemblies), MemberType = typeof(CompiledDependencyTests))]
    public void KafkaClient_IsReferencedOnlyByAllowListedInfrastructureAssemblies(string assemblyName)
    {
        var referencesKafka = Assembly.Load(assemblyName)
            .GetReferencedAssemblies()
            .Any(reference => string.Equals(reference.Name, KafkaClientPackage, StringComparison.Ordinal));

        Assert.False(
            referencesKafka,
            $"{assemblyName} references {KafkaClientPackage}. Kafka client code lives only in the messaging building block (ADR-005, ADR-028).");
    }

    [Theory]
    [MemberData(nameof(CompiledDependencyTests.ServiceAssemblies), MemberType = typeof(CompiledDependencyTests))]
    public void MessagingTypes_ExistOnlyInAllowListedInfrastructureAssemblies(string assemblyName)
    {
        if (PublisherProjects.Contains(assemblyName))
        {
            return;
        }

        var violations = Assembly.Load(assemblyName).GetTypes()
            .Where(type => MessagingTypeWords.Any(word => type.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Select(type => type.FullName)
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"{assemblyName} contains messaging types, which belong only in an allow-listed publisher's Infrastructure assembly or the messaging block: {string.Join(", ", violations)}");
    }

    private static bool IsKafkaClient(string package) => string.Equals(package, KafkaClientPackage, StringComparison.OrdinalIgnoreCase);

    private static bool IsInfrastructureLayer(SourceProject project) =>
        project is { Kind: ProjectKind.ServiceLayer, Layer: "Infrastructure" };
}
