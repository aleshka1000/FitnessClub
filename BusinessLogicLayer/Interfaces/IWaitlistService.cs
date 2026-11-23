using BusinessLogicLayer.Models;

namespace BusinessLogicLayer.Interfaces;

public interface IWaitlistService
{
    Task<Result<WaitlistJoinResult>> JoinAsync(long userId, JoinWaitlistCommand command,
        CancellationToken ct = default);

    Task<Result<WaitlistCancelResult>> CancelAsync(long userId, long id, CancellationToken ct = default);
}
