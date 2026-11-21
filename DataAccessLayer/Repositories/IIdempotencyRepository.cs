using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;

public record IdempotencyClaim(IdempotencyRequest Request, bool IsNew);

public interface IIdempotencyRepository
{
    Task<IdempotencyClaim> ClaimAsync(long userId, string operation, string key, byte[] keyHash,
        long classId, int seats, DateTime createdAt, CancellationToken ct = default);
}
