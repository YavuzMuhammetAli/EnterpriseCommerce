using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace EnterpriseCommerce.Identity.Api.ExceptionHandling;

/// <summary>
/// Last-resort handler for unhandled exceptions.
/// Logs the exception and returns a generic 500 problem details response
/// without leaking exception messages or stack traces to the client.
/// Mapping of domain/application exceptions to specific status codes is added
/// when those exception types exist.
/// </summary>
internal sealed partial class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        LogUnhandledException(logger, exception, httpContext.Request.Method, httpContext.Request.Path);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
                Type = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
            },
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
}
