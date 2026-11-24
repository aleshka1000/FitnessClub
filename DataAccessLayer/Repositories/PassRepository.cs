using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public class PassRepository : GenericRepository<Pass>, IPassRepository
{
    public PassRepository(DbSet<Pass> dbSet)
        : base(dbSet)
    {
    }

    public async Task<Pass?> GetByUserAsync(long userId, CancellationToken ct = default)
    {
        return await _dbSet.FirstOrDefaultAsync(x => x.UserId == userId, ct);
    }

    public Task<List<Pass>> GetByUsersAsync(IReadOnlyCollection<long> userIds, CancellationToken ct = default) =>
        _dbSet.Where(x => userIds.Contains(x.UserId)).ToListAsync(ct);
}
