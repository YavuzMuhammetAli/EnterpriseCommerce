using System.Reflection;

namespace EnterpriseCommerce.ArchitectureTests;

/// <summary>
/// The messaging and outbox boundaries of ADR-023 (replacing the S1 to S3 deferral rules of ADR-012):
/// <list type="bullet">
/// <item>Confluent.Kafka is the only Kafka client, and only allow-listed Infrastructure projects use it (ADR-005: Kafka
/// client code lives only in Infrastructure layers). A service is added to the allow-list when its own design
/// introduces messaging, so that every new Kafka user is a reviewed decision.</item>
/// <item>No messaging framework, Schema Registry, Avro or CloudEvents package (ADR-023, alternatives considered).</item>
/// <item>Outbox, relay, envelope and integration-event types exist only in an allow-listed Infrastructure assembly;
/// Domain and Application stay free of them (ADR-023 section 5).</item>
/// </list>
/// Domain and Application are also covered by the general infrastructure-technology rules of
/// <see cref="DependencyRuleTests"/> and <see cref="CompiledDependencyTests"/>.
/// </summary>
public sealed class MessagingBoundaryTests
{
    private const string KafkaClientPackage = "Confluent.Kafka";

    /// <summary>The projects whose design (ADR-023, Phase 3 S4) introduces Kafka publication.</summary>
    private static readonly HashSet<string> KafkaProjects = new(StringComparer.Ordinal)
    {
        "EnterpriseCommerce.TenantMerchant.Infrastructure",
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
            declaresKafka && !IsAllowListedInfrastructure(project),
            $"{projectName} declares {KafkaClientPackage}. Only allow-listed Infrastructure projects may: {string.Join(", ", KafkaProjects)}.");
    }

    [Fact]
    public void KafkaAllowList_NamesOnlyExistingInfrastructureProjects()
    {
        // A stale or mistyped entry would silently allow nothing, or the wrong layer.
        foreach (var name in KafkaProjects)
        {
            var project = Repository.SourceProjects.SingleOrDefault(candidate => candidate.Name == name);
            Assert.True(project is not null, $"The Kafka allow-list names {name}, which is not a project under src/.");
            Assert.True(IsInfrastructureLayer(project), $"The Kafka allow-list names {name}, which is not an Infrastructure layer project.");
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
            referencesKafka && !KafkaProjects.Contains(assemblyName),
            $"{assemblyName} references {KafkaClientPackage}. Kafka client code lives only in allow-listed Infrastructure assemblies (ADR-005, ADR-023).");
    }

    [Theory]
    [MemberData(nameof(CompiledDependencyTests.ServiceAssemblies), MemberType = typeof(CompiledDependencyTests))]
    public void MessagingTypes_ExistOnlyInAllowListedInfrastructureAssemblies(string assemblyName)
    {
        if (KafkaProjects.Contains(assemblyName))
        {
            return;
        }

        var violations = Assembly.Load(assemblyName).GetTypes()
            .Where(type => MessagingTypeWords.Any(word => type.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Select(type => type.FullName)
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"{assemblyName} contains messaging types, which belong only in an allow-listed Infrastructure assembly: {string.Join(", ", violations)}");
    }

    private static bool IsKafkaClient(string package) => string.Equals(package, KafkaClientPackage, StringComparison.OrdinalIgnoreCase);

    private static bool IsInfrastructureLayer(SourceProject project) =>
        project is { Kind: ProjectKind.ServiceLayer, Layer: "Infrastructure" };

    private static bool IsAllowListedInfrastructure(SourceProject project) =>
        IsInfrastructureLayer(project) && KafkaProjects.Contains(project.Name);
}
