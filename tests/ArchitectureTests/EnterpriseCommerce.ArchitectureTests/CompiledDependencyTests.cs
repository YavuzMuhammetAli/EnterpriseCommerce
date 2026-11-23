using System.Reflection;

namespace EnterpriseCommerce.ArchitectureTests;

/// <summary>
/// Compiled dependency rules: inspects the assemblies the service layers actually reference.
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

    /// <summary>The implemented services. A new service is added here together with its project references.</summary>
    public static readonly IReadOnlyList<string> ImplementedServices = ["Identity", "TenantMerchant", "Catalog"];

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
        {
            "EnterpriseCommerce.TenantMerchant.Domain",
            ["EnterpriseCommerce.TenantMerchant.Application", "EnterpriseCommerce.TenantMerchant.Infrastructure", "EnterpriseCommerce.TenantMerchant.Api"]
        },
        {
            "EnterpriseCommerce.TenantMerchant.Application",
            ["EnterpriseCommerce.TenantMerchant.Infrastructure", "EnterpriseCommerce.TenantMerchant.Api"]
        },
        {
            "EnterpriseCommerce.TenantMerchant.Infrastructure",
            ["EnterpriseCommerce.TenantMerchant.Api"]
        },
        {
            "EnterpriseCommerce.Catalog.Domain",
            ["EnterpriseCommerce.Catalog.Application", "EnterpriseCommerce.Catalog.Infrastructure", "EnterpriseCommerce.Catalog.Api"]
        },
        {
            "EnterpriseCommerce.Catalog.Application",
            ["EnterpriseCommerce.Catalog.Infrastructure", "EnterpriseCommerce.Catalog.Api"]
        },
        {
            "EnterpriseCommerce.Catalog.Infrastructure",
            ["EnterpriseCommerce.Catalog.Api"]
        },
    };

    /// <summary>Every compiled layer of every implemented service.</summary>
    public static TheoryData<string> ServiceAssemblies() =>
    [
        .. ImplementedServices.SelectMany(service => SourceProject.Layers.Select(layer => $"EnterpriseCommerce.{service}.{layer}")),
    ];

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
    [InlineData("EnterpriseCommerce.TenantMerchant.Domain")]
    [InlineData("EnterpriseCommerce.TenantMerchant.Application")]
    [InlineData("EnterpriseCommerce.Catalog.Domain")]
    [InlineData("EnterpriseCommerce.Catalog.Application")]
    public void InnerLayer_DoesNotReferenceInfrastructureAssemblies(string assemblyName)
    {
        var violations = ReferencedAssemblyNames(assemblyName)
            .Where(name => InfrastructureAssemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)));

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("EnterpriseCommerce.Identity.Domain")]
    [InlineData("EnterpriseCommerce.TenantMerchant.Domain")]
    [InlineData("EnterpriseCommerce.Catalog.Domain")]
    public void Domain_ReferencesNoOtherEnterpriseCommerceAssembly(string assemblyName)
    {
        var violations = ReferencedAssemblyNames(assemblyName)
            .Where(name => name.StartsWith("EnterpriseCommerce.", StringComparison.Ordinal));

        Assert.Empty(violations);
    }

    [Theory]
    [MemberData(nameof(ServiceAssemblies))]
    public void ServiceAssembly_ReferencesNoOtherServiceAssembly(string assemblyName)
    {
        var service = assemblyName.Split('.')[1];
        var violations = ReferencedAssemblyNames(assemblyName)
            .Where(name => name.StartsWith("EnterpriseCommerce.", StringComparison.Ordinal))
            .Where(name => !name.StartsWith($"EnterpriseCommerce.{service}.", StringComparison.Ordinal)
                && !name.StartsWith("EnterpriseCommerce.BuildingBlocks.", StringComparison.Ordinal));

        Assert.Empty(violations);
    }

    internal static IEnumerable<string> ReferencedAssemblyNames(string assemblyName) =>
        Assembly.Load(assemblyName)
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .OfType<string>();
}
