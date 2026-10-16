using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DatabaseAIAgent.Api.Validation;

/// <summary>Runs the registered FluentValidation validator for every action argument before the action executes.</summary>
internal sealed class ValidationFilter(IServiceProvider services) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null || services.GetService(typeof(IValidator<>).MakeGenericType(argument.GetType())) is not IValidator validator)
            {
                continue;
            }

            var result = await validator.ValidateAsync(new ValidationContext<object>(argument), context.HttpContext.RequestAborted);
            if (!result.IsValid)
            {
                var errors = result.Errors
                    .GroupBy(e => JsonNamingPolicy.CamelCase.ConvertName(e.PropertyName))
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());

                var problem = new ValidationProblemDetails(errors)
                {
                    Title = "Please correct the highlighted fields.",
                    Status = StatusCodes.Status400BadRequest,
                };
                problem.Extensions["code"] = "validation_failed";
                context.Result = new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
                return;
            }
        }

        await next();
    }
}
