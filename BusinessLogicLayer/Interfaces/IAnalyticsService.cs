using BusinessLogicLayer.Models;

namespace BusinessLogicLayer.Interfaces;

public interface IAnalyticsService
{
    Task<Result<LoadResult>> GetLoadAsync(LoadQuery query, CancellationToken ct = default);
}
