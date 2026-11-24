namespace EnterpriseCommerce.Inventory.Domain.Common;

/// <summary>
/// A fact an aggregate records about itself, internal to Inventory. It is never published as it is: the Infrastructure
/// maps the public ones to versioned integration event contracts when the aggregate is saved (ADR-023 section 5,
/// ADR-028). There is no dispatcher and no mediator.
/// </summary>
public interface IDomainEvent
{
    /// <summary>The business time of the fact.</summary>
    DateTimeOffset OccurredAt { get; }
}

/// <summary>An aggregate that records domain events until it is saved.</summary>
public interface IHasDomainEvents
{
    /// <summary>The events recorded since the aggregate was loaded or last saved, in the order they occurred.</summary>
    IReadOnlyList<IDomainEvent> DomainEvents { get; }

    /// <summary>Forgets the recorded events once they have been handed to the save that persists them.</summary>
    void ClearDomainEvents();
}
