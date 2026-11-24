namespace EnterpriseCommerce.Inventory.Domain.Common;

/// <summary>Who caused a stock movement (ADR-026 section 5).</summary>
public enum ActorKind
{
    /// <summary>A merchant-portal user; the actor ID is the Identity subject.</summary>
    User,

    /// <summary>A trusted service client; the actor ID is its <c>client_id</c>.</summary>
    Service,

    /// <summary>The service itself: the reservation expiry.</summary>
    System,
}

/// <summary>The actor of a movement. Stored for audit; never published (ADR-023 section 4).</summary>
public sealed record Actor
{
    /// <summary>The longest actor ID kept: an Identity subject or a client ID (both at most 128 characters).</summary>
    public const int IdMaxLength = 128;

    private Actor(ActorKind kind, string id)
    {
        Kind = kind;
        Id = id;
    }

    public ActorKind Kind { get; }

    public string Id { get; }

    /// <summary>The actor of every expiry, lazy or by the worker.</summary>
    public static Actor ReservationExpiry { get; } = new(ActorKind.System, "reservation-expiry");

    public static Actor User(string subjectId) => new(ActorKind.User, Checked(subjectId));

    public static Actor Service(string clientId) => new(ActorKind.Service, Checked(clientId));

    private static string Checked(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return id.Length <= IdMaxLength ? id : throw new ArgumentException($"An actor ID has at most {IdMaxLength} characters.", nameof(id));
    }
}
