using BusinessLogicLayer.Models;

namespace BusinessLogicLayer.Interfaces;

public interface IEnrollmentService
{
    Task<Result<EnrollmentCreationResult>> CreateAsync(long userId, CreateEnrollmentCommand command,
        CancellationToken ct = default);
}
