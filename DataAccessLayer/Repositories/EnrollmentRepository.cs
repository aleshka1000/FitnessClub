using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public class EnrollmentRepository : GenericRepository<Enrollment>, IEnrollmentRepository
{
    public EnrollmentRepository(DbSet<Enrollment> dbSet)
        : base(dbSet)
    {
    }

    public async Task<List<Enrollment>> GetByUserAsync(long userId, CancellationToken ct = default)
    {
        return await _dbSet
            .Where(x => x.UserId == userId)
            .ToListAsync(ct);
    }

    public Task<Enrollment?> GetForUserAsync(long id, long userId, CancellationToken ct = default)
    {
        return _dbSet.SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
    }

    public Task<long> GetActiveSeatsAsync(long classId, CancellationToken ct = default)
    {
        return _dbSet.Where(x => x.ClassId == classId && x.Status == "active")
            .SumAsync(x => (long)x.Seats, ct);
    }
}
