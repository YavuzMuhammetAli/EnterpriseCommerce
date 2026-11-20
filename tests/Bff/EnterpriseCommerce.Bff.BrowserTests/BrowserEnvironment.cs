using System.Net;
using EnterpriseCommerce.Bff.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;

namespace EnterpriseCommerce.Bff.BrowserTests;

/// <summary>
/// Identity and two BFF deployments on real Kestrel listeners for real browsers, plus a cross-site "attacker" origin.
/// The storefront uses the accepted defaults; the merchant deployment gets a 3-second idle timeout for the expiry test.
/// </summary>
public sealed class BrowserEnvironment : BffTestEnvironment
{
    public const string Variable = "EC_TEST_BROWSERS";

    private WebApplication? _attacker;

    protected override bool UseKestrel => true;

    public static bool IsEnabled => string.Equals(Environment.GetEnvironmentVariable(Variable), "true", StringComparison.OrdinalIgnoreCase);

    public BffHost StorefrontHost { get; private set; } = null!;

    public BffHost ShortIdleHost { get; private set; } = null!;

    public Uri AttackerOrigin { get; private set; } = null!;

    public IPlaywright Playwright { get; private set; } = null!;

    public override async ValueTask InitializeAsync()
    {
        if (!IsEnabled || !TestInfrastructure.IsConfigured)
        {
            return;
        }

        await base.InitializeAsync();
        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        StorefrontHost = CreateBff(Storefront, options => options.WebRoot = webRoot);
        ShortIdleHost = CreateBff(Merchant, options =>
        {
            options.WebRoot = webRoot;
            options.IdleTimeout = TimeSpan.FromSeconds(3);
        });
        await StartAttackerAsync();

        // Idempotent; CI installs the browsers and their system dependencies in a separate step first.
        var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium", "firefox", "webkit"]);
        Assert.Equal(0, exitCode);
        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
    }

    public void RequireBrowsers()
    {
        if (!IsEnabled)
        {
            Assert.Skip($"Browser test: set {Variable}=true (and the infrastructure of the integration tests).");
        }

        RequireAvailable();
    }

    public async Task<IBrowserContext> NewContextAsync(string engine)
    {
        var browser = await Playwright[engine].LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

        // The local development certificate is not trusted on every machine; certificate errors only are ignored.
        return await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
    }

    /// <summary>Signs in through the BFF with Identity's login page and waits for the landing page.</summary>
    public async Task SignInAsync(IPage page, BffHost bff, string email, string returnUrl = "/", string password = Password)
    {
        await page.GotoAsync(bff.Url("/bff/login?returnUrl=" + Uri.EscapeDataString(returnUrl)).AbsoluteUri);
        await page.WaitForURLAsync(url => url.Contains("/account/login", StringComparison.Ordinal));
        await page.FillAsync("input[name='Input.Email']", email);
        await page.FillAsync("input[name='Input.Password']", password);
        await page.PressAsync("input[name='Input.Password']", "Enter");
        await page.WaitForURLAsync(url => url.StartsWith(bff.Origin.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal) && !url.Contains("/bff/", StringComparison.Ordinal));
    }

    private async Task StartAttackerAsync()
    {
        var port = FreeLoopbackPort();
        AttackerOrigin = new Uri($"https://localhost:{port}");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrelHttpsConfiguration().ConfigureKestrel(kestrel => kestrel.ListenLocalhost(port, listen => listen.UseHttps()));
        _attacker = builder.Build();
        var target = StorefrontHost.Url("/bff/logout").AbsoluteUri;
        _attacker.MapGet("/", () => Results.Content(
            $"<!doctype html><form id=\"f\" method=\"post\" action=\"{target}\"></form><script>document.getElementById('f').submit();</script>",
            "text/html"));
        await _attacker.StartAsync();
    }

    private static int FreeLoopbackPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public override async ValueTask DisposeAsync()
    {
        Playwright?.Dispose();
        if (_attacker is not null)
        {
            await _attacker.DisposeAsync();
        }

        await base.DisposeAsync();
    }
}
