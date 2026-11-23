using BusinessLogicLayer.Models;

namespace BusinessLogicLayer.Interfaces;

public interface IEnrollmentService
{
    Task<Result<EnrollmentDetailsResult>> GetAsync(long userId, long id, CancellationToken ct = default);

    Task<Result<EnrollmentDetailsResult>> ChangeSeatsAsync(long userId, long id, int seats,
        CancellationToken ct = default);

    Task<Result<EnrollmentCreationResult>> CreateAsync(long userId, CreateEnrollmentCommand command,
        CancellationToken ct = default);

    Task<Result<EnrollmentCancellationResult>> CancelAsync(long userId, long id, CancellationToken ct = default);
}
