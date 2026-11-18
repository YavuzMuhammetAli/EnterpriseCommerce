using EnterpriseCommerce.Identity.Application.Tokens;
using EnterpriseCommerce.Identity.Domain.Security;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace EnterpriseCommerce.Identity.Api.Protocol;

/// <summary>
/// Runs immediately before OpenIddict's native <c>ValidateTokenEntry</c> handler at the token endpoint
/// (ADR-017 section 5). When a refresh token that is already redeemed is presented again, it applies the
/// EnterpriseCommerce reuse policy.
/// </summary>
/// <remarks>
/// The presenter check comes first: if the authenticated client is not the client the token was issued to, the
/// request is rejected here, so the native handler never runs and <b>nothing is revoked</b> (PoC-13e, threat T24).
/// Otherwise the policy revokes the affected session or, for the admin console, every session of the user, and the
/// native handler then rejects the request.
/// </remarks>
internal sealed class RefreshTokenReuseHook(
    IOpenIddictTokenManager tokens,
    IOpenIddictApplicationManager applications,
    RefreshTokenReuseHandler reuseHandler) : IOpenIddictServerHandler<ValidateTokenContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateTokenContext>()
            .UseScopedHandler<RefreshTokenReuseHook>()
            .SetOrder(OpenIddictServerHandlers.Protection.ValidateTokenEntry.Descriptor.Order - 1)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    public async ValueTask HandleAsync(ValidateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.EndpointType != OpenIddictServerEndpointType.Token
            || context.Principal is null
            || !context.Principal.HasTokenType(TokenTypeIdentifiers.RefreshToken)
            || string.IsNullOrEmpty(context.TokenId))
        {
            return;
        }

        var token = await tokens.FindByIdAsync(context.TokenId, context.CancellationToken);
        if (token is null || !await tokens.HasStatusAsync(token, Statuses.Redeemed, context.CancellationToken))
        {
            return;
        }

        var applicationId = await tokens.GetApplicationIdAsync(token, context.CancellationToken);
        var application = applicationId is null ? null : await applications.FindByIdAsync(applicationId, context.CancellationToken);
        var tokenClientId = application is null ? null : await applications.GetClientIdAsync(application, context.CancellationToken);

        var action = await reuseHandler.HandleAsync(
            new RefreshReuseEvent(
                "redeemed-token",
                tokenClientId,
                context.Request?.ClientId,
                context.AuthorizationId,
                context.Principal.GetClaim(Claims.Subject)),
            context.CancellationToken);

        if (action == RefreshReuseAction.RejectPresenterMismatch)
        {
            context.Reject(
                error: Errors.InvalidGrant,
                description: "The specified refresh token is invalid.");
        }
    }
}
