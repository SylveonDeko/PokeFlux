using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace EeveeCore.Controllers.Common;

/// <summary>
///     Base for the PokeFlux dashboard API controllers. Every request needs the API key.
/// </summary>
[ApiController]
[Authorize(ApiConstants.ApiKeyPolicy)]
public abstract class PokeFluxControllerBase : ControllerBase, IActionFilter
{
    /// <summary>
    ///     Rejects requests whose route values, query or body could not be read.
    /// </summary>
    /// <param name="context">The action context.</param>
    [NonAction]
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (ModelState.IsValid)
            return;

        var message = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)
            .FirstOrDefault(m => !string.IsNullOrEmpty(m));
        context.Result = Error(StatusCodes.Status400BadRequest, message ?? "The request is not valid.");
    }

    /// <inheritdoc />
    [NonAction]
    public void OnActionExecuted(ActionExecutedContext context)
    {
    }

    /// <summary>
    ///     Returns an <c>{ "error": message }</c> response with the given status code.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="message">The error message.</param>
    protected ObjectResult Error(int statusCode, string message) => StatusCode(statusCode, new ApiError(message));

    /// <summary>
    ///     Returns the message of a successful game action, or a 400 error with its message.
    /// </summary>
    /// <param name="result">The result of a service call.</param>
    protected IActionResult FromResult((bool Success, string Message) result) =>
        result.Success ? Ok(new ApiMessage(result.Message)) : Error(StatusCodes.Status400BadRequest, result.Message);
}

/// <summary>
///     Base for controllers acting for a dashboard user, who is identified by the verified token.
/// </summary>
[DashUser]
public abstract class PokeFluxUserControllerBase : PokeFluxControllerBase
{
    /// <summary>
    ///     Gets the Fluxer id of the user, taken from the verified token's <c>sub</c> claim.
    /// </summary>
    protected ulong UserId => (ulong)HttpContext.Items[DashUserAttribute.UserIdKey]!;
}

/// <summary>
///     An error response.
/// </summary>
/// <param name="Error">What went wrong.</param>
public record ApiError(string Error);

/// <summary>
///     The message of a successful action.
/// </summary>
/// <param name="Message">What happened.</param>
public record ApiMessage(string Message);
