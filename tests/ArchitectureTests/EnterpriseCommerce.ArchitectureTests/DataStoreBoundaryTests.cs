namespace EnterpriseCommerce.ArchitectureTests;

/// <summary>
/// The MongoDB boundary (ADR-004 rule 5, ADR-011, ADR-024 section 1): Catalog is the only MongoDB service, and its
/// driver is declared and used only by Catalog's Infrastructure project. Its Api, Application and Domain never touch
/// the driver; the host reaches MongoDB only through Infrastructure registrations. Every new MongoDB user is a reviewed
/// decision that adds itself to the allow-list.
/// </summary>
public sealed class DataStoreBoundaryTests
{
    private const string MongoPackagePrefix = "MongoDB.";

    /// <summary>The projects whose design introduces MongoDB (ADR-024).</summary>
    private static readonly HashSet<string> MongoProjects = new(StringComparer.Ordinal)
    {
        "EnterpriseCommerce.Catalog.Infrastructure",
    };

    public static TheoryData<string> AllSourceProjects() => [.. Repository.SourceProjects.Select(project => project.Name)];

    public static TheoryData<string> ServiceAssemblies() => CompiledDependencyTests.ServiceAssemblies();

    [Theory]
    [MemberData(nameof(AllSourceProjects))]
    public void MongoDriver_IsDeclaredOnlyByAllowListedInfrastructureProjects(string projectName)
    {
        var project = Repository.SourceProjects.Single(candidate => candidate.Name == projectName);
        var declaresMongo = project.PackageReferences.Any(package => package.StartsWith(MongoPackagePrefix, StringComparison.OrdinalIgnoreCase));

        Assert.False(
            declaresMongo && !MongoProjects.Contains(projectName),
            $"{projectName} declares a MongoDB package. Only allow-listed Infrastructure projects may: {string.Join(", ", MongoProjects)}.");
    }

    [Theory]
    [MemberData(nameof(ServiceAssemblies))]
    public void MongoDriver_IsCompiledIntoAllowListedInfrastructureAssembliesOnly(string assemblyName)
    {
        var referencesMongo = CompiledDependencyTests.ReferencedAssemblyNames(assemblyName)
            .Any(name => name.StartsWith(MongoPackagePrefix, StringComparison.Ordinal));

        Assert.False(referencesMongo && !MongoProjects.Contains(assemblyName), $"{assemblyName} references a MongoDB assembly.");
    }

    [Fact]
    public void TheAllowList_NamesExistingInfrastructureProjects()
    {
        foreach (var name in MongoProjects)
        {
            var project = Assert.Single(Repository.SourceProjects, candidate => candidate.Name == name);
            Assert.Equal("Infrastructure", project.Layer);
        }
    }
}
