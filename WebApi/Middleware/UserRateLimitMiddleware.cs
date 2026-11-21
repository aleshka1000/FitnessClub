using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using WebApi.RateLimiting;

namespace WebApi.Middleware;

public sealed class UserRateLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IUserRateLimiter rateLimiter,
        IProblemDetailsService problemDetailsService)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<UserRateLimitedAttribute>() is null
            || context.User.Identity?.IsAuthenticated != true
            || !long.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            await next(context);
            return;
        }

        if (await rateLimiter.TryAcquireAsync(userId, context.RequestAborted))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many requests"
            }
        });
    }
}
