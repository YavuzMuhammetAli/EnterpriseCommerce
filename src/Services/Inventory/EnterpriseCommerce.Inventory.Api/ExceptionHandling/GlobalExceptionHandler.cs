using EnterpriseCommerce.Inventory.Application.Authorization;
using EnterpriseCommerce.Inventory.Application.Catalog;
using EnterpriseCommerce.Inventory.Application.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace EnterpriseCommerce.Inventory.Api.ExceptionHandling;

/// <summary>
/// Last-resort handler for unhandled exceptions. Never leaks exception messages or stack traces to the client.
/// </summary>
/// <remarks>
/// Every dependency failure answers <c>503</c> with <c>Retry-After</c>, and nothing is reported as done
/// (INVENTORY_OPERATIONS.md section 6):
/// <list type="bullet">
/// <item><see cref="DataStoreUnavailableException"/>: SQL Server could not complete the operation, a lock wait exceeded
/// its bound, or deadlock retries were exhausted.</item>
/// <item><see cref="MembershipUnavailableException"/>: no merchant request proceeds without Tenant &amp; Merchant's
/// verdict (ADR-025).</item>
/// <item><see cref="CatalogUnavailableException"/>: no item is created without Catalog's verdict (ADR-027).</item>
/// </list>
/// Everything else becomes a generic <c>500</c>.
/// </remarks>
internal sealed partial class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    private const string TypeBase = "https://errors.enterprisecommerce.example/inventory/";

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
            // content type or a value of the wrong type. The client gets the same problem, without parser details.
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
            problem = Unavailable(httpContext, "The service is temporarily unavailable. Try again later.", "https://tools.ietf.org/html/rfc9110#section-15.6.4");
        }
        else if (exception is MembershipUnavailableException)
        {
            // The exception is not logged: its details are already logged, without the subject, by the membership client.
            LogDependencyUnavailable(logger, "Tenant & Merchant", httpContext.Request.Method, httpContext.Request.Path);
            problem = Unavailable(httpContext, "Your merchant membership could not be verified right now. Try again later.", TypeBase + "membership-unavailable");
        }
        else if (exception is CatalogUnavailableException)
        {
            LogDependencyUnavailable(logger, "Catalog", httpContext.Request.Method, httpContext.Request.Path);
            problem = Unavailable(httpContext, "The product could not be verified with the catalog right now. Try again later.", TypeBase + "catalog-unavailable");
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

        httpContext.Response.StatusCode = problem.Status!.Value;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
        });
    }

    private static ProblemDetails Unavailable(HttpContext httpContext, string title, string type)
    {
        httpContext.Response.Headers.RetryAfter = "5";
        return new ProblemDetails { Status = StatusCodes.Status503ServiceUnavailable, Title = title, Type = type };
    }

    [LoggerMessage(EventId = 5000, Level = LogLevel.Error, Message = "Unhandled exception while processing {RequestMethod} {RequestPath}")]
    private static partial void LogUnhandledException(ILogger logger, Exception exception, string requestMethod, PathString requestPath);

    [LoggerMessage(
        EventId = 5001,
        Level = LogLevel.Error,
        Message = "Inventory database unavailable while processing {RequestMethod} {RequestPath}; answered 503")]
    private static partial void LogDataStoreUnavailable(ILogger logger, Exception exception, string requestMethod, PathString requestPath);

    [LoggerMessage(
        EventId = 5002,
        Level = LogLevel.Error,
        Message = "{Dependency} unavailable while processing {RequestMethod} {RequestPath}; answered 503")]
    private static partial void LogDependencyUnavailable(ILogger logger, string dependency, string requestMethod, PathString requestPath);
}
