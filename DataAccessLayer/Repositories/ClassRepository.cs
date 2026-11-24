using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using DataAccessLayer.Models;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public class ClassRepository : GenericRepository<Class>, IClassRepository
{
    private readonly DbSet<Enrollment> _enrollments;

    public ClassRepository(DbSet<Class> dbSet, DbSet<Enrollment> enrollments)
        : base(dbSet)
    {
        _enrollments = enrollments;
    }

    public async Task<Class?> GetWithEnrollmentsAsync(long id, CancellationToken ct = default)
    {
        return await _dbSet
            .Include(x => x.Enrollments)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public Task<Class?> GetIncludingCancelledAsync(long id, CancellationToken ct = default)
    {
        return _dbSet.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id, ct);
    }

    public void MarkForConcurrencyCheck(Class scheduledClass)
    {
        // решение: UPDATE неизменённой capacity обновляет xmin занятия при изменении связанных броней.
        _dbSet.Entry(scheduledClass).Property(x => x.Capacity).IsModified = true;
    }

    public Task<List<ScheduleClassData>> GetScheduleAsync(long userId, DateTime from, DateTime to, long? roomId,
        DateTime? afterStartsAt, long afterId, int take, CancellationToken ct = default)
    {
        var query = _dbSet.AsNoTracking().Where(x => x.StartsAt >= from && x.StartsAt < to);
        if (roomId is not null) query = query.Where(x => x.RoomId == roomId);
        if (afterStartsAt is not null)
            query = query.Where(x => x.StartsAt > afterStartsAt || x.StartsAt == afterStartsAt && x.Id > afterId);

        return query.OrderBy(x => x.StartsAt).ThenBy(x => x.Id).Take(take)
            .Select(x => new ScheduleClassData(x.Id, x.Title, x.StartsAt, x.DurationMinutes,
                x.RoomId, x.Room.Name, x.Capacity,
                x.Enrollments.Where(e => e.Status == "active").Sum(e => (long)e.Seats),
                x.Enrollments.Where(e => e.Status == "active" && e.UserId == userId).Sum(e => (long)e.Seats)))
            .ToListAsync(ct);
    }

    public Task<List<RoomLoadData>> GetLoadAsync(DateTime from, DateTime? until, long? roomId,
        CancellationToken ct = default)
    {
        var query = _dbSet.AsNoTracking().Where(x => x.StartsAt >= from);
        if (until is not null) query = query.Where(x => x.StartsAt < until);
        if (roomId is not null) query = query.Where(x => x.RoomId == roomId);

        var booked = from enrollment in _enrollments
            join scheduledClass in query on enrollment.ClassId equals scheduledClass.Id
            where enrollment.Status == "active"
            group enrollment by enrollment.ClassId into bookings
            select new { ClassId = bookings.Key, Seats = bookings.Sum(x => x.Seats) };
        return (from scheduledClass in query
                join total in booked on scheduledClass.Id equals total.ClassId into totals
                from total in totals.DefaultIfEmpty()
                select new
            {
                Date = DateOnly.FromDateTime(scheduledClass.StartsAt), scheduledClass.RoomId,
                RoomName = scheduledClass.Room.Name, scheduledClass.Capacity,
                BookedSeats = (int?)total.Seats ?? 0
            })
            .GroupBy(x => new { x.Date, x.RoomId, x.RoomName })
            .OrderBy(g => g.Key.Date).ThenBy(g => g.Key.RoomId)
            .Select(g => new RoomLoadData(g.Key.Date, g.Key.RoomId, g.Key.RoomName,
                g.Count(), g.Sum(x => x.Capacity), g.Sum(x => x.BookedSeats)))
            .ToListAsync(ct);
    }
}
