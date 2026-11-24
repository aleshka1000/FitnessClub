using BusinessLogicLayer.Interfaces;
using BusinessLogicLayer.Models;
using DataAccessLayer.UnitOfWork;

namespace BusinessLogicLayer.Services;

public class AnalyticsService(IUnitOfWork unitOfWork) : IAnalyticsService
{
    public async Task<Result<LoadResult>> GetLoadAsync(LoadQuery query, CancellationToken ct = default)
    {
        if (query.To < query.From)
            return Result<LoadResult>.Failure(new BusinessError(BusinessErrorKind.Validation));

        var from = query.From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        DateTime? until = query.To == DateOnly.MaxValue
            ? null
            : query.To.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var rows = await unitOfWork.Class.GetLoadAsync(from, until, query.RoomId, ct);

        return Result<LoadResult>.Success(new LoadResult(rows
            .Select(x => new RoomLoadResult(
                x.Date,
                new RoomResult(x.RoomId, x.RoomName),
                x.ClassesCount,
                x.TotalCapacity,
                x.BookedSeats))
            .ToArray()));
    }
}
