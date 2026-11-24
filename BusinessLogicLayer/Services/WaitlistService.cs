using System.Security.Cryptography;
using System.Text;
using BusinessLogicLayer.Interfaces;
using BusinessLogicLayer.Models;
using Common.Database.UnitOfWork;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using DataAccessLayer.UnitOfWork;

namespace BusinessLogicLayer.Services;

public class WaitlistService(
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    IWaitlistPromotionService promotionService) : IWaitlistService
{
    public async Task<Result<WaitlistJoinResult>> JoinAsync(
        long userId,
        JoinWaitlistCommand command,
        CancellationToken ct = default)
    {
        if (command.Seats < 1 || string.IsNullOrWhiteSpace(command.IdempotencyKey))
            return Failure<WaitlistJoinResult>(BusinessErrorKind.Validation);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(command.IdempotencyKey));

        try
        {
            return await unitOfWork.ExecuteWithConcurrencyRetryAsync(async attemptCt =>
            {
                await using var transaction = await unitOfWork.BeginTransactionAsync(attemptCt);
                var claim = await unitOfWork.Idempotency.ClaimAsync(userId, "join-waitlist", command.IdempotencyKey,
                    hash, command.ClassId, command.Seats, Now(), attemptCt);

                if (!claim.IsNew)
                {
                    // решение: тот же ключ с другими classId или seats отклоняем с idempotency-key-reused.
                    if (claim.Request.ClassId != command.ClassId || claim.Request.Seats != command.Seats)
                        return Failure<WaitlistJoinResult>(BusinessErrorKind.Conflict, "idempotency-key-reused");

                    var snapshot = await unitOfWork.Waitlist.GetWithPositionAsync(
                        claim.Request.WaitlistEntryId
                            ?? throw new InvalidOperationException("Incomplete waitlist idempotency record."),
                        userId,
                        attemptCt)
                        ?? throw new InvalidOperationException("The waitlist entry no longer exists.");

                    // решение: повтор для завершённого ожидания возвращает waitlist-inactive — текущей позиции уже нет.
                    if (snapshot.Entry.Status != "waiting")
                        return Failure<WaitlistJoinResult>(BusinessErrorKind.Conflict, "waitlist-inactive");

                    await transaction.CommitAsync(attemptCt);

                    return JoinSuccess(snapshot);
                }

                var scheduledClass = await unitOfWork.Class.GetIncludingCancelledAsync(command.ClassId, attemptCt);

                if (scheduledClass is null)
                    return Failure<WaitlistJoinResult>(BusinessErrorKind.NotFound);

                var now = Now();
                var validationError = await ValidateJoinAsync(userId, command, scheduledClass, now, attemptCt);

                if (validationError is not null)
                    return Result<WaitlistJoinResult>.Failure(validationError);

                var entry = await unitOfWork.Waitlist.CreateAsync(new WaitlistEntry
                    {
                        UserId = userId,
                        ClassId = command.ClassId,
                        Seats = command.Seats,
                        CreatedAt = now
                    }, attemptCt);
                unitOfWork.Class.MarkForConcurrencyCheck(scheduledClass);
                await unitOfWork.SaveChangesAsync(attemptCt);
                claim.Request.WaitlistEntryId = entry.Id;
                await unitOfWork.SaveChangesAsync(attemptCt);
                var result = await unitOfWork.Waitlist.GetWithPositionAsync(entry.Id, userId, attemptCt);

                await transaction.CommitAsync(attemptCt);

                return JoinSuccess(result!);
            }, ct);
        }
        catch (ConcurrencyRetriesExhaustedException)
        {
            return Failure<WaitlistJoinResult>(BusinessErrorKind.Unavailable);
        }
    }

    public async Task<Result<WaitlistCancelResult>> CancelAsync(long userId, long id, CancellationToken ct = default)
    {
        try
        {
            return await unitOfWork.ExecuteWithConcurrencyRetryAsync(async attemptCt =>
            {
                await using var transaction = await unitOfWork.BeginTransactionAsync(attemptCt);
                var entry = await unitOfWork.Waitlist.GetForUserAsync(id, userId, attemptCt);

                if (entry is null)
                    return Failure<WaitlistCancelResult>(BusinessErrorKind.NotFound);

                // решение: для promoted возвращаем waitlist-promoted — выданную бронь отменяют отдельным запросом.
                if (entry.Status == "promoted")
                    return Failure<WaitlistCancelResult>(BusinessErrorKind.Conflict, "waitlist-promoted");

                if (entry.Status is "cancelled" or "skipped")
                    return Result<WaitlistCancelResult>.Success(new WaitlistCancelResult(id, "cancelled"));

                var scheduledClass = await unitOfWork.Class.GetIncludingCancelledAsync(entry.ClassId, attemptCt);

                if (scheduledClass is null)
                    return Failure<WaitlistCancelResult>(BusinessErrorKind.NotFound);

                var now = Now();
                entry.Status = "cancelled";
                entry.FinishedAt = now;
                unitOfWork.Class.MarkForConcurrencyCheck(scheduledClass);
                await unitOfWork.SaveChangesAsync(attemptCt);

                // решение: после выхода снова продвигаем очередь — ушедшая заявка могла удерживать свободные места.
                await promotionService.PromoteAsync(scheduledClass, now, attemptCt);

                await transaction.CommitAsync(attemptCt);

                return Result<WaitlistCancelResult>.Success(new WaitlistCancelResult(id, "cancelled"));
            }, ct);
        }
        catch (ConcurrencyRetriesExhaustedException)
        {
            return Failure<WaitlistCancelResult>(BusinessErrorKind.Unavailable);
        }
    }

    private async Task<BusinessError?> ValidateJoinAsync(
        long userId,
        JoinWaitlistCommand command,
        Class scheduledClass,
        DateTime now,
        CancellationToken ct)
    {
        if (scheduledClass.IsCancelled)
            return new BusinessError(BusinessErrorKind.Conflict, "class-cancelled");

        if (now >= scheduledClass.StartsAt)
            return new BusinessError(BusinessErrorKind.Conflict, "class-started");

        // решение: заявку сверх вместимости отклоняем с capacity-exceeded, чтобы она не блокировала очередь.
        if (command.Seats > scheduledClass.Capacity)
            return new BusinessError(BusinessErrorKind.Conflict, "capacity-exceeded");

        if (await unitOfWork.Waitlist.IsWaitingAsync(userId, command.ClassId, ct))
            return new BusinessError(BusinessErrorKind.Conflict, "already-waiting");

        var booked = await unitOfWork.Enrollment.GetActiveSeatsAsync(command.ClassId, ct);

        if (scheduledClass.Capacity - booked >= command.Seats)
            return new BusinessError(BusinessErrorKind.Conflict, "seats-available");

        return null;
    }

    private DateTime Now()
    {
        var value = timeProvider.GetUtcNow().UtcDateTime;

        return new DateTime(value.Ticks - value.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc);
    }

    private static Result<T> Failure<T>(BusinessErrorKind kind, string? reason = null) =>
        Result<T>.Failure(new BusinessError(kind, reason));

    private static Result<WaitlistJoinResult> JoinSuccess(WaitlistSnapshot snapshot) =>
        Result<WaitlistJoinResult>.Success(new WaitlistJoinResult(snapshot.Entry.Id, snapshot.Entry.ClassId,
                snapshot.Entry.Seats, snapshot.Position, snapshot.Entry.CreatedAt));
}
