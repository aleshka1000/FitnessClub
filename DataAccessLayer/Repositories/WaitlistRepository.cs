using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public class WaitlistRepository(DbSet<WaitlistEntry> dbSet) : GenericRepository<WaitlistEntry>(dbSet), IWaitlistRepository
{
    public Task<bool> IsWaitingAsync(long userId, long classId, CancellationToken ct = default) =>
        _dbSet.AnyAsync(x => x.UserId == userId && x.ClassId == classId && x.Status == "waiting", ct);

    public Task<WaitlistEntry?> GetForUserAsync(long id, long userId, CancellationToken ct = default) =>
        _dbSet.SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);

    public Task<WaitlistSnapshot?> GetWithPositionAsync(long id, long userId, CancellationToken ct = default) =>
        _dbSet.AsNoTracking().Where(x => x.Id == id && x.UserId == userId)
            .Select(x => new WaitlistSnapshot(x, _dbSet.Count(other => other.ClassId == x.ClassId
                && other.Status == "waiting" && (other.CreatedAt < x.CreatedAt
                    || other.CreatedAt == x.CreatedAt && other.Id <= x.Id))))
            .SingleOrDefaultAsync(ct);

    public Task<List<WaitlistEntry>> GetWaitingBatchAsync(long classId, DateTime? afterCreatedAt, long afterId,
        CancellationToken ct = default)
    {
        var query = _dbSet.Where(x => x.ClassId == classId && x.Status == "waiting");
        if (afterCreatedAt.HasValue)
        {
            query = query.Where(x => x.CreatedAt > afterCreatedAt.Value
                || x.CreatedAt == afterCreatedAt.Value && x.Id > afterId);
        }
        return query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Take(32).ToListAsync(ct);
    }
}
