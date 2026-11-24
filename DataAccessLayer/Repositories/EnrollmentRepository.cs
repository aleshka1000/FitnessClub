using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using DataAccessLayer.Models;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public class EnrollmentRepository : GenericRepository<Enrollment>, IEnrollmentRepository
{
    private readonly DbSet<Pass> _passes;

    public EnrollmentRepository(DbSet<Enrollment> dbSet, DbSet<Pass> passes)
        : base(dbSet)
    {
        _passes = passes;
    }

    public Task<EnrollmentCardVersions?> GetCardVersionsAsync(long id, long userId, CancellationToken ct = default)
    {
        return (from enrollment in _dbSet.AsNoTracking()
                join pass in _passes on enrollment.UserId equals pass.UserId
                where enrollment.Id == id && enrollment.UserId == userId
                select new EnrollmentCardVersions(enrollment.Version, pass.Version))
            .SingleOrDefaultAsync(ct);
    }

    public Task<EnrollmentDetailsData?> GetDetailsAsync(long id, long userId, CancellationToken ct = default)
    {
        return (from enrollment in _dbSet.IgnoreQueryFilters().AsNoTracking()
                join pass in _passes on enrollment.UserId equals pass.UserId
                where enrollment.Id == id && enrollment.UserId == userId
                select new EnrollmentDetailsData(enrollment.Id, enrollment.Status, enrollment.Seats,
                    enrollment.CreatedAt, enrollment.ClassId, enrollment.Class.Title, enrollment.Class.StartsAt,
                    enrollment.Class.DurationMinutes, enrollment.Class.IsCancelled,
                    enrollment.Class.RoomId, enrollment.Class.Room.Name, pass.RemainingVisits, pass.ValidUntil,
                    enrollment.Version, pass.Version))
            .SingleOrDefaultAsync(ct);
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
