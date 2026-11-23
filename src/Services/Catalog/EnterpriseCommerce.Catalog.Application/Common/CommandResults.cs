using System.Diagnostics.CodeAnalysis;

namespace EnterpriseCommerce.Catalog.Application.Common;

/// <summary>The outcome of a Catalog use case, mapped by the API to a status code and a problem type.</summary>
public enum CommandStatus
{
    /// <summary>Done (or already in effect).</summary>
    Ok,

    /// <summary>A new resource was created.</summary>
    Created,

    /// <summary>A retried creation: the resource created by the first request with the same key.</summary>
    Replayed,

    /// <summary>The caller is not an active member of the tenant, or the tenant does not exist (<c>404</c>).</summary>
    TenantNotVisible,

    /// <summary>The caller's role lacks the permission (<c>403</c>).</summary>
    PermissionDenied,

    /// <summary>The resource does not exist in this scope (<c>404</c>).</summary>
    NotFound,

    /// <summary>Validation failed (<c>400</c>), with errors per field.</summary>
    Invalid,

    SlugTaken,
    SkuTaken,
    IdempotencyKeyReused,

    /// <summary>The <c>If-Match</c> version is not the current one (<c>412</c>).</summary>
    PreconditionFailed,

    /// <summary>A concurrent write won between read and write (<c>409</c>).</summary>
    ConcurrencyConflict,

    /// <summary>The product is archived and read-only.</summary>
    ProductArchived,

    /// <summary>The product does not meet its publication requirements.</summary>
    RequirementsNotMet,

    /// <summary>The state command is not allowed from the current state.</summary>
    InvalidTransition,

    /// <summary>A category cannot be created under, or reactivated below, an unavailable parent.</summary>
    ParentUnavailable,

    /// <summary>A category would be deeper than the taxonomy allows.</summary>
    CategoryTooDeep,

    /// <summary>A category with active children cannot be retired.</summary>
    HasActiveChildren,
}

/// <summary>The result of a use case: its status and, depending on it, the resource, validation errors or unmet requirements.</summary>
public sealed record CommandResult<T>(CommandStatus Status, T? Value = default, IReadOnlyDictionary<string, string[]>? Errors = null, IReadOnlyList<string>? Unmet = null)
    where T : class
{
    [MemberNotNullWhen(true, nameof(Value))]
    public bool HasValue => Value is not null;

    public static CommandResult<T> Of(CommandStatus status) => new(status);

    public static CommandResult<T> Done(T value, CommandStatus status = CommandStatus.Ok) => new(status, value);

    public static CommandResult<T> Invalid(IReadOnlyDictionary<string, string[]> errors) => new(CommandStatus.Invalid, Errors: errors);

    public static CommandResult<T> NotMet(IReadOnlyList<string> unmet) => new(CommandStatus.RequirementsNotMet, Unmet: unmet);
}

/// <summary>Maps the store's write outcome of a replacement to a command status.</summary>
public static class WriteOutcomes
{
    public static CommandStatus ToStatus(WriteOutcome outcome) => outcome switch
    {
        WriteOutcome.Written => CommandStatus.Ok,
        WriteOutcome.VersionConflict => CommandStatus.ConcurrencyConflict,
        WriteOutcome.SlugTaken => CommandStatus.SlugTaken,
        WriteOutcome.SkuTaken => CommandStatus.SkuTaken,
        WriteOutcome.CreationKeyTaken => CommandStatus.IdempotencyKeyReused,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };
}
