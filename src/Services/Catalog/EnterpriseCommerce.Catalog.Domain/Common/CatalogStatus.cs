namespace EnterpriseCommerce.Catalog.Domain.Common;

/// <summary>
/// The status of a category or a brand. Neither is ever deleted: retiring keeps every product reference valid
/// (ADR-024 sections 6 and 7).
/// </summary>
public enum CatalogStatus
{
    Active,
    Retired,
}

/// <summary>The outcome of a state command.</summary>
public enum TransitionOutcome
{
    /// <summary>The state changed.</summary>
    Changed,

    /// <summary>The target state was already in effect; nothing changed.</summary>
    Unchanged,

    /// <summary>The command is not allowed from the current state.</summary>
    InvalidTransition,

    /// <summary>A rule of the target state is not met (for example publication requirements).</summary>
    RequirementsNotMet,
}
