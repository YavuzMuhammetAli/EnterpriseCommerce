using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EnterpriseCommerce.Identity.IntegrationTests;

/// <summary>
/// Hosts the real Identity API pipeline in memory.
/// The tests using it never open the database, so a placeholder connection string that points nowhere is enough
/// to satisfy startup validation. SQL-backed tests use <see cref="Infrastructure.SqlServerTestDatabase"/>.
/// </summary>
public sealed class IdentityApiFactory : WebApplicationFactory<Program>
{
    private const string UnusedDatabase =
        "Server=tcp:127.0.0.1,1;Database=unused;Connect Timeout=1;TrustServerCertificate=true;Integrated Security=true";

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder
            .UseEnvironment("Development")
            .UseSetting("ConnectionStrings:IdentityDatabase", UnusedDatabase);
}
