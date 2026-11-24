using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BusinessLogicLayer.Interfaces;
using BusinessLogicLayer.Models;
using Common.Caching;
using Common.Database.UnitOfWork;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace BusinessLogicLayer.Services;

public class EnrollmentService(
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    IWaitlistPromotionService promotionService,
    ICacheService cache,
    EnrollmentCardCacheOptions cacheOptions,
    ILogger<EnrollmentService> logger) : IEnrollmentService
{
    private const string Operation = "create-enrollment";
    private static readonly TimeSpan CardCacheTtl = TimeSpan.FromMinutes(5);

    public async Task<Result<EnrollmentDetailsResult>> GetAsync(long userId, long id, CancellationToken ct = default)
    {
        var versions = await unitOfWork.Enrollment.GetCardVersionsAsync(id, userId, ct);

        if (versions is null)
            return Result<EnrollmentDetailsResult>.Failure(new BusinessError(BusinessErrorKind.NotFound));

        var key = GetCardCacheKey(userId, id, versions.EnrollmentVersion, versions.PassVersion);
        var cached = await GetCachedCardAsync(key, ct);

        if (cached is not null)
            return Result<EnrollmentDetailsResult>.Success(cached);

        var data = await unitOfWork.Enrollment.GetDetailsAsync(id, userId, ct);

        if (data is null)
            return Result<EnrollmentDetailsResult>.Failure(new BusinessError(BusinessErrorKind.NotFound));

        var result = EnrollmentDetailsResult.From(data);

        key = GetCardCacheKey(userId, id, data.EnrollmentVersion, data.PassVersion);
        await CacheCardAsync(key, result, ct);

        return Result<EnrollmentDetailsResult>.Success(result);
    }

    private string GetCardCacheKey(long userId, long id, uint enrollmentVersion, uint passVersion) =>
        $"{cacheOptions.KeyPrefix}:card:{userId}:{id}:{enrollmentVersion}:{passVersion}";

    private async Task<EnrollmentDetailsResult?> GetCachedCardAsync(string key, CancellationToken ct)
    {
        try
        {
            return await cache.GetAsync<EnrollmentDetailsResult>(key, ct);
        }
        catch (Exception exception) when (exception is RedisException or RedisTimeoutException or JsonException)
        {
            logger.LogWarning(exception, "Could not read enrollment card cache {Key}.", key);

            return null;
        }
    }

    private async Task CacheCardAsync(string key, EnrollmentDetailsResult card, CancellationToken ct)
    {
        try
        {
            await cache.SetAsync(key, card, CardCacheTtl, ct);
        }
        catch (Exception exception) when (exception is RedisException or RedisTimeoutException)
        {
            logger.LogWarning(exception, "Could not write enrollment card cache {Key}.", key);
        }
    }

    public async Task<Result<EnrollmentDetailsResult>> ChangeSeatsAsync(
        long userId,
        long id,
        int seats,
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

                if (enrollment.Status == "cancelled")
                    return DetailsConflict("enrollment-cancelled");

                var scheduledClass = await unitOfWork.Class.GetIncludingCancelledAsync(enrollment.ClassId, attemptCt);

                if (scheduledClass is null)
                    return Result<EnrollmentDetailsResult>.Failure(new BusinessError(BusinessErrorKind.NotFound));

                var now = timeProvider.GetUtcNow().UtcDateTime;
                var classError = ValidateClassState(scheduledClass, now);

                if (classError is not null)
                    return Result<EnrollmentDetailsResult>.Failure(classError);

                var delta = seats - enrollment.Seats;

                if (delta != 0)
                {
                    var pass = await unitOfWork.Pass.GetByUserAsync(userId, attemptCt)
                        ?? throw new InvalidOperationException("The user has no pass.");

                    if (delta > 0)
                    {
                        var allocationError = await ValidateSeatAllocationAsync(
                            scheduledClass,
                            pass,
                            delta,
                            now,
                            attemptCt);

                        if (allocationError is not null)
                            return Result<EnrollmentDetailsResult>.Failure(allocationError);
                    }

                    enrollment.Seats = seats;
                    pass.RemainingVisits -= delta;
                    unitOfWork.Class.MarkForConcurrencyCheck(scheduledClass);
                    await unitOfWork.SaveChangesAsync(attemptCt);

                    // решение: уменьшение брони тоже запускает очередь, чтобы свободные места сразу достались ожидающим.
                    if (delta < 0)
                        await promotionService.PromoteAsync(scheduledClass, TruncateToMicroseconds(now), attemptCt);
                }

                // решение: после продвижения очереди читаем карточку из БД — оно могло изменить абонемент клиента.
                var data = await unitOfWork.Enrollment.GetDetailsAsync(id, userId, attemptCt);
                var result = data is null
                    ? Result<EnrollmentDetailsResult>.Failure(new BusinessError(BusinessErrorKind.NotFound))
                    : Result<EnrollmentDetailsResult>.Success(EnrollmentDetailsResult.From(data));

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

    public async Task<Result<EnrollmentCreationResult>> CreateAsync(
        long userId,
        CreateEnrollmentCommand command,
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

    private async Task<Result<EnrollmentCreationResult>> CreateAttemptAsync(
        long userId,
        CreateEnrollmentCommand command,
        byte[] keyHash,
        CancellationToken ct)
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

        var classError = ValidateClassState(scheduledClass, now);

        if (classError is not null)
            return Result<EnrollmentCreationResult>.Failure(classError);

        var pass = await unitOfWork.Pass.GetByUserAsync(userId, ct)
            ?? throw new InvalidOperationException("The user has no pass.");

        var allocationError = await ValidateSeatAllocationAsync(scheduledClass, pass, command.Seats, now, ct);

        if (allocationError is not null)
            return Result<EnrollmentCreationResult>.Failure(allocationError);

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

    public async Task<Result<EnrollmentCancellationResult>> CancelAsync(
        long userId,
        long id,
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

                if (enrollment.Status == "cancelled")
                    return Result<EnrollmentCancellationResult>.Success(new EnrollmentCancellationResult(
                            id,
                            "cancelled",
                            0));

                var scheduledClass = await unitOfWork.Class.GetIncludingCancelledAsync(enrollment.ClassId, attemptCt);

                if (scheduledClass is null)
                    return Result<EnrollmentCancellationResult>.Failure(new BusinessError(BusinessErrorKind.NotFound));

                var now = timeProvider.GetUtcNow().UtcDateTime;

                if (!scheduledClass.IsCancelled && now >= scheduledClass.StartsAt)
                    return Result<EnrollmentCancellationResult>.Failure(new BusinessError(
                            BusinessErrorKind.Conflict,
                            "class-started"));

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

                return Result<EnrollmentCancellationResult>.Success(new EnrollmentCancellationResult(
                        id,
                        "cancelled",
                        refunded));
            }, ct);
        }
        catch (ConcurrencyRetriesExhaustedException)
        {
            return Result<EnrollmentCancellationResult>.Failure(new BusinessError(BusinessErrorKind.Unavailable));
        }
    }

    private static BusinessError? ValidateClassState(Class scheduledClass, DateTime now)
    {
        if (scheduledClass.IsCancelled)
            return new BusinessError(BusinessErrorKind.Conflict, "class-cancelled");

        if (now >= scheduledClass.StartsAt)
            return new BusinessError(BusinessErrorKind.Conflict, "class-started");

        return null;
    }

    private async Task<BusinessError?> ValidateSeatAllocationAsync(
        Class scheduledClass,
        Pass pass,
        int additionalSeats,
        DateTime now,
        CancellationToken ct)
    {
        if (pass.ValidUntil < DateOnly.FromDateTime(now))
            return new BusinessError(BusinessErrorKind.Conflict, "pass-expired");

        var activeSeats = await unitOfWork.Enrollment.GetActiveSeatsAsync(scheduledClass.Id, ct);

        if (activeSeats + additionalSeats > scheduledClass.Capacity)
            return new BusinessError(BusinessErrorKind.Conflict, "capacity-exceeded");

        if (pass.RemainingVisits < additionalSeats)
            return new BusinessError(BusinessErrorKind.Conflict, "pass-exhausted");

        return null;
    }

    private static Result<EnrollmentCreationResult> Success(Enrollment enrollment) =>
        Result<EnrollmentCreationResult>.Success(new EnrollmentCreationResult(enrollment.Id, enrollment.ClassId,
                enrollment.Seats, enrollment.Status, enrollment.CreatedAt));

    private static DateTime TruncateToMicroseconds(DateTime value)
    {
        return new DateTime(value.Ticks - value.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc);
    }
}
