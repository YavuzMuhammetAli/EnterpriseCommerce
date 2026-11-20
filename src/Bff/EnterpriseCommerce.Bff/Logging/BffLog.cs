namespace EnterpriseCommerce.Bff.Logging;

/// <summary>
/// BFF log events (7000-7099). They carry the deployment name, a non-reversible session reference
/// (<see cref="Sessions.SessionIds.LogReference"/>) and error codes only: never a token, authorization code,
/// client assertion, PKCE verifier, cookie, session key or query string (ADR-017 section 11).
/// </summary>
internal static partial class BffLog
{
    [LoggerMessage(EventId = 7000, Level = LogLevel.Information, Message = "Security event LoginSucceeded: deployment {Deployment}, silent {Silent}")]
    public static partial void LoginSucceeded(ILogger logger, string deployment, bool silent);

    [LoggerMessage(EventId = 7001, Level = LogLevel.Warning, Message = "Security event LoginFailed: deployment {Deployment}, error {Error}, silent {Silent}")]
    public static partial void LoginFailed(ILogger logger, string deployment, string error, bool silent);

    [LoggerMessage(EventId = 7010, Level = LogLevel.Information, Message = "Session created: deployment {Deployment}, session {SessionReference}, persistent {Persistent}")]
    public static partial void SessionCreated(ILogger logger, string deployment, string sessionReference, bool persistent);

    [LoggerMessage(EventId = 7011, Level = LogLevel.Information, Message = "Session deleted: deployment {Deployment}, session {SessionReference}")]
    public static partial void SessionDeleted(ILogger logger, string deployment, string sessionReference);

    [LoggerMessage(EventId = 7012, Level = LogLevel.Debug, Message = "Session cookie without a usable session: deployment {Deployment}, reason {Reason}")]
    public static partial void SessionNotFound(ILogger logger, string deployment, string reason);

    [LoggerMessage(EventId = 7013, Level = LogLevel.Information, Message = "Session ended: access token expired and no refresh possible: deployment {Deployment}, session {SessionReference}")]
    public static partial void SessionEndedWithoutRefresh(ILogger logger, string deployment, string sessionReference);

    [LoggerMessage(EventId = 7020, Level = LogLevel.Debug, Message = "Refresh succeeded: deployment {Deployment}, session {SessionReference}")]
    public static partial void RefreshSucceeded(ILogger logger, string deployment, string sessionReference);

    [LoggerMessage(EventId = 7021, Level = LogLevel.Information, Message = "Security event RefreshRejected: invalid_grant ends the session: deployment {Deployment}, session {SessionReference}")]
    public static partial void RefreshRejected(ILogger logger, string deployment, string sessionReference);

    [LoggerMessage(EventId = 7022, Level = LogLevel.Warning, Message = "Security event RefreshOutcomeUncertain: the refresh token is not presented again: deployment {Deployment}, session {SessionReference}, cause {Cause}")]
    public static partial void RefreshOutcomeUncertain(ILogger logger, string deployment, string sessionReference, string cause);

    [LoggerMessage(EventId = 7023, Level = LogLevel.Error, Message = "Security event RefreshClientRejected: Identity rejected the BFF client: deployment {Deployment}, session {SessionReference}, error {Error}")]
    public static partial void RefreshClientRejected(ILogger logger, string deployment, string sessionReference, string error);

    [LoggerMessage(EventId = 7030, Level = LogLevel.Information, Message = "Logout: deployment {Deployment}, session {SessionReference}, revocation {Revocation}")]
    public static partial void LoggedOut(ILogger logger, string deployment, string sessionReference, string revocation);

    [LoggerMessage(EventId = 7031, Level = LogLevel.Warning, Message = "Logout: Identity revocation failed; the local session is deleted anyway: deployment {Deployment}, session {SessionReference}")]
    public static partial void RevocationFailed(ILogger logger, string deployment, string sessionReference);

    [LoggerMessage(EventId = 7032, Level = LogLevel.Information, Message = "Logout-all: deployment {Deployment}, session {SessionReference}, outcome {Outcome}")]
    public static partial void LogoutAll(ILogger logger, string deployment, string sessionReference, string outcome);

    [LoggerMessage(EventId = 7040, Level = LogLevel.Warning, Message = "Security event CsrfRejected: deployment {Deployment}, path {RequestPath}, layer {Layer}")]
    public static partial void CsrfRejected(ILogger logger, string deployment, PathString requestPath, string layer);

    [LoggerMessage(EventId = 7090, Level = LogLevel.Error, Message = "Session store unavailable; the request fails closed")]
    public static partial void SessionStoreUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7092, Level = LogLevel.Error, Message = "Identity is unreachable (metadata, keys or back channel); the request fails with 503: {ExceptionType}")]
    public static partial void IdentityUnavailable(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 7091, Level = LogLevel.Critical, Message = "The session Redis uses maxmemory-policy {Policy}; noeviction is required. The BFF does not start")]
    public static partial void SessionStorePolicyRejected(ILogger logger, string policy);

    [LoggerMessage(EventId = 7099, Level = LogLevel.Error, Message = "Unhandled exception in the BFF")]
    public static partial void UnhandledException(ILogger logger, Exception exception);
}
