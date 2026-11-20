using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace EnterpriseCommerce.Identity.Api.Protocol;

/// <summary>
/// Creates or updates the OpenIddict applications from <c>Identity:Clients</c> at startup.
/// Registrations are validated first; an invalid one stops the host (ADR-017 section 3).
/// </summary>
internal sealed partial class ClientRegistrationSynchronizer(
    IServiceScopeFactory scopeFactory,
    IOptions<IdentityServerSettings> settings,
    ILogger<ClientRegistrationSynchronizer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var clients = settings.Value.Clients;
        if (clients.Count == 0)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        foreach (var client in clients)
        {
            var descriptor = CreateDescriptor(client);
            var existing = await manager.FindByClientIdAsync(client.ClientId, cancellationToken);
            if (existing is null)
            {
                await manager.CreateAsync(descriptor, cancellationToken);
            }
            else
            {
                await manager.UpdateAsync(existing, descriptor, cancellationToken);
            }

            LogClientSynchronized(logger, client.ClientId, client.Kind);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static OpenIddictApplicationDescriptor CreateDescriptor(ClientRegistration client)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = client.ClientId,
            DisplayName = client.DisplayName ?? client.ClientId,
            ClientType = ClientTypes.Confidential,
            ApplicationType = ApplicationTypes.Web,
            ConsentType = ConsentTypes.Implicit,
            ClientSecret = string.IsNullOrWhiteSpace(client.ClientSecret) ? null : client.ClientSecret,
            JsonWebKeySet = string.IsNullOrWhiteSpace(client.JsonWebKeySet) ? null : JsonWebKeySet.Create(client.JsonWebKeySet),
        };

        descriptor.Permissions.Add(Permissions.Endpoints.Token);

        if (client.Kind == ClientKind.Service)
        {
            descriptor.Permissions.Add(Permissions.GrantTypes.ClientCredentials);
            foreach (var scope in client.ServiceScopes)
            {
                descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope.Scope);
            }

            descriptor.Settings[Settings.TokenLifetimes.AccessToken] = client.EffectiveAccessTokenLifetime.ToString("c", System.Globalization.CultureInfo.InvariantCulture);
            return descriptor;
        }

        descriptor.Permissions.Add(Permissions.Endpoints.Authorization);
        descriptor.Permissions.Add(Permissions.Endpoints.Revocation);
        descriptor.Permissions.Add(Permissions.Endpoints.EndSession);
        descriptor.Permissions.Add(Permissions.GrantTypes.AuthorizationCode);
        descriptor.Permissions.Add(Permissions.GrantTypes.RefreshToken);
        descriptor.Permissions.Add(Permissions.ResponseTypes.Code);
        descriptor.Permissions.Add(Permissions.Prefixes.Scope + Scopes.OpenId);
        descriptor.Permissions.Add(Permissions.Prefixes.Scope + Scopes.OfflineAccess);
        descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);

        foreach (var uri in client.RedirectUris)
        {
            descriptor.RedirectUris.Add(new Uri(uri, UriKind.Absolute));
        }

        foreach (var uri in client.PostLogoutRedirectUris)
        {
            descriptor.PostLogoutRedirectUris.Add(new Uri(uri, UriKind.Absolute));
        }

        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        descriptor.Settings[Settings.TokenLifetimes.AccessToken] = client.EffectiveAccessTokenLifetime.ToString("c", invariant);
        descriptor.Settings[Settings.TokenLifetimes.RefreshToken] = client.RefreshTokenLifetime.ToString("c", invariant);
        return descriptor;
    }

    [LoggerMessage(EventId = 4000, Level = LogLevel.Information, Message = "OAuth client {ClientId} ({Kind}) synchronized")]
    private static partial void LogClientSynchronized(ILogger logger, string clientId, ClientKind kind);
}
