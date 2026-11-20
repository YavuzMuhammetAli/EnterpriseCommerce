using EnterpriseCommerce.Identity.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.Identity.IntegrationTests.Protocol;

/// <summary>
/// The Identity host with test controls: commit gates and simulated database failures per authoritative operation,
/// failing post-commit cleanup, and a failing Identity cookie sign-in. Tests in one class run sequentially and must
/// leave every control as they found it.
/// </summary>
public sealed class ControlledIdentityServerFixture : IdentityServerFixture
{
    /// <summary>When set, the next Identity cookie sign-in throws (simulates a failure after a committed change).</summary>
    public bool FailNextCookieSignIn { get; set; }

    public ControllableSecurityTransaction Transactions => Services.GetRequiredService<ControllableSecurityTransaction>();

    public ControllableCleanup Cleanup => Services.GetRequiredService<ControllableCleanup>();

    protected override void ConfigureHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            ControllableSecurityTransaction.Decorate(services);
            ControllableCleanup.Decorate(services);
            services.PostConfigure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, options =>
            {
                var inner = options.Events.OnSigningIn;
                options.Events.OnSigningIn = context =>
                {
                    if (FailNextCookieSignIn)
                    {
                        FailNextCookieSignIn = false;
                        throw new InvalidOperationException("Test: the cookie could not be issued.");
                    }

                    return inner(context);
                };
            });
        });
}
