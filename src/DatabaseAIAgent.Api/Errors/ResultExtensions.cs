using DatabaseAIAgent.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace DatabaseAIAgent.Api.Errors;

/// <summary>Maps expected application errors to ProblemDetails responses with a stable <c>code</c> extension.</summary>
internal static class ResultExtensions
{
    public static ActionResult ToProblem(this ControllerBase controller, Error error)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(error);

        var status = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.TooManyRequests => StatusCodes.Status429TooManyRequests,
            _ => StatusCodes.Status400BadRequest,
        };

        ProblemDetails problem = error.Fields is { Count: > 0 } fields
            ? new ValidationProblemDetails(fields.ToDictionary(f => f.Key, f => f.Value)) { Title = error.Message, Status = status }
            : new ProblemDetails { Title = error.Message, Status = status };
        problem.Detail = error.Message;
        problem.Extensions["code"] = error.Code;

        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }

    public static ActionResult<T> ToActionResult<T>(this ControllerBase controller, Result<T> result) =>
        result.IsSuccess ? controller.Ok(result.Value) : controller.ToProblem(result.Error);

    public static ActionResult ToNoContent(this ControllerBase controller, Result result) =>
        result.IsSuccess ? controller.NoContent() : controller.ToProblem(result.Error);
}
