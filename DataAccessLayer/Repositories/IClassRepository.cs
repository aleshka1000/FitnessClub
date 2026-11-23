using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using DataAccessLayer.Models;

namespace DataAccessLayer.Repositories;

public interface IClassRepository : IGenericRepository<Class>
{
    Task<List<ScheduleClassData>> GetScheduleAsync(long userId, DateTime from, DateTime to, long? roomId,
        DateTime? afterStartsAt, long afterId, int take, CancellationToken ct = default);

    Task<List<RoomLoadData>> GetLoadAsync(DateTime from, DateTime? until, long? roomId,
        CancellationToken ct = default);

    Task<Class?> GetWithEnrollmentsAsync(long id, CancellationToken ct = default);

    Task<Class?> GetIncludingCancelledAsync(long id, CancellationToken ct = default);

    void MarkForConcurrencyCheck(Class scheduledClass);
}
