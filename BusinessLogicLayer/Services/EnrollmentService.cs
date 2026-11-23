using System.Security.Cryptography;
using System.Text;
using BusinessLogicLayer.Interfaces;
using BusinessLogicLayer.Models;
using Common.Database.UnitOfWork;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;

namespace BusinessLogicLayer.Services;

public class EnrollmentService(IUnitOfWork unitOfWork, TimeProvider timeProvider,
    IWaitlistPromotionService promotionService) : IEnrollmentService
{
    private const string Operation = "create-enrollment";

    public async Task<Result<EnrollmentDetailsResult>> GetAsync(long userId, long id, CancellationToken ct = default)
    {
        var data = await unitOfWork.Enrollment.GetDetailsAsync(id, userId, ct);
        return data is null
            ? Result<EnrollmentDetailsResult>.Failure(new BusinessError(BusinessErrorKind.NotFound))
            : Result<EnrollmentDetailsResult>.Success(EnrollmentDetailsResult.From(data));
    }

    public async Task<Result<EnrollmentDetailsResult>> ChangeSeatsAsync(long userId, long id, int seats,
        CancellationToken ct = default)
    {
        if (seats < 1)
            return Result<EnrollmentDetailsResult>.Failure(new BusinessError(BusinessErrorKind.Validation));

        try
        {
            return await unitOfWork.ExecuteWithConcurrencyRetryAsync(async attemptCt =>
            {
                await using var transaction = await unitOfWork.BeginTransactionAsync(attemptCt);
                var enrollment = await unitOfWork.Enrollment.GetForUserAsync(id, userId, attemptCt);
                if (enrollment is null)
                    return Result<EnrollmentDetailsResult>.Failure(new BusinessError(BusinessErrorKind.NotFound));

                // решение: сначала проверяем отмену брони, затем отмену и начало занятия; абонемент проверяем только при увеличении.
                if (enrollment.Status == "cancelled") return DetailsConflict("enrollment-cancelled");
                var scheduledClass = await unitOfWork.Class.GetIncludingCancelledAsync(enrollment.ClassId, attemptCt);
                if (scheduledClass is null)
                    return Result<EnrollmentDetailsResult>.Failure(new BusinessError(BusinessErrorKind.NotFound));
                if (scheduledClass.IsCancelled) return DetailsConflict("class-cancelled");
                var now = timeProvider.GetUtcNow().UtcDateTime;
                if (now >= scheduledClass.StartsAt) return DetailsConflict("class-started");

                var delta = seats - enrollment.Seats;
                if (delta != 0)
                {
                    var pass = await unitOfWork.Pass.GetByUserAsync(userId, attemptCt)
                        ?? throw new InvalidOperationException("The user has no pass.");
                    if (delta > 0)
                    {
                        if (pass.ValidUntil < DateOnly.FromDateTime(now)) return DetailsConflict("pass-expired");
                        var booked = await unitOfWork.Enrollment.GetActiveSeatsAsync(scheduledClass.Id, attemptCt);
                        if (booked + delta > scheduledClass.Capacity) return DetailsConflict("capacity-exceeded");
                        if (pass.RemainingVisits < delta) return DetailsConflict("pass-exhausted");
                    }

                    enrollment.Seats = seats;
                    pass.RemainingVisits -= delta;
                    unitOfWork.Class.MarkForConcurrencyCheck(scheduledClass);
                    await unitOfWork.SaveChangesAsync(attemptCt);
                    // решение: уменьшение брони продвигает очередь в той же транзакции, как отмена.
                    if (delta < 0)
                        await promotionService.PromoteAsync(scheduledClass, TruncateToMicroseconds(now), attemptCt);
                }

                var result = await GetAsync(userId, id, attemptCt);
                await transaction.CommitAsync(attemptCt);
                return result;
            }, ct);
        }
        catch (ConcurrencyRetriesExhaustedException)
        {
            return Result<EnrollmentDetailsResult>.Failure(new BusinessError(BusinessErrorKind.Unavailable));
        }
    }

    private static Result<EnrollmentDetailsResult> DetailsConflict(string reason) =>
        Result<EnrollmentDetailsResult>.Failure(new BusinessError(BusinessErrorKind.Conflict, reason));

    public async Task<Result<EnrollmentCreationResult>> CreateAsync(long userId, CreateEnrollmentCommand command,
        CancellationToken ct = default)
    {
        if (command.Seats < 1 || string.IsNullOrWhiteSpace(command.IdempotencyKey))
        {
            return Result<EnrollmentCreationResult>.Failure(new BusinessError(BusinessErrorKind.Validation));
        }

        var keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(command.IdempotencyKey));

        try
        {
            return await unitOfWork.ExecuteWithConcurrencyRetryAsync(
                attemptCt => CreateAttemptAsync(userId, command, keyHash, attemptCt), ct);
        }
        catch (ConcurrencyRetriesExhaustedException)
        {
            return Result<EnrollmentCreationResult>.Failure(new BusinessError(BusinessErrorKind.Unavailable));
        }
    }

    private async Task<Result<EnrollmentCreationResult>> CreateAttemptAsync(long userId,
        CreateEnrollmentCommand command, byte[] keyHash, CancellationToken ct)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        var claim = await unitOfWork.Idempotency.ClaimAsync(userId, Operation, command.IdempotencyKey, keyHash,
            command.ClassId, command.Seats, TruncateToMicroseconds(timeProvider.GetUtcNow().UtcDateTime), ct);

        if (!claim.IsNew)
        {
            if (claim.Request.ClassId != command.ClassId || claim.Request.Seats != command.Seats)
            {
                return Conflict("idempotency-key-reused");
            }

            // решение: повтор создания возвращает текущее состояние связанной брони
            var existing = await unitOfWork.Enrollment.GetForUserAsync(
                claim.Request.EnrollmentId ?? throw new InvalidOperationException("Incomplete idempotency record."),
                userId, ct);

            if (existing is null)
            {
                throw new InvalidOperationException("The idempotent enrollment no longer exists.");
            }

            await transaction.CommitAsync(ct);
            return Success(existing);
        }

        var scheduledClass = await unitOfWork.Class.GetIncludingCancelledAsync(command.ClassId, ct);

        if (scheduledClass is null)
        {
            return Result<EnrollmentCreationResult>.Failure(new BusinessError(BusinessErrorKind.NotFound));
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // решение: при нескольких отказах сначала проверяем занятие, затем срок, вместимость и остаток посещений.
        if (scheduledClass.IsCancelled)
        {
            return Conflict("class-cancelled");
        }

        if (now >= scheduledClass.StartsAt)
        {
            return Conflict("class-started");
        }

        var pass = await unitOfWork.Pass.GetByUserAsync(userId, ct)
            ?? throw new InvalidOperationException("The user has no pass.");

        if (pass.ValidUntil < DateOnly.FromDateTime(now))
        {
            return Conflict("pass-expired");
        }

        var activeSeats = await unitOfWork.Enrollment.GetActiveSeatsAsync(scheduledClass.Id, ct);

        if (activeSeats + command.Seats > scheduledClass.Capacity)
        {
            return Conflict("capacity-exceeded");
        }

        if (pass.RemainingVisits < command.Seats)
        {
            return Conflict("pass-exhausted");
        }

        unitOfWork.Class.MarkForConcurrencyCheck(scheduledClass);
        pass.RemainingVisits -= command.Seats;

        var enrollment = await unitOfWork.Enrollment.CreateAsync(new Enrollment
        {
            UserId = userId,
            ClassId = scheduledClass.Id,
            Seats = command.Seats,
            Status = "active",
            CreatedAt = TruncateToMicroseconds(now)
        }, ct);

        await unitOfWork.SaveChangesAsync(ct);
        claim.Request.EnrollmentId = enrollment.Id;
        await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Success(enrollment);
    }

    private static Result<EnrollmentCreationResult> Conflict(string reason) =>
        Result<EnrollmentCreationResult>.Failure(new BusinessError(BusinessErrorKind.Conflict, reason));

    public async Task<Result<EnrollmentCancellationResult>> CancelAsync(long userId, long id,
        CancellationToken ct = default)
    {
        try
        {
            return await unitOfWork.ExecuteWithConcurrencyRetryAsync(async attemptCt =>
            {
                await using var transaction = await unitOfWork.BeginTransactionAsync(attemptCt);
                var enrollment = await unitOfWork.Enrollment.GetForUserAsync(id, userId, attemptCt);
                if (enrollment is null)
                    return Result<EnrollmentCancellationResult>.Failure(new BusinessError(BusinessErrorKind.NotFound));

                // решение: повторная отмена ничего не начисляет; исторический размер возврата не храним.
                if (enrollment.Status == "cancelled")
                    return Result<EnrollmentCancellationResult>.Success(new EnrollmentCancellationResult(id, "cancelled", 0));

                var scheduledClass = await unitOfWork.Class.GetIncludingCancelledAsync(enrollment.ClassId, attemptCt);
                if (scheduledClass is null)
                    return Result<EnrollmentCancellationResult>.Failure(new BusinessError(BusinessErrorKind.NotFound));
                var now = timeProvider.GetUtcNow().UtcDateTime;
                if (!scheduledClass.IsCancelled && now >= scheduledClass.StartsAt)
                    return Result<EnrollmentCancellationResult>.Failure(new BusinessError(BusinessErrorKind.Conflict, "class-started"));

                var refunded = scheduledClass.IsCancelled || scheduledClass.StartsAt - now >= TimeSpan.FromHours(2)
                    ? enrollment.Seats : 0;
                if (refunded > 0)
                {
                    var pass = await unitOfWork.Pass.GetByUserAsync(userId, attemptCt)
                        ?? throw new InvalidOperationException("The user has no pass.");
                    pass.RemainingVisits += refunded;
                }

                enrollment.Status = "cancelled";
                enrollment.CancelledAt = TruncateToMicroseconds(now);
                unitOfWork.Class.MarkForConcurrencyCheck(scheduledClass);
                await unitOfWork.SaveChangesAsync(attemptCt);
                await promotionService.PromoteAsync(scheduledClass, enrollment.CancelledAt.Value, attemptCt);
                await transaction.CommitAsync(attemptCt);
                return Result<EnrollmentCancellationResult>.Success(new EnrollmentCancellationResult(id, "cancelled", refunded));
            }, ct);
        }
        catch (ConcurrencyRetriesExhaustedException)
        {
            return Result<EnrollmentCancellationResult>.Failure(new BusinessError(BusinessErrorKind.Unavailable));
        }
    }

    private static Result<EnrollmentCreationResult> Success(Enrollment enrollment) =>
        Result<EnrollmentCreationResult>.Success(new EnrollmentCreationResult(enrollment.Id, enrollment.ClassId,
            enrollment.Seats, enrollment.Status, enrollment.CreatedAt));

    private static DateTime TruncateToMicroseconds(DateTime value)
    {
        // решение: createdAt в памяти и PostgreSQL имеет одинаковую микросекундную точность.
        return new DateTime(value.Ticks - value.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc);
    }
}
