using CallCenter.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace CallCenter.Api.Filters;

/// <summary>
/// Turns a broken business rule into a standard <c>ProblemDetails</c> response.
///
/// This is the seam between the domain's language and HTTP's. The domain throws
/// <see cref="DomainException"/> and knows nothing about status codes; this filter, registered
/// globally in <see cref="Startup"/>, is the single place that decides such a refusal is a 400.
/// That is why no action in this project has a try/catch, and why a new action cannot forget the
/// error contract.
/// </summary>
public class DomainExceptionFilter : IExceptionFilter
{
    private readonly ProblemDetailsFactory _problemDetailsFactory;
    private readonly ILogger<DomainExceptionFilter> _logger;

    public DomainExceptionFilter(
        ProblemDetailsFactory problemDetailsFactory,
        ILogger<DomainExceptionFilter> logger)
    {
        _problemDetailsFactory = problemDetailsFactory;
        _logger = logger;
    }

    public void OnException(ExceptionContext context)
    {
        // Anything that is not a broken rule is a genuine fault, and is left to the framework so
        // it is logged as an error and returns a 500 rather than being quietly swallowed.
        if (context.Exception is not DomainException exception)
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
