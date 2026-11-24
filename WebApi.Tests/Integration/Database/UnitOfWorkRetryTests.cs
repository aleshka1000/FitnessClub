using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace WebApi.Tests;

[TestClass]
public class UnitOfWorkRetryTests
{
    [TestMethod]
    public async Task RealPostgresDeadlock_RetriesEntireTransaction()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var first = await host.AddUserAsync();
        var second = await host.AddUserAsync();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        var totalAttempts = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        async Task ChangeAsync(long firstUser, long secondUser)
        {
            await using var scope = host.Factory.Services.CreateAsyncScope();
            var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var attempt = 0;
            await unit.ExecuteWithConcurrencyRetryAsync(async ct =>
            {
                attempt++;
                Interlocked.Increment(ref totalAttempts);
                await using var transaction = await unit.BeginTransactionAsync(ct);
                await unit.DbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE passes SET remaining_visits = remaining_visits - 1 WHERE user_id = {firstUser}", ct);

                if (attempt == 1)
                {
                    if (Interlocked.Increment(ref arrived) == 2)
                        ready.TrySetResult();

                    await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
                }

                await unit.DbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE passes SET remaining_visits = remaining_visits - 1 WHERE user_id = {secondUser}", ct);

                await transaction.CommitAsync(ct);

                return true;
            }, timeout.Token);
        }

        // Act
        await Task.WhenAll(ChangeAsync(first, second), ChangeAsync(second, first));

        // Assert
        Assert.IsTrue(totalAttempts >= 3);
        var visits = await host.DbAsync(db => db.Passes.Where(x => x.UserId == first || x.UserId == second)
                .Select(x => x.RemainingVisits).ToListAsync());
        CollectionAssert.AreEquivalent(new[] { 8, 8 }, visits);
    }

    [TestMethod]
    public async Task WaitingUniqueConstraint_IsRetried_AndUnrelatedConstraintIsNot()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var (scenario, _) = await WaitlistTestData.FullAsync(host);
        var user = await host.AddUserAsync();
        using var joined = await host.JoinAsync(user, scenario.ClassId);
        await ApiAssertions.CreatedIdAsync(joined);
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var attempts = 0;

        // Act
        var exists = await unit.ExecuteWithConcurrencyRetryAsync(async ct =>
        {
            attempts++;
            await using var transaction = await unit.BeginTransactionAsync(ct);

            if (attempts == 1)
            {
                await unit.Waitlist.CreateAsync(new WaitlistEntry
                    {
                        UserId = user,
                        ClassId = scenario.ClassId,
                        Seats = 1,
                        CreatedAt = host.Clock.Now.UtcDateTime
                    }, ct);
                await unit.SaveChangesAsync(ct);
            }

            var waiting = await unit.Waitlist.IsWaitingAsync(user, scenario.ClassId, ct);

            await transaction.CommitAsync(ct);

            return waiting;
        });

        // Assert
        Assert.IsTrue(exists);
        Assert.AreEqual(2, attempts);

        // Arrange
        var rejectedAttempts = 0;
        var failure = new PostgresException("unrelated constraint", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation,
            constraintName: "another_index");

        // Act & Assert
        await Assert.ThrowsExceptionAsync<PostgresException>(() => unit.ExecuteWithConcurrencyRetryAsync<int>(_ =>
        {
            rejectedAttempts++;

            return Task.FromException<int>(failure);
        }));

        // Assert
        Assert.AreEqual(1, rejectedAttempts);
    }
}
