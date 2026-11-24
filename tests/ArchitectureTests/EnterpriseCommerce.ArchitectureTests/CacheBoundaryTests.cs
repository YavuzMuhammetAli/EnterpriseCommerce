namespace EnterpriseCommerce.ArchitectureTests;

/// <summary>
/// The Redis boundary (ADR-006 rules 2 and 5, ADR-016, ADR-030): the Redis client is declared and compiled only where a
/// reviewed design introduced it. The BFF's session store (ADR-016) and Cart's cache (ADR-030, Infrastructure only) are
/// the two users; a service's Api, Application and Domain never touch the client. Every new Redis user is a reviewed
/// decision that adds itself to the allow-list.
/// </summary>
public sealed class CacheBoundaryTests
{
    private const string RedisPackage = "StackExchange.Redis";

    /// <summary>The projects whose design introduces Redis.</summary>
    private static readonly HashSet<string> RedisProjects = new(StringComparer.Ordinal)
    {
        "EnterpriseCommerce.Bff",
        "EnterpriseCommerce.Cart.Infrastructure",
    };

    public static TheoryData<string> AllSourceProjects() => [.. Repository.SourceProjects.Select(project => project.Name)];

    public static TheoryData<string> ServiceAssemblies() => CompiledDependencyTests.ServiceAssemblies();

    [Theory]
    [MemberData(nameof(AllSourceProjects))]
    public void TheRedisClient_IsDeclaredOnlyByAllowListedProjects(string projectName)
    {
        var project = Repository.SourceProjects.Single(candidate => candidate.Name == projectName);
        var declaresRedis = project.PackageReferences.Any(package =>
            package.StartsWith(RedisPackage, StringComparison.OrdinalIgnoreCase) || package.Contains("Caching.StackExchangeRedis", StringComparison.OrdinalIgnoreCase));

        Assert.False(
            declaresRedis && !RedisProjects.Contains(projectName),
            $"{projectName} declares a Redis package. Only allow-listed projects may: {string.Join(", ", RedisProjects)}.");
    }

    [Theory]
    [MemberData(nameof(ServiceAssemblies))]
    public void TheRedisClient_IsCompiledIntoAllowListedServiceAssembliesOnly(string assemblyName)
    {
        var referencesRedis = CompiledDependencyTests.ReferencedAssemblyNames(assemblyName)
            .Any(name => name.StartsWith(RedisPackage, StringComparison.Ordinal));

        Assert.False(referencesRedis && !RedisProjects.Contains(assemblyName), $"{assemblyName} references the Redis client.");
    }

    [Fact]
    public void TheAllowList_NamesExistingProjects_AndServiceEntriesAreInfrastructure()
    {
        foreach (var name in RedisProjects)
        {
            var project = Assert.Single(Repository.SourceProjects, candidate => candidate.Name == name);
            if (project.Kind == ProjectKind.ServiceLayer)
            {
                Assert.Equal("Infrastructure", project.Layer);
            }
        }
    }

    [Fact]
    public void Cart_ReferencesNoCatalogOrInventoryProject()
    {
        // ADR-029 section 5 and section 7: Cart reaches Catalog through its public HTTP read and never calls Inventory.
        var violations = Repository.SourceProjects
            .Where(project => project.Service == "Cart")
            .SelectMany(project => project.ProjectReferences.Select(reference => (project.Name, Reference: reference)))
            .Where(pair => pair.Reference.Contains("Catalog", StringComparison.Ordinal) || pair.Reference.Contains("Inventory", StringComparison.Ordinal))
            .Select(pair => $"{pair.Name} -> {pair.Reference}");

        Assert.Empty(violations);
    }
}
