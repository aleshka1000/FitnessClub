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
[Authorize(Roles = AdminRole)]
[UserRateLimited]
[Route("analytics")]
public class AnalyticsController : BaseApiController
{
    private readonly IAnalyticsService _analyticsService;

    public AnalyticsController(IAnalyticsService analyticsService)
    {
        _analyticsService = analyticsService;
    }

    [HttpGet("load")]
    [ProducesResponseType(typeof(LoadResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLoad(
        [FromQuery, Required, ModelBinder(BinderType = typeof(QueryDateModelBinder))] DateOnly? from,
        [FromQuery, Required, ModelBinder(BinderType = typeof(QueryDateModelBinder))] DateOnly? to,
        [FromQuery] long? roomId, CancellationToken ct)
    {
        var result = await _analyticsService.GetLoadAsync(new LoadQuery(from!.Value, to!.Value, roomId), ct);
        if (!result.IsSuccess) return BusinessProblem(result.Error);
        return Ok(new LoadResponse(result.Value.Items.Select(x => new RoomLoadResponse(x.Date,
            new RoomResponse(x.Room.Id, x.Room.Name), x.ClassesCount, x.TotalCapacity, x.BookedSeats)).ToArray()));
    }
}
