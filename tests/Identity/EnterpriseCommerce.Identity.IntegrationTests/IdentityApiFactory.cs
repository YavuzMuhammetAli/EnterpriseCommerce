using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EnterpriseCommerce.Identity.IntegrationTests;

/// <summary>
/// Hosts the real Identity API pipeline in memory.
/// No external infrastructure is required in this phase.
/// </summary>
public sealed class IdentityApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseEnvironment("Development");
}
