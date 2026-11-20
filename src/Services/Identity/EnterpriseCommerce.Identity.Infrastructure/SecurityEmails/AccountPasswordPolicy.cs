using EnterpriseCommerce.Identity.Application.Accounts;
using EnterpriseCommerce.Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;

namespace EnterpriseCommerce.Identity.Infrastructure.SecurityEmails;

/// <summary>Evaluates the configured ASP.NET Core Identity password validators, independently of any account.</summary>
internal sealed class AccountPasswordPolicy(UserManager<ApplicationUser> users) : IAccountPasswordPolicy
{
    public async Task<IReadOnlyList<string>> ValidateAsync(string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);

        var errors = new List<string>();
        foreach (var validator in users.PasswordValidators)
        {
            var result = await validator.ValidateAsync(users, new ApplicationUser(), password);
            errors.AddRange(result.Errors.Select(error => error.Code));
        }

        return errors;
    }
}

/// <summary>Hashes passwords with the configured ASP.NET Core Identity hasher (PBKDF2 by default).</summary>
internal sealed class AccountPasswordHasher(IPasswordHasher<ApplicationUser> hasher) : IAccountPasswordHasher
{
    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        // The default hasher does not use the user instance.
        return hasher.HashPassword(new ApplicationUser(), password);
    }
}
