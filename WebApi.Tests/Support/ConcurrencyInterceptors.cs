using System.Data.Common;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace WebApi.Tests;

internal sealed class ActiveSeatsBarrier : DbCommandInterceptor
{
    public bool Enabled { get; set; } = true;

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrived;

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        if (Enabled && command.CommandText.Contains("sum(", StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains("enrollments", StringComparison.OrdinalIgnoreCase))
        {
            var arrived = Interlocked.Increment(ref _arrived);

            if (arrived == 2)
            {
                _ready.TrySetResult();
            }

            if (arrived <= 2)
            {
                await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
        }

        return result;
    }
}

internal sealed class ArmedSaveBarrier : SaveChangesInterceptor
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrived;

    public long ClassId { get; set; }
    public long AddedEnrollmentUserId { get; set; }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var context = eventData.Context!;
        var matches = AddedEnrollmentUserId != 0
            ? context.ChangeTracker.Entries<Enrollment>().Any(
                x => x.State == EntityState.Added && x.Entity.UserId == AddedEnrollmentUserId)
            : ClassId != 0 && context.ChangeTracker.Entries<Class>().Any(
                x => x.State == EntityState.Modified && x.Entity.Id == ClassId);

        if (matches)
        {
            var arrived = Interlocked.Increment(ref _arrived);

            if (arrived == 2)
                _ready.TrySetResult();

            if (arrived <= 2)
                await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }

        return result;
    }
}

internal sealed class PauseActiveSeats : DbCommandInterceptor
{
    private int _queries;

    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("sum(", StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains("enrollments", StringComparison.OrdinalIgnoreCase)
            && Interlocked.Increment(ref _queries) == 1)
        {
            Reached.TrySetResult();
            await Continue.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }

        return result;
    }
}

internal sealed class ForcedConcurrencyFailures(int failures) : SaveChangesInterceptor
{
    public long UserId { get; set; }

    public int Attempts { get; private set; }

    public int RemainingFailures { get; set; } = failures;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (UserId != 0 && eventData.Context!.ChangeTracker.Entries<Enrollment>()
                .Any(x => x.State == EntityState.Added && x.Entity.UserId == UserId))
        {
            Attempts++;

            if (RemainingFailures-- > 0)
            {
                throw new DbUpdateConcurrencyException("Forced concurrent update.");
            }
        }

        return ValueTask.FromResult(result);
    }
}

internal sealed class FailIdempotencyCompletion : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<IdempotencyRequest>()
                .Any(x => x.State == EntityState.Modified && x.Entity.EnrollmentId is not null))
        {
            throw new InvalidOperationException("Forced failure after saving enrollment.");
        }

        return ValueTask.FromResult(result);
    }
}

internal sealed class PauseWaitlistSave : SaveChangesInterceptor
{
    private int _paused;
    private readonly TaskCompletionSource _continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public long UserId { get; set; }
    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Release() => _continue.TrySetResult();

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (UserId != 0 && eventData.Context!.ChangeTracker.Entries<WaitlistEntry>()
                .Any(x => x.State == EntityState.Added && x.Entity.UserId == UserId)
            && Interlocked.Exchange(ref _paused, 1) == 0)
        {
            Reached.TrySetResult();
            await _continue.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }

        return result;
    }
}

internal sealed class ForcedWaitlistConcurrencyFailures(EntityState state) : SaveChangesInterceptor
{
    private int _remaining = 5;
    public long UserId { get; set; }
    public int Attempts { get; private set; }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (UserId != 0 && eventData.Context!.ChangeTracker.Entries<WaitlistEntry>()
                .Any(x => x.State == state && x.Entity.UserId == UserId))
        {
            Attempts++;

            if (_remaining-- > 0)
                throw new DbUpdateConcurrencyException("Forced waitlist concurrency conflict.");
        }

        return ValueTask.FromResult(result);
    }
}
