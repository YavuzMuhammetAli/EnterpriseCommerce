namespace EnterpriseCommerce.ArchitectureTests;

/// <summary>
/// Declared dependency rules (from .csproj files), applied to every service:
/// Domain → nothing; Application → Domain; Infrastructure → Application, Domain;
/// Api → Application, Infrastructure. No references across services.
/// The Gateway references no service project.
/// </summary>
public sealed class DependencyRuleTests
{
    private static readonly Dictionary<string, string[]> AllowedLayerReferences = new(StringComparer.Ordinal)
    {
        ["Domain"] = [],
        ["Application"] = ["Domain"],
        ["Infrastructure"] = ["Application", "Domain"],
        ["Api"] = ["Application", "Infrastructure"],
    };

    /// <summary>Infrastructure technologies that must never appear in Domain or Application.</summary>
    private static readonly string[] InfrastructurePackagePrefixes =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.Data.SqlClient",
        "System.Data.SqlClient",
        "MongoDB.",
        "Confluent.Kafka",
        "StackExchange.Redis",
        "Microsoft.Extensions.Caching.StackExchangeRedis",
        "Microsoft.AspNetCore",
        "Yarp.",
        "Serilog",
    ];

    public static TheoryData<string> ServiceProjects() =>
        [.. Repository.SourceProjects.Where(p => p.Kind == ProjectKind.ServiceLayer).Select(p => p.Name)];

    public static TheoryData<string> InnerLayerProjects() =>
        [.. Repository.SourceProjects
            .Where(p => p.Kind == ProjectKind.ServiceLayer && p.Layer is "Domain" or "Application")
            .Select(p => p.Name)];

    [Theory]
    [MemberData(nameof(ServiceProjects))]
    public void ServiceProject_ReferencesOnlyAllowedLayersOfItsOwnService(string projectName)
    {
        var project = Get(projectName);
        var allowed = AllowedLayerReferences[project.Layer!]
            .Select(layer => $"{SourceProject.RootNamespace}.{project.Service}.{layer}")
            .ToHashSet(StringComparer.Ordinal);

        var violations = project.ProjectReferences
            .Where(reference =>
                (!allowed.Contains(reference) && !IsBuildingBlock(reference))
                || IsBuildingBlockForbidden(project, reference))
            .ToList();

        Assert.True(violations.Count == 0, $"{projectName} has forbidden project references: {string.Join(", ", violations)}");
    }

    [Theory]
    [MemberData(nameof(InnerLayerProjects))]
    public void InnerLayers_DoNotReferenceInfrastructureTechnologies(string projectName)
    {
        var project = Get(projectName);

        var forbiddenPackages = project.PackageReferences
            .Where(package => InfrastructurePackagePrefixes.Any(prefix => package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(forbiddenPackages.Count == 0, $"{projectName} references infrastructure packages: {string.Join(", ", forbiddenPackages)}");
        Assert.True(project.FrameworkReferences.Count == 0, $"{projectName} must not use framework references: {string.Join(", ", project.FrameworkReferences)}");
        Assert.Equal("Microsoft.NET.Sdk", project.Sdk);
    }

    [Fact]
    public void DomainProjects_HaveNoPackageReferences()
    {
        var violations = Repository.SourceProjects
            .Where(p => p.Kind == ProjectKind.ServiceLayer && p.Layer == "Domain" && p.PackageReferences.Count > 0)
            .Select(p => $"{p.Name}: {string.Join(", ", p.PackageReferences)}");

        Assert.Empty(violations);
    }

    [Fact]
    public void Gateway_DoesNotReferenceAnyServiceProject()
    {
        var gateways = Repository.SourceProjects.Where(p => p.Kind == ProjectKind.Gateway).ToList();
        Assert.NotEmpty(gateways);

        var violations = gateways
            .SelectMany(gateway => gateway.ProjectReferences.Where(reference => !IsBuildingBlock(reference))
                .Select(reference => $"{gateway.Name} -> {reference}"));

        Assert.Empty(violations);
    }

    [Fact]
    public void BuildingBlocks_DoNotReferenceServicesOrGateway()
    {
        var violations = Repository.SourceProjects
            .Where(p => p.Kind == ProjectKind.BuildingBlock)
            .SelectMany(block => block.ProjectReferences.Where(reference => !IsBuildingBlock(reference))
                .Select(reference => $"{block.Name} -> {reference}"));

        Assert.Empty(violations);
    }

    private static SourceProject Get(string name) =>
        Repository.SourceProjects.Single(project => project.Name == name);

    private static bool IsBuildingBlock(string projectName) =>
        projectName.StartsWith($"{SourceProject.RootNamespace}.BuildingBlocks.", StringComparison.Ordinal);

    /// <summary>The Domain layer stays self-contained: no shared domain model via BuildingBlocks.</summary>
    private static bool IsBuildingBlockForbidden(SourceProject project, string reference) =>
        project.Layer == "Domain" && IsBuildingBlock(reference);
}
