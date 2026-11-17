using System.Reflection;

namespace EnterpriseCommerce.ArchitectureTests;

/// <summary>
/// Compiled dependency rules: inspects the assemblies the Identity layers actually reference.
/// Complements <see cref="DependencyRuleTests"/>, which checks declared references.
/// </summary>
public sealed class CompiledDependencyTests
{
    private static readonly string[] InfrastructureAssemblyPrefixes =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.Data.SqlClient",
        "System.Data.SqlClient",
        "MongoDB.",
        "Confluent.Kafka",
        "StackExchange.Redis",
        "Microsoft.AspNetCore",
        "Yarp.",
        "Serilog",
    ];

    public static TheoryData<string, string[]> LayerRules() => new()
    {
        {
            "EnterpriseCommerce.Identity.Domain",
            ["EnterpriseCommerce.Identity.Application", "EnterpriseCommerce.Identity.Infrastructure", "EnterpriseCommerce.Identity.Api"]
        },
        {
            "EnterpriseCommerce.Identity.Application",
            ["EnterpriseCommerce.Identity.Infrastructure", "EnterpriseCommerce.Identity.Api"]
        },
        {
            "EnterpriseCommerce.Identity.Infrastructure",
            ["EnterpriseCommerce.Identity.Api"]
        },
    };

    [Theory]
    [MemberData(nameof(LayerRules))]
    public void Layer_DoesNotReferenceOuterLayers(string assemblyName, string[] forbidden)
    {
        var referenced = ReferencedAssemblyNames(assemblyName);

        Assert.Empty(referenced.Intersect(forbidden, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("EnterpriseCommerce.Identity.Domain")]
    [InlineData("EnterpriseCommerce.Identity.Application")]
    public void InnerLayer_DoesNotReferenceInfrastructureAssemblies(string assemblyName)
    {
        var violations = ReferencedAssemblyNames(assemblyName)
            .Where(name => InfrastructureAssemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)));

        Assert.Empty(violations);
    }

    [Fact]
    public void Domain_ReferencesNoOtherEnterpriseCommerceAssembly()
    {
        var violations = ReferencedAssemblyNames("EnterpriseCommerce.Identity.Domain")
            .Where(name => name.StartsWith("EnterpriseCommerce.", StringComparison.Ordinal));

        Assert.Empty(violations);
    }

    private static IEnumerable<string> ReferencedAssemblyNames(string assemblyName) =>
        Assembly.Load(assemblyName)
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .OfType<string>();
}
