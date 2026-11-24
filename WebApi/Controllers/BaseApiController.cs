using System.Security.Claims;
using BusinessLogicLayer.Models;
using Microsoft.AspNetCore.Mvc;

namespace WebApi.Controllers;

public abstract class BaseApiController : ControllerBase
{
    protected const string AdminRole = "admin";

    protected long CurrentUserId => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    protected bool IsAdmin => User.IsInRole(AdminRole);

    protected ObjectResult BusinessProblem(BusinessError error)
    {
        var (status, title) = error.Kind switch
        {
            BusinessErrorKind.Validation => (400, "Invalid request"),
            BusinessErrorKind.NotFound => (404, "Not found"),
            BusinessErrorKind.Conflict => (409, "Operation cannot be completed"),
            BusinessErrorKind.Unavailable => (503, "Temporarily unavailable"),
            _ => throw new ArgumentOutOfRangeException(nameof(error))
        };

        var details = new ProblemDetails { Status = status, Title = title };

        if (error.Reason is not null)
        {
            details.Extensions["reason"] = error.Reason;
        }

        return new ObjectResult(details)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" }
        };
    }
}
