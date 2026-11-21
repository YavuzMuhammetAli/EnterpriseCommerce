using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace EnterpriseCommerce.Identity.IntegrationTests.Protocol;

/// <summary>Helpers for the internal account APIs: lifecycle (Phase 2.3 S6) and email verification (ADR-021).</summary>
public partial class IdentityServerFixture
{
    public const string AccountLifecycleScope = "svc.identity.account-lifecycle";

    public const string AccountVerificationScope = "svc.identity.account-verification";

    /// <summary>Obtains a client credentials access token for <paramref name="client"/> with <paramref name="scope"/>.</summary>
    public async Task<string> ServiceTokenAsync(TestClient client, string scope = AccountLifecycleScope)
    {
        var response = await RequestTokenAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = scope,
        });
        Assert.Equal(HttpStatusCode.OK, response.Status);
        return response.AccessToken!;
    }

    /// <summary>Calls <c>POST /internal/v1/accounts/{accountId}/{action}</c> with an optional bearer token and body.</summary>
    public async Task<(HttpStatusCode Status, JsonElement Body)> CallAccountLifecycleAsync(
        string action,
        string accountId,
        string? bearerToken,
        object? body = null)
    {
        using var backChannel = CreateBackChannel();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/v1/accounts/{Uri.EscapeDataString(accountId)}/{action}")
        {
            Content = JsonContent.Create(body ?? new { reason = "Administrative", actorReference = "admin:test-operator" }),
        };
        if (bearerToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        using var response = await backChannel.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text).RootElement.Clone());
    }

    /// <summary>Disables the account through the internal API as the allowed admin service client.</summary>
    public async Task<(HttpStatusCode Status, JsonElement Body)> DisableThroughApiAsync(string accountId) =>
        await CallAccountLifecycleAsync("disable", accountId, await ServiceTokenAsync(AccountAdmin));

    /// <summary>Reactivates the account through the internal API as the allowed admin service client.</summary>
    public async Task<(HttpStatusCode Status, JsonElement Body)> ReactivateThroughApiAsync(string accountId) =>
        await CallAccountLifecycleAsync("reactivate", accountId, await ServiceTokenAsync(AccountAdmin));

    /// <summary>Calls <c>POST /internal/v1/accounts/{accountId}/email-verification</c> with an optional bearer token and body.</summary>
    public async Task<(HttpStatusCode Status, JsonElement Body)> CallEmailVerificationAsync(string accountId, string? bearerToken, object? body)
    {
        using var backChannel = CreateBackChannel();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/v1/accounts/{Uri.EscapeDataString(accountId)}/email-verification")
        {
            Content = body as HttpContent ?? JsonContent.Create(body),
        };
        if (bearerToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        using var response = await backChannel.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text).RootElement.Clone());
    }
}
