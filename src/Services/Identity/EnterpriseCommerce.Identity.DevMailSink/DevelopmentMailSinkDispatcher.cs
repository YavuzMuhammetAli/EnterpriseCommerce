using System.Net.Mail;
using System.Net.Sockets;
using EnterpriseCommerce.Identity.Application.SecurityEmails;
using EnterpriseCommerce.Identity.Domain.Security;
using EnterpriseCommerce.Identity.Domain.SecurityEmails;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace EnterpriseCommerce.Identity.DevMailSink;

/// <summary>
/// DEVELOPMENT ONLY. Sends security emails over plain SMTP to a local mail catcher. It is never part of a Release build.
/// The guard in Identity.Api has already verified the environment, the opt-in and the host before registration.
/// </summary>
internal sealed partial class DevelopmentMailSinkDispatcher(string host, int port, ILogger<DevelopmentMailSinkDispatcher> logger) : ISecurityEmailDispatcher
{
    public async Task<SecurityEmailDispatchOutcome> DispatchAsync(SecurityEmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var mail = new MailMessage("no-reply@enterprisecommerce.local", message.RecipientEmail)
        {
            Subject = message.Purpose == SecurityTokenPurpose.PasswordReset ? "Reset your password" : "Confirm your email address",
            Body = message.Purpose == SecurityTokenPurpose.PasswordReset
                ? $"Open this link within 30 minutes to choose a new password:{Environment.NewLine}{message.Link.AbsoluteUri}"
                : $"Open this link to confirm your email address:{Environment.NewLine}{message.Link.AbsoluteUri}",
        };
        mail.Headers.Add("X-Delivery-Id", message.DeliveryId.ToString());

        // SmtpClient is acceptable for a local, unauthenticated mail catcher; it never reaches a real mail server.
        using var client = new SmtpClient(host, port) { EnableSsl = false, DeliveryMethod = SmtpDeliveryMethod.Network };
        try
        {
            await client.SendMailAsync(mail, cancellationToken);
            LogDelivered(logger, message.DeliveryId);
            return SecurityEmailDispatchOutcome.Delivered;
        }
        catch (SmtpException exception) when (exception.InnerException is SocketException)
        {
            LogUnreachable(logger, message.DeliveryId);
            return SecurityEmailDispatchOutcome.TransientFailure;
        }
        catch (SmtpException exception)
        {
            LogFailed(logger, message.DeliveryId, exception.StatusCode);
            return SecurityEmailDispatchOutcome.Unknown;
        }
    }

    [LoggerMessage(EventId = 9000, Level = LogLevel.Information, Message = "DEVELOPMENT mail sink: security email delivery {DeliveryId} written to the local mail catcher")]
    private static partial void LogDelivered(ILogger logger, Guid deliveryId);

    [LoggerMessage(EventId = 9001, Level = LogLevel.Warning, Message = "DEVELOPMENT mail sink unreachable; delivery {DeliveryId} not sent (start the Compose profile 'mailsink')")]
    private static partial void LogUnreachable(ILogger logger, Guid deliveryId);

    [LoggerMessage(EventId = 9002, Level = LogLevel.Warning, Message = "DEVELOPMENT mail sink rejected delivery {DeliveryId}: {StatusCode}")]
    private static partial void LogFailed(ILogger logger, Guid deliveryId, SmtpStatusCode statusCode);
}

/// <summary>Registration of the development mail sink. Called only from <c>#if DEBUG</c> code after the runtime guard.</summary>
public static class DevelopmentMailSinkRegistration
{
    /// <summary>Replaces every registered <see cref="ISecurityEmailDispatcher"/>, so exactly one remains.</summary>
    public static IServiceCollection AddDevelopmentMailSink(this IServiceCollection services, string host, int port)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        services.RemoveAll<ISecurityEmailDispatcher>();
        services.AddSingleton<ISecurityEmailDispatcher>(provider => new DevelopmentMailSinkDispatcher(
            host,
            port,
            provider.GetRequiredService<ILogger<DevelopmentMailSinkDispatcher>>()));
        return services;
    }
}
