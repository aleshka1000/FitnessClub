using BusinessLogicLayer.Interfaces;
using BusinessLogicLayer.Models;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApi.RateLimiting;
using WebApi.Models;
using WebApi.Models.Enrollments;
using WebApi.ModelBinding;

namespace WebApi.Controllers;

[ApiController]
[Authorize]
[UserRateLimited]
[Route("schedule")]
public class ScheduleController : BaseApiController
{
    private readonly IScheduleService _scheduleService;

    public ScheduleController(IScheduleService scheduleService)
    {
        _scheduleService = scheduleService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(ScheduleResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(
        [FromQuery, Required, ModelBinder(BinderType = typeof(QueryDateModelBinder))] DateTime? from,
        [FromQuery, Required, ModelBinder(BinderType = typeof(QueryDateModelBinder))] DateTime? to,
        [FromQuery] long? roomId,
        [FromQuery] int limit = 20,
        [FromQuery] string? continuationToken = null,
        CancellationToken ct = default)
    {
        if (Request.Query.ContainsKey("continuationToken") && string.IsNullOrWhiteSpace(continuationToken))
            return BusinessProblem(new BusinessError(BusinessErrorKind.Validation));

        var result = await _scheduleService.GetAsync(CurrentUserId,
            new ScheduleQuery(from!.Value, to!.Value, roomId, limit, continuationToken), ct);

        if (!result.IsSuccess)
            return BusinessProblem(result.Error);

        return Ok(new ScheduleResponse(result.Value.Items.Select(x => new ScheduleItemResponse(x.ClassId,
                        x.Title, x.StartsAt, x.DurationMinutes, new RoomResponse(x.Room.Id, x.Room.Name),
                        x.Capacity, x.FreeSeats, x.MySeats)).ToArray(), result.Value.ContinuationToken));
    }
}
