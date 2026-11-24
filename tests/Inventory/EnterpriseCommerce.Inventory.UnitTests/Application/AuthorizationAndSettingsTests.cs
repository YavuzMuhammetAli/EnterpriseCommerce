using EnterpriseCommerce.Inventory.Application.Authorization;
using EnterpriseCommerce.Inventory.Application.Catalog;
using EnterpriseCommerce.Inventory.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.Inventory.UnitTests.Application;

/// <summary>The merchant role matrix (ADR-026 section 10), Catalog eligibility (ADR-027 section 2) and the startup rules.</summary>
public sealed class AuthorizationAndSettingsTests
{
    [Theory]
    [InlineData("MerchantOwner", InventoryPermission.ViewInventory, true)]
    [InlineData("MerchantOwner", InventoryPermission.ManageInventory, true)]
    [InlineData("MerchantAdmin", InventoryPermission.ViewInventory, true)]
    [InlineData("MerchantAdmin", InventoryPermission.ManageInventory, true)]
    [InlineData("MerchantStaff", InventoryPermission.ViewInventory, true)]
    [InlineData("MerchantStaff", InventoryPermission.ManageInventory, false)]
    [InlineData("MerchantAuditor", InventoryPermission.ViewInventory, false)]
    [InlineData("merchantowner", InventoryPermission.ViewInventory, false)]
    [InlineData(null, InventoryPermission.ViewInventory, false)]
    public void TheRoleMatrix_GrantsStaffReadsOnly_AndUnknownRolesNothing(string? role, InventoryPermission permission, bool granted)
    {
        Assert.Equal(granted, InventoryRolePermissions.Grants(role, permission));
    }

    [Theory]
    [InlineData(true, "Draft", true)]
    [InlineData(true, "Published", true)]
    [InlineData(true, "Archived", false)]
    [InlineData(true, "Quarantined", false)]
    [InlineData(true, null, false)]
    [InlineData(false, null, false)]
    public void OnlyDraftAndPublishedProducts_AreEligible_AndUnknownStatusesFailClosed(bool found, string? status, bool eligible)
    {
        Assert.Equal(eligible, new CatalogVariantVerdict(found, status, true, "SKU").IsEligible);
    }

    [Fact]
    public void InDevelopment_TheServiceStarts_WithoutTheIntegrations()
    {
        Assert.Null(Validate(Environments.Development, []));
    }

    [Theory]
    [InlineData("Inventory:ServiceClient is required")]
    [InlineData("Inventory:MembershipVerification is invalid")]
    [InlineData("Inventory:CatalogVerification is invalid")]
    [InlineData("Inventory:Kafka is invalid")]
    public void OutsideDevelopment_EveryIntegration_IsRequired(string expected)
    {
        var failure = Validate(Environments.Production, []);

        Assert.NotNull(failure);
        Assert.Contains(expected.Replace(" is required", " is invalid", StringComparison.Ordinal), failure.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Inventory:MembershipVerification:BaseAddress", "https://tenant-merchant.test/", "Inventory:MembershipVerification is invalid")]
    [InlineData("Inventory:ServiceClient:TokenEndpoint", "https://identity.test/connect/token", "Inventory:ServiceClient is invalid")]
    [InlineData("Inventory:ServiceClient:ClientId", "bff-merchant", "Inventory:ServiceClient is invalid")]
    public void APartialIntegration_IsAnError_EvenInDevelopment(string setting, string value, string expected)
    {
        var settings = new Dictionary<string, string?> { [setting] = value };
        if (setting.EndsWith("ClientId", StringComparison.Ordinal))
        {
            settings["Inventory:ServiceClient:TokenEndpoint"] = "https://identity.test/connect/token";
            settings["Inventory:ServiceClient:PrivateKeyPath"] = "key.pem";
            settings["Inventory:Authentication:Issuer"] = "https://identity.test/";
        }

        var failure = Validate(Environments.Development, settings);

        Assert.NotNull(failure);
        Assert.Contains(expected, failure.ToString(), StringComparison.Ordinal);
    }

    private static Exception? Validate(string environment, Dictionary<string, string?> settings)
    {
        settings["ConnectionStrings:InventoryDatabase"] = "Server=unused;Database=unused";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment(environment));
        services.AddInventoryInfrastructure(configuration);

        using var provider = services.BuildServiceProvider();
        return Record.Exception(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "EnterpriseCommerce.Inventory.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
