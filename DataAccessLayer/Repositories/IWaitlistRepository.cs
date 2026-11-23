using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;

public sealed record WaitlistSnapshot(WaitlistEntry Entry, int Position);

public interface IWaitlistRepository : IGenericRepository<WaitlistEntry>
{
    Task<bool> IsWaitingAsync(long userId, long classId, CancellationToken ct = default);
    Task<WaitlistEntry?> GetForUserAsync(long id, long userId, CancellationToken ct = default);
    Task<WaitlistSnapshot?> GetWithPositionAsync(long id, long userId, CancellationToken ct = default);
    Task<List<WaitlistEntry>> GetWaitingBatchAsync(long classId, DateTime? afterCreatedAt, long afterId,
        CancellationToken ct = default);
}
