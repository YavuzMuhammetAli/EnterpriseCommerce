using EnterpriseCommerce.Identity.Application.Security;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace EnterpriseCommerce.Identity.Api.ExceptionHandling;

/// <summary>
/// Last-resort handler for unhandled exceptions. Never leaks exception messages or stack traces to the client.
/// </summary>
/// <remarks>
/// <see cref="SecurityStateUnavailableException"/> becomes <c>503</c>: the authoritative security state could not be
/// confirmed, so nothing was issued or reported as done (ADR-017 section 8). Everything else becomes a generic <c>500</c>.
/// </remarks>
internal sealed partial class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ProblemDetails problem;

        if (exception is SecurityStateUnavailableException)
        {
            LogSecurityStateUnavailable(logger, exception, httpContext.Request.Method, httpContext.Request.Path);
            httpContext.Response.Headers.RetryAfter = "5";
            problem = new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "The service is temporarily unavailable. Try again later.",
                Type = "https://tools.ietf.org/html/rfc9110#section-15.6.4",
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

    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Error,
        Message = "Unhandled exception while processing {RequestMethod} {RequestPath}")]
    private static partial void LogUnhandledException(
        ILogger logger,
        Exception exception,
        string requestMethod,
        PathString requestPath);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Error,
        Message = "Security state unavailable while processing {RequestMethod} {RequestPath}; failed closed with 503")]
    private static partial void LogSecurityStateUnavailable(
        ILogger logger,
        Exception exception,
        string requestMethod,
        PathString requestPath);
}
