using DatabaseAIAgent.Api.Middleware;
using DatabaseAIAgent.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace DatabaseAIAgent.Api.Errors;

/// <summary>
/// Converts unhandled exceptions into safe ProblemDetails responses. Details (stack traces, SQL, provider
/// messages) are written to internal logs only; the client receives a generic message plus a correlation id.
/// </summary>
internal sealed class GlobalExceptionHandler(IProblemDetailsService problemDetails, ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public const string GenericErrorMessage = "We could not execute this request. Please try again or contact your administrator.";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var correlationId = CorrelationIdMiddleware.Get(httpContext);

        var (status, title, detail) = exception switch
        {
            DomainException domain => (StatusCodes.Status400BadRequest, "The request is not valid.", domain.Message),
            BadHttpRequestException bad => (bad.StatusCode, "The request is not valid.", "The request could not be processed."),
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested => (499, "Request cancelled.", "The request was cancelled."),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.", GenericErrorMessage),
        };

        if (status >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path} (correlation {CorrelationId})", httpContext.Request.Method, httpContext.Request.Path, correlationId);
        }
        else
        {
            logger.LogInformation("Request rejected with {Status}: {ExceptionType} (correlation {CorrelationId})", status, exception.GetType().Name, correlationId);
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = detail,
            },
        });
    }
}
