using EnterpriseCommerce.Bff.Logging;
using StackExchange.Redis;

namespace EnterpriseCommerce.Bff.Sessions;

/// <summary>
/// Refuses to start the BFF unless the session Redis answers and uses <c>maxmemory-policy noeviction</c>
/// (ADR-016 section 3): an evicting store would silently end active sessions. There is no fallback.
/// </summary>
internal sealed class SessionStorePolicyCheck(IRedisConnection redis, ILogger<SessionStorePolicyCheck> logger) : IHostedService
{
    public const string RequiredPolicy = "noeviction";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        string? policy;
        try
        {
            policy = await redis.GetEvictionPolicyAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException)
        {
            BffLog.SessionStoreUnavailable(logger, exception);
            throw new InvalidOperationException("The session Redis is unreachable; the BFF does not start without it.", exception);
        }

        if (!IsAccepted(policy))
        {
            BffLog.SessionStorePolicyRejected(logger, policy ?? "(unknown)");
            throw new InvalidOperationException(
                $"The session Redis uses maxmemory-policy '{policy ?? "(unknown)"}'; the BFF requires '{RequiredPolicy}' (ADR-016 section 3).");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static bool IsAccepted(string? policy) => string.Equals(policy, RequiredPolicy, StringComparison.Ordinal);
}
