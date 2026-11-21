using System.Reflection;

namespace EnterpriseCommerce.ArchitectureTests;

/// <summary>
/// Ownership boundaries between services (ADR-003, ADR-014, ADR-015, ADR-017):
/// credentials and token issuance belong to Identity only, merchant roles to Tenant &amp; Merchant only, and no service
/// names another service's database.
/// </summary>
public sealed class ServiceOwnershipTests
{
    /// <summary>The user store, credential and token-server technologies (ADR-015). Only Identity may declare them.</summary>
    private static readonly string[] CredentialPackagePrefixes =
    [
        "Microsoft.AspNetCore.Identity",
        "Microsoft.Extensions.Identity",
        "OpenIddict",
    ];

    /// <summary>Member names that would mean a service stores or handles user credentials.</summary>
    private static readonly string[] CredentialMemberWords =
    [
        "Password",
        "Credential",
        "SecurityStamp",
        "RefreshToken",
        "ClientSecret",
    ];

    private static readonly string[] MerchantRoleNames = ["MerchantOwner", "MerchantAdmin", "MerchantStaff", "MerchantRole"];

    public static TheoryData<string> NonIdentityServiceProjects() =>
        [.. Repository.SourceProjects
            .Where(project => project.Kind is ProjectKind.ServiceLayer or ProjectKind.DevelopmentAdapter && project.Service != "Identity")
            .Select(project => project.Name)];

    [Theory]
    [MemberData(nameof(NonIdentityServiceProjects))]
    public void OnlyIdentity_DeclaresCredentialOrTokenServerPackages(string projectName)
    {
        var project = Repository.SourceProjects.Single(candidate => candidate.Name == projectName);

        var violations = project.PackageReferences
            .Where(package => CredentialPackagePrefixes.Any(prefix => package.StartsWith(prefix, StringComparison.Ordinal)));

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("EnterpriseCommerce.TenantMerchant.Domain")]
    [InlineData("EnterpriseCommerce.TenantMerchant.Application")]
    [InlineData("EnterpriseCommerce.TenantMerchant.Infrastructure")]
    [InlineData("EnterpriseCommerce.TenantMerchant.Api")]
    public void TenantMerchant_HoldsNoCredentials(string assemblyName)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var violations = Assembly.Load(assemblyName).GetTypes()
            .SelectMany(type => type.GetProperties(all).Select(property => $"{type.FullName}.{property.Name}")
                .Concat(type.GetFields(all).Select(field => $"{type.FullName}.{field.Name}")))
            .Where(member => CredentialMemberWords.Any(word => member.Split('.')[^1].Contains(word, StringComparison.OrdinalIgnoreCase)));

        Assert.Empty(violations);
    }

    [Fact]
    public void Identity_NeverKnowsMerchantRoles()
    {
        // Merchant roles are never put into Identity tokens (ADR-017 section 2). The claim side is tested by Identity's
        // AuthorizationCodeFlowTests (no roles or tenant_context claims); this keeps the role catalog out of Identity.
        var violations = SourceFiles("Identity", "*.cs")
            .SelectMany(file => MerchantRoleNames
                .Where(role => File.ReadAllText(file).Contains(role, StringComparison.Ordinal))
                .Select(role => $"{Path.GetRelativePath(Repository.Root, file)}: {role}"));

        Assert.Empty(violations);
    }

    [Fact]
    public void Services_NameOnlyTheirOwnDatabase()
    {
        // Convention: a service's connection string is named "{Service}Database" (ConnectionStrings section).
        var services = ServiceNames();
        Assert.Contains("TenantMerchant", services);

        var violations = services
            .SelectMany(owner => services
                .Where(other => other != owner)
                .SelectMany(other => SourceFiles(other, "*.cs").Concat(SourceFiles(other, "*.json")))
                .Where(file => File.ReadAllText(file).Contains($"{owner}Database", StringComparison.Ordinal))
                .Select(file => $"{Path.GetRelativePath(Repository.Root, file)} names {owner}Database"));

        Assert.Empty(violations);
    }

    private static IReadOnlyList<string> ServiceNames() =>
        [.. Repository.SourceProjects.Where(project => project.Kind == ProjectKind.ServiceLayer).Select(project => project.Service!).Distinct()];

    private static IEnumerable<string> SourceFiles(string service, string pattern) =>
        Directory.EnumerateFiles(Path.Combine(Repository.Root, "src", "Services", service), pattern, SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment => segment is "bin" or "obj"));
}
