using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public class IdempotencyRepository(ClubDbContext dbContext) : IIdempotencyRepository
{
    public async Task<IdempotencyClaim> ClaimAsync(
        long userId,
        string operation,
        string key,
        byte[] keyHash,
        long classId,
        int seats,
        DateTime createdAt,
        CancellationToken ct = default)
    {
        var inserted = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO idempotency_requests
                (user_id, operation, original_key, key_hash, class_id, seats, created_at)
            VALUES ({userId}, {operation}, {key}, {keyHash}, {classId}, {seats}, {createdAt})
            ON CONFLICT (user_id, operation, key_hash) DO NOTHING
            """, ct);

        var request = await dbContext.IdempotencyRequests.SingleAsync(
            x => x.UserId == userId && x.Operation == operation && x.KeyHash == keyHash, ct);

        return new IdempotencyClaim(request, inserted == 1);
    }
}
