using EnterpriseCommerce.Identity.Application.Sessions;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace EnterpriseCommerce.Identity.Api.Protocol;

/// <summary>
/// Ends the whole client session when a client revokes one of its tokens at <c>/connect/revoke</c>
/// (PHASE_2_2_AUTHENTICATION_FLOWS.md section 5.5.1, S10). OpenIddict revokes only the presented token natively (V11).
/// </summary>
/// <remarks>
/// <para>
/// It runs in the handling stage, after OpenIddict validated the token and its presenter (<c>ValidateAuthorizedParty</c>),
/// so a token presented by another client is rejected before this runs and revokes nothing. It is ordered before the
/// native <c>RevokeToken</c> handler: the authorization is revoked in the authoritative transaction first, its tokens as
/// cleanup afterwards.
/// </para>
/// <para>
/// If the authorization status cannot be committed, the exception surfaces as <c>503</c>: revocation never reports
/// success without it.
/// </para>
/// </remarks>
internal sealed class RevocationSessionHook(EndClientSessionHandler handler) : IOpenIddictServerHandler<HandleRevocationRequestContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<HandleRevocationRequestContext>()
            .UseScopedHandler<RevocationSessionHook>()
            .SetOrder(OpenIddictServerHandlers.Revocation.RevokeToken.Descriptor.Order - 1)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    public async ValueTask HandleAsync(HandleRevocationRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Service tokens (client credentials) belong to no authorization: nothing beyond the token itself to end.
        var authorizationId = context.GenericTokenPrincipal?.GetAuthorizationId();
        if (string.IsNullOrEmpty(authorizationId))
        {
            return;
        }

        await handler.HandleAsync(authorizationId, "revocation", context.CancellationToken);
    }
}
