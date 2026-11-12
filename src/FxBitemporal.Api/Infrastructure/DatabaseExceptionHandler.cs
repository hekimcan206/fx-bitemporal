using FxBitemporal.Api.Persistence;
using FxBitemporal.Sources;
using Microsoft.AspNetCore.Diagnostics;
using Npgsql;

namespace FxBitemporal.Api.Infrastructure;

/// <summary>
/// Maps expected failures to problem details: lock waits and outages become a retryable 503, a known time in the future
/// a 400, and an upstream rate source failure a 502. Anything else stays a 500 without internals in the body.
/// </summary>
internal sealed class DatabaseExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            FutureKnownTimeException future => (StatusCodes.Status400BadRequest,
                "knownAt is in the future.", $"{future.Message} An answer about future knowledge could still change, so it is refused."),
            RateSourceException source => (StatusCodes.Status502BadGateway, "The rate source failed.", source.Message),
            PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected } =>
                (StatusCodes.Status503ServiceUnavailable, "Temporarily unavailable, retry.", null),
            NpgsqlException { IsTransient: true } or TimeoutException =>
                (StatusCodes.Status503ServiceUnavailable, "Temporarily unavailable, retry.", null),
            _ => (0, null, null),
        };

        if (status == 0)
        {
            return false;
        }

        httpContext.Response.StatusCode = status;
        if (status == StatusCodes.Status503ServiceUnavailable)
        {
            httpContext.Response.Headers.RetryAfter = "1";
        }

        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = { Status = status, Title = title, Detail = detail },
            Exception = exception,
        });
    }
}
