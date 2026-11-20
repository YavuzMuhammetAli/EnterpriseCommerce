using EnterpriseCommerce.Bff.Logging;
using EnterpriseCommerce.Bff.Sessions;
using Microsoft.AspNetCore.Diagnostics;

namespace EnterpriseCommerce.Bff.Hosting;

/// <summary>
/// Unhandled exceptions become RFC 9457 problem details without any detail. A session store that cannot answer is a
/// temporary 503: the request fails closed and is never answered as authenticated or silently anonymous.
/// </summary>
internal sealed class BffExceptionHandler(IProblemDetailsService problemDetails, ILogger<BffExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var unavailable = exception is SessionStoreUnavailableException
            || exception.InnerException is SessionStoreUnavailableException;
        if (unavailable)
        {
            BffLog.SessionStoreUnavailable(logger, exception);
            httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            httpContext.Response.Headers.RetryAfter = "5";
        }
        else
        {
            BffLog.UnhandledException(logger, exception);
            httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        }

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = null,
            ProblemDetails =
            {
                Status = httpContext.Response.StatusCode,
                Title = unavailable ? "Temporarily unavailable. Retry shortly." : "An unexpected error occurred.",
            },
        });
    }
}
