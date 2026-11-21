using System.ComponentModel.DataAnnotations;
using BusinessLogicLayer.Interfaces;
using BusinessLogicLayer.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using WebApi.Models.Enrollments;
using WebApi.RateLimiting;

namespace WebApi.Controllers;

[ApiController]
[Authorize]
[UserRateLimited]
[Route("enrollments")]
public class EnrollmentsController : BaseApiController
{
    private readonly IEnrollmentService _enrollmentService;
    private readonly IWaitlistService _waitlistService;

    public EnrollmentsController(IEnrollmentService enrollmentService, IWaitlistService waitlistService)
    {
        _enrollmentService = enrollmentService;
        _waitlistService = waitlistService;
    }

    [HttpPost]
    [ProducesResponseType(typeof(CreateEnrollmentResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Create(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow), Required] CreateEnrollmentRequest? request,
        [FromHeader(Name = "Idempotency-Key"), Required] string? idempotencyKey, CancellationToken ct)
    {
        var result = await _enrollmentService.CreateAsync(CurrentUserId,
            new CreateEnrollmentCommand(request!.ClassId!.Value, request.Seats!.Value, idempotencyKey!), ct);

        if (!result.IsSuccess)
        {
            return BusinessProblem(result.Error);
        }

        var value = result.Value;
        return StatusCode(StatusCodes.Status201Created,
            new CreateEnrollmentResponse(value.Id, value.ClassId, value.Seats, value.Status, value.CreatedAt));
    }
}
