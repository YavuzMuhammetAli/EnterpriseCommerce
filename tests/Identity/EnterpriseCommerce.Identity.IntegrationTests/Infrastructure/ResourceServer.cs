using System.Net;
using System.Net.Http.Headers;
using EnterpriseCommerce.Identity.IntegrationTests.Protocol;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseCommerce.Identity.IntegrationTests.Infrastructure;

/// <summary>
/// A resource server that validates Identity's access tokens with the standard ASP.NET Core JWT bearer handler,
/// configured as ADR-017 section 4 requires for every service: JWKS from Identity's metadata, exact issuer and audience,
/// RS256 only, <c>typ: at+jwt</c>, 30 seconds of clock skew. It caches the JWKS like a real service.
/// </summary>
internal static class ResourceServer
{
    public static async Task<WebApplication> StartAsync(HttpMessageHandler identityBackChannel, string audience)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.Authority = IdentityServerFixture.Issuer.AbsoluteUri;
            options.Audience = audience;
            options.MapInboundClaims = false;
            options.BackchannelHttpHandler = identityBackChannel;
            options.TokenValidationParameters.ValidIssuer = IdentityServerFixture.Issuer.AbsoluteUri;
            options.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.RsaSha256];
            options.TokenValidationParameters.ValidTypes = ["at+jwt"];
            options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/resource", () => Results.Ok()).RequireAuthorization();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    public static async Task<HttpStatusCode> CallAsync(WebApplication api, string accessToken)
    {
        using var client = api.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/resource");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }
}
