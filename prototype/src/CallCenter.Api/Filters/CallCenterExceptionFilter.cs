using CallCenter.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace CallCenter.Api.Filters;

/// <summary>
/// Turns a refused command into a standard <c>ProblemDetails</c> response.
///
/// This is registered globally in <see cref="Startup"/>, which is why no action in this project
/// has a try/catch in it. A rule broken deep in the domain surfaces as the same HTTP response
/// wherever it is thrown, and adding a new action cannot accidentally forget to handle it.
/// </summary>
public class CallCenterExceptionFilter : IExceptionFilter
{
    private readonly ProblemDetailsFactory _problemDetailsFactory;
    private readonly ILogger<CallCenterExceptionFilter> _logger;

    public CallCenterExceptionFilter(
        ProblemDetailsFactory problemDetailsFactory,
        ILogger<CallCenterExceptionFilter> logger)
    {
        _problemDetailsFactory = problemDetailsFactory;
        _logger = logger;
    }

    public void OnException(ExceptionContext context)
    {
        // Anything that is not a domain refusal is a genuine fault, and is left to the framework
        // so it is logged as an error and returns a 500 rather than being quietly swallowed.
        if (context.Exception is not CallCenterException exception)
        {
            return;
        }

        _logger.LogInformation("Command refused: {Reason}", exception.Message);

        var problemDetails = _problemDetailsFactory.CreateProblemDetails(
            context.HttpContext,
            statusCode: StatusCodes.Status400BadRequest,
            title: "The command was refused",
            detail: exception.Message);

        context.Result = new ObjectResult(problemDetails)
        {
            StatusCode = StatusCodes.Status400BadRequest
        };

        context.ExceptionHandled = true;
    }
}
