using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using DataAccessLayer.Models;

namespace DataAccessLayer.Repositories;

public interface IEnrollmentRepository : IGenericRepository<Enrollment>
{
    Task<EnrollmentCardVersions?> GetCardVersionsAsync(long id, long userId, CancellationToken ct = default);

    Task<EnrollmentDetailsData?> GetDetailsAsync(long id, long userId, CancellationToken ct = default);

    Task<List<Enrollment>> GetByUserAsync(long userId, CancellationToken ct = default);

    Task<Enrollment?> GetForUserAsync(long id, long userId, CancellationToken ct = default);

    Task<long> GetActiveSeatsAsync(long classId, CancellationToken ct = default);
}
