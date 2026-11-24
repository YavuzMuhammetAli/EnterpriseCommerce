namespace EnterpriseCommerce.Cart.Infrastructure.Caching;

/// <summary>
/// The cart cache (ADR-030). Configuration section <c>Cart:Cache</c>; the connection string is
/// <c>ConnectionStrings:CartCache</c> (environment variable <c>ConnectionStrings__CartCache</c>), never committed.
/// </summary>
internal sealed class CartCacheOptions
{
    public const string SectionName = "Cart:Cache";
    public const string ConnectionStringName = "CartCache";

    /// <summary>
    /// Whether Redis is used. When true, the connection string is required and its absence fails at startup. False runs
    /// the service on SQL Server alone (ADR-030 section 5).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The connection string, read from <c>ConnectionStrings:CartCache</c>.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>The prefix of every key; keeps Cart's keys private to it (ADR-006 rule 2).</summary>
    public string KeyPrefix { get; set; } = "ec:cart:";

    /// <summary>The lifetime of an entry, set on every write (ADR-006 rule 3). 1 to 60 minutes.</summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>The bound of one Redis operation. 50 milliseconds to 2 seconds.</summary>
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The bound of a connection attempt. 100 milliseconds to 10 seconds.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long this instance bypasses Redis after a failure. 1 to 60 seconds.</summary>
    public TimeSpan CircuitBreakDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The most carts whose post-commit cache write waits for a repair. 0 (no repair) to 100,000.</summary>
    public int RepairQueueCapacity { get; set; } = 1000;

    /// <summary>How often the repair loop tries to replay missed writes. 100 milliseconds to 1 minute.</summary>
    public TimeSpan RepairInterval { get; set; } = TimeSpan.FromSeconds(1);

    public IEnumerable<string> Validate()
    {
        if (Enabled && string.IsNullOrWhiteSpace(ConnectionString))
        {
            yield return $"ConnectionStrings:{ConnectionStringName} is required when {SectionName}:Enabled is true.";
        }

        if (string.IsNullOrWhiteSpace(KeyPrefix) || KeyPrefix.Length > 64 || KeyPrefix.Any(char.IsWhiteSpace))
        {
            yield return $"{SectionName}:KeyPrefix must be 1 to 64 characters without white space.";
        }

        if (TimeToLive < TimeSpan.FromMinutes(1) || TimeToLive > TimeSpan.FromMinutes(60))
        {
            yield return $"{SectionName}:TimeToLive must be 1 to 60 minutes.";
        }

        if (OperationTimeout < TimeSpan.FromMilliseconds(50) || OperationTimeout > TimeSpan.FromSeconds(2))
        {
            yield return $"{SectionName}:OperationTimeout must be 50 milliseconds to 2 seconds.";
        }

        if (ConnectTimeout < TimeSpan.FromMilliseconds(100) || ConnectTimeout > TimeSpan.FromSeconds(10))
        {
            yield return $"{SectionName}:ConnectTimeout must be 100 milliseconds to 10 seconds.";
        }

        if (CircuitBreakDuration < TimeSpan.FromSeconds(1) || CircuitBreakDuration > TimeSpan.FromSeconds(60))
        {
            yield return $"{SectionName}:CircuitBreakDuration must be 1 to 60 seconds.";
        }

        if (RepairQueueCapacity is < 0 or > 100_000)
        {
            yield return $"{SectionName}:RepairQueueCapacity must be 0 to 100000.";
        }

        if (RepairInterval < TimeSpan.FromMilliseconds(100) || RepairInterval > TimeSpan.FromMinutes(1))
        {
            yield return $"{SectionName}:RepairInterval must be 100 milliseconds to 1 minute.";
        }
    }
}
