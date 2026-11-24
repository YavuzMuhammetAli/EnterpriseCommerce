using System.Globalization;
using EnterpriseCommerce.Inventory.Application.Common;
using EnterpriseCommerce.Inventory.Application.Items;
using EnterpriseCommerce.Inventory.Domain.Common;
using EnterpriseCommerce.Inventory.Domain.Items;
using EnterpriseCommerce.Inventory.Domain.Reservations;

namespace EnterpriseCommerce.Inventory.Application.Reservations;

/// <summary>
/// The reservation lifetimes (<c>Inventory:Reservations</c>; ADR-026 section 7). The default holds stock for a
/// simulated payment step; a caller may ask for a lifetime between one minute and the maximum.
/// </summary>
public sealed class ReservationSettings
{
    public const string SectionName = "Inventory:Reservations";

    /// <summary>The shortest lifetime a caller may request.</summary>
    public static readonly TimeSpan MinRequestedLifetime = TimeSpan.FromMinutes(1);

    public TimeSpan DefaultLifetime { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan MaxLifetime { get; set; } = TimeSpan.FromHours(1);

    public IEnumerable<string> Validate()
    {
        if (DefaultLifetime < TimeSpan.FromMinutes(1) || DefaultLifetime > TimeSpan.FromHours(24))
        {
            yield return "DefaultLifetime must be 1 minute to 24 hours.";
        }

        if (MaxLifetime < TimeSpan.FromMinutes(1) || MaxLifetime > TimeSpan.FromHours(24))
        {
            yield return "MaxLifetime must be 1 minute to 24 hours.";
        }

        if (DefaultLifetime > MaxLifetime)
        {
            yield return "DefaultLifetime must not exceed MaxLifetime.";
        }
    }
}

/// <summary>The allow-listed service client of a request and the request's correlation ID.</summary>
public sealed record ServiceCaller(string ClientId, string CorrelationId);

/// <summary>A reservation request as received.</summary>
public sealed record CreateReservationCommand(
    ServiceCaller Caller,
    Guid TenantId,
    IReadOnlyList<ReservationLineRequest>? Lines,
    string? Reference,
    int? ExpiresInSeconds,
    string IdempotencyKey);

/// <summary>
/// Places a reservation (ADR-026 sections 6 to 9): every line against the tenant's locked items, all or nothing,
/// idempotent per (client, tenant, key) under a transaction-scoped application lock on that scope, with its
/// movements and events in the same transaction.
/// </summary>
public sealed class CreateReservationHandler(IInventoryWriteGate gate, IInventoryQueries queries, ReservationSettings settings)
{
    public const string RequestHashVersion = "inventory-reservation/v1";

    public async Task<CommandResult<Reservation>> HandleAsync(CreateReservationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = Validate(command, out var lifetime);
        if (errors.Count > 0)
        {
            InventoryMetrics.RecordReservation("refused");
            return CommandResult<Reservation>.Invalid(errors);
        }

        var lines = command.Lines!;
        var key = new IdempotencyRecord(command.IdempotencyKey, CanonicalRequestHash.Compute(
        [
            RequestHashVersion,
            command.TenantId.ToString("D"),
            command.Reference ?? string.Empty,
            command.ExpiresInSeconds?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,

            // The line order does not change the request: lines are hashed in variant order.
            .. lines.OrderBy(line => line.VariantId).Select(line => $"{line.VariantId:D}={line.Quantity.ToString(CultureInfo.InvariantCulture)}"),
        ]));

        try
        {
            var result = await gate.ExecuteAsync(
                "create-reservation",
                async (session, token) =>
                {
                    // The first lock: a retry of the same key waits here for the original, then replays it.
                    await session.LockReservationKeyAsync(command.Caller.ClientId, command.TenantId, key.Key, token);
                    if (await session.FindReservationByKeyAsync(command.Caller.ClientId, command.TenantId, key.Key, token) is { } previous)
                    {
                        return WriteDecision.Rollback(ReplayOf(previous, key));
                    }

                    var items = await session.LockItemsOfVariantsAsync(command.TenantId, [.. lines.Select(line => line.VariantId)], token);
                    var placement = Reservation.Place(
                        command.TenantId, command.Caller.ClientId, key, lines, items, command.Reference, lifetime, command.Caller.CorrelationId, session.Now);
                    switch (placement.Error)
                    {
                        case PlacementError.UnknownVariant:
                            return WriteDecision.Rollback(new CommandResult<Reservation>(CommandStatus.InventoryItemNotFound, Variants: placement.UnknownVariants));
                        case PlacementError.InsufficientStock:
                            return WriteDecision.Rollback(new CommandResult<Reservation>(CommandStatus.InsufficientStock, Variants: placement.ShortVariants));
                        case PlacementError.InvalidLines:
                            return WriteDecision.Rollback(CommandResult<Reservation>.Invalid(new Dictionary<string, string[]> { ["lines"] = ["The lines are invalid."] }));
                    }

                    session.UseMessageMetadata(MessageMetadata.ForRequest(command.Caller.CorrelationId)!);
                    session.Add(placement.Reservation!);
                    return WriteDecision.Commit(new CommandResult<Reservation>(CommandStatus.Created, placement.Reservation));
                },
                cancellationToken);
            InventoryMetrics.RecordReservation(result.Status switch
            {
                CommandStatus.Created => "created",
                CommandStatus.Replayed => "replayed",
                CommandStatus.InsufficientStock => "insufficient_stock",
                CommandStatus.InventoryItemNotFound => "unknown_variant",
                _ => "refused",
            });
            return result;
        }
        catch (InventoryWriteConflictException exception) when (exception.Conflict == InventoryWriteConflict.UniqueConstraint)
        {
            // A backstop: the application lock serializes one scope, so the unique index should never refuse.
            var previous = await queries.FindReservationByKeyAsync(command.TenantId, command.Caller.ClientId, key.Key, cancellationToken);
            return previous is null ? CommandResult<Reservation>.Of(CommandStatus.ConcurrencyConflict) : ReplayOf(previous, key);
        }
    }

    private Dictionary<string, string[]> Validate(CreateReservationCommand command, out TimeSpan lifetime)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        lifetime = settings.DefaultLifetime;
        if (!Reservation.AreValidLines(command.Lines))
        {
            errors["lines"] = [$"1 to {Reservation.MaxLines} lines of distinct variants are required, each with a whole quantity of 1 to {Quantities.MaxLineQuantity}."];
        }

        if (command.Reference is not null && !TraceText.IsValid(command.Reference))
        {
            errors["reference"] = [$"reference must be 1 to {TraceText.MaxLength} of A-Z a-z 0-9 - _ . :"];
        }

        if (command.ExpiresInSeconds is { } seconds)
        {
            var requested = TimeSpan.FromSeconds(seconds);
            if (requested < ReservationSettings.MinRequestedLifetime || requested > settings.MaxLifetime)
            {
                errors["expiresInSeconds"] = [$"expiresInSeconds must be between {ReservationSettings.MinRequestedLifetime.TotalSeconds} and {settings.MaxLifetime.TotalSeconds}."];
            }
            else
            {
                lifetime = requested;
            }
        }

        return errors;
    }

    private static CommandResult<Reservation> ReplayOf(Reservation previous, IdempotencyRecord key) =>
        previous.RequestHash.AsSpan().SequenceEqual(key.RequestHash)
            ? new CommandResult<Reservation>(CommandStatus.Replayed, previous)
            : CommandResult<Reservation>.Of(CommandStatus.IdempotencyKeyReused);
}

/// <summary>
/// Reads, commits and releases reservations for their owner (ADR-026 sections 6 and 7). Each transition locks the
/// reservation, then its items in ID order, and re-reads the state under the lock, so commit, release and expiry never
/// transition one reservation twice. An overdue active reservation expires in the request's own transaction.
/// </summary>
public sealed class ReservationHandlers(IInventoryWriteGate gate, IInventoryQueries queries)
{
    public async Task<CommandResult<Reservation>> GetAsync(ServiceCaller caller, Guid tenantId, Guid reservationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var reservation = await queries.GetReservationAsync(tenantId, caller.ClientId, reservationId, cancellationToken);
        return reservation is null ? CommandResult<Reservation>.Of(CommandStatus.NotFound) : new CommandResult<Reservation>(CommandStatus.Ok, reservation);
    }

    /// <summary>Commits an active reservation; idempotent on a committed one.</summary>
    public Task<CommandResult<Reservation>> CommitAsync(ServiceCaller caller, Guid tenantId, Guid reservationId, CancellationToken cancellationToken) =>
        TransitionAsync("commit-reservation", "commit", caller, tenantId, reservationId, (reservation, items, now) => reservation.Commit(items, Actor.Service(caller.ClientId), caller.CorrelationId, now), cancellationToken);

    /// <summary>Releases an active reservation; idempotent on a released one, and a success on an expired one.</summary>
    public Task<CommandResult<Reservation>> ReleaseAsync(ServiceCaller caller, Guid tenantId, Guid reservationId, CancellationToken cancellationToken) =>
        TransitionAsync("release-reservation", "release", caller, tenantId, reservationId, (reservation, items, now) => reservation.Release(items, Actor.Service(caller.ClientId), caller.CorrelationId, now), cancellationToken);

    private async Task<CommandResult<Reservation>> TransitionAsync(
        string operation,
        string transition,
        ServiceCaller caller,
        Guid tenantId,
        Guid reservationId,
        Func<Reservation, IReadOnlyDictionary<Guid, InventoryItem>, DateTimeOffset, TransitionResult> change,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return await gate.ExecuteAsync(
            operation,
            async (session, token) =>
            {
                var reservation = await session.LockReservationAsync(tenantId, reservationId, token);

                // Another client's reservation is never visible, as if it did not exist.
                if (reservation is null || reservation.OwnerClientId != caller.ClientId)
                {
                    return WriteDecision.Rollback(CommandResult<Reservation>.Of(CommandStatus.NotFound));
                }

                IReadOnlyDictionary<Guid, InventoryItem> items = new Dictionary<Guid, InventoryItem>();
                if (!reservation.IsTerminal)
                {
                    items = await session.LockItemsAsync(tenantId, [.. reservation.Lines.Select(line => line.InventoryItemId)], token);
                }

                var result = change(reservation, items, session.Now);
                InventoryMetrics.RecordTransition(transition, result.Outcome switch
                {
                    TransitionOutcome.Changed => "changed",
                    TransitionOutcome.Unchanged => "unchanged",
                    TransitionOutcome.ExpiredNow => "expired_now",
                    _ => "conflict",
                });

                switch (result.Outcome)
                {
                    case TransitionOutcome.Changed:
                        session.UseMessageMetadata(MessageMetadata.ForRequest(caller.CorrelationId)!);
                        return WriteDecision.Commit(new CommandResult<Reservation>(CommandStatus.Ok, reservation));
                    case TransitionOutcome.ExpiredNow:
                        // The expiry is committed; a commit then reports it, a release succeeds with the actual state.
                        InventoryMetrics.RecordExpired("lazy");
                        session.UseMessageMetadata(MessageMetadata.ForRequest(caller.CorrelationId)!);
                        return WriteDecision.Commit(new CommandResult<Reservation>(transition == "commit" ? CommandStatus.ReservationExpired : CommandStatus.Ok, reservation));
                    case TransitionOutcome.Unchanged:
                        return WriteDecision.Rollback(new CommandResult<Reservation>(CommandStatus.Ok, reservation));
                    default:
                        return WriteDecision.Rollback(new CommandResult<Reservation>(
                            reservation.Status switch
                            {
                                ReservationStatus.Committed => CommandStatus.ReservationCommitted,
                                ReservationStatus.Released => CommandStatus.ReservationReleased,
                                _ => CommandStatus.ReservationExpired,
                            },
                            reservation));
                }
            },
            cancellationToken);
    }
}
