using EnterpriseCommerce.Catalog.Application.Authorization;
using EnterpriseCommerce.Catalog.Application.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace EnterpriseCommerce.Catalog.Api.ExceptionHandling;

/// <summary>
/// Last-resort handler for unhandled exceptions. Never leaks exception messages or stack traces to the client.
/// </summary>
/// <remarks>
/// <see cref="DataStoreUnavailableException"/> becomes <c>503</c> with <c>Retry-After</c>: MongoDB could not complete
/// the operation, so nothing is reported as done. <see cref="MembershipUnavailableException"/> becomes <c>503</c> too:
/// no merchant request proceeds without Tenant &amp; Merchant's verdict (ADR-025). Everything else becomes a generic
/// <c>500</c>.
/// </remarks>
internal sealed partial class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails problem;

        if (exception is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
        {
            problem = new ProblemDetails
            {
                Status = StatusCodes.Status413PayloadTooLarge,
                Title = "The request body is too large.",
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.14",
            };
        }
        else if (exception is BadHttpRequestException badRequest)
        {
            // A request that could not be bound: malformed JSON, a member the contract does not define, a wrong
            // content type or a query value of the wrong type. The client gets the same problem, without parser details.
            problem = new ProblemDetails
            {
                Status = badRequest.StatusCode,
                Title = "The request is malformed.",
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
            };
        }
        else if (exception is DataStoreUnavailableException)
        {
            LogDataStoreUnavailable(logger, exception, httpContext.Request.Method, httpContext.Request.Path);
            httpContext.Response.Headers.RetryAfter = "5";
            problem = new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "The service is temporarily unavailable. Try again later.",
                Type = "https://tools.ietf.org/html/rfc9110#section-15.6.4",
            };
        }
        else if (exception is MembershipUnavailableException)
        {
            LogMembershipUnavailable(logger, httpContext.Request.Method, httpContext.Request.Path);
            httpContext.Response.Headers.RetryAfter = "5";
            problem = new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Your merchant membership could not be verified right now. Try again later.",
                Type = "https://errors.enterprisecommerce.example/catalog/membership-unavailable",
            };
        }
        else
        {
            LogUnhandledException(logger, exception, httpContext.Request.Method, httpContext.Request.Path);
            problem = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
                Type = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
            };
        }

        httpContext.Response.StatusCode = problem.Status.Value;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
        });
    }

    [LoggerMessage(EventId = 1000, Level = LogLevel.Error, Message = "Unhandled exception while processing {RequestMethod} {RequestPath}")]
    private static partial void LogUnhandledException(ILogger logger, Exception exception, string requestMethod, PathString requestPath);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Error,
        Message = "Catalog database unavailable while processing {RequestMethod} {RequestPath}; answered 503")]
    private static partial void LogDataStoreUnavailable(ILogger logger, Exception exception, string requestMethod, PathString requestPath);

    // The exception is not logged: its details are already logged, without the subject, by the membership client.
    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Error,
        Message = "Membership verification unavailable while processing {RequestMethod} {RequestPath}; answered 503")]
    private static partial void LogMembershipUnavailable(ILogger logger, string requestMethod, PathString requestPath);
}
