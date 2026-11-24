using BusinessLogicLayer.Models;

namespace BusinessLogicLayer.Interfaces;

public interface IScheduleService
{
    Task<Result<ScheduleResult>> GetAsync(long userId, ScheduleQuery query, CancellationToken ct = default);
}
