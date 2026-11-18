namespace EnterpriseCommerce.Identity.Application.Security;

/// <summary>The result of one authoritative unit of work, and whether its changes are committed.</summary>
public readonly record struct TransactionDecision<TResult>(bool ShouldCommit, TResult Result);

/// <summary>Factory methods for <see cref="TransactionDecision{TResult}"/>.</summary>
public static class TransactionDecision
{
    public static TransactionDecision<TResult> Commit<TResult>(TResult result) => new(true, result);

    public static TransactionDecision<TResult> Rollback<TResult>(TResult result) => new(false, result);
}
