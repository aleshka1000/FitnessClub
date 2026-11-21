using System.Data.Common;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace WebApi.Tests;

internal sealed class ActiveSeatsBarrier : DbCommandInterceptor
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrived;

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
        CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("sum(", StringComparison.OrdinalIgnoreCase)
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

internal sealed class PauseActiveSeats : DbCommandInterceptor
{
    private int _queries;

    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
        CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
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

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
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
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<IdempotencyRequest>()
            .Any(x => x.State == EntityState.Modified && x.Entity.EnrollmentId is not null))
        {
            throw new InvalidOperationException("Forced failure after saving enrollment.");
        }

        return ValueTask.FromResult(result);
    }
}
