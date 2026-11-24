using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using DataAccessLayer.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace WebApi.Tests;

[TestClass]
public class RepositoryTests
{
    [TestMethod]
    public async Task LoadGroupsByUtcDate_EvenWhenPostgresSessionUsesAnotherTimezone()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        var start = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(scenario.UserId * 3);
        await host.AddScheduledClassAsync(scenario.RoomId, start.AddHours(22), 10);
        await host.AddScheduledClassAsync(scenario.RoomId, start.AddDays(1).AddHours(1), 20);
        await host.DbAsync(async db =>
        {
            await db.Database.OpenConnectionAsync();

            try
            {
                await db.Database.ExecuteSqlRawAsync("SET TIME ZONE 'Asia/Yekaterinburg'");

                // Act
                var rows = await new ClassRepository(db.Classes, db.Enrollments)
                    .GetLoadAsync(start, start.AddDays(2), scenario.RoomId);

                // Assert
                Assert.AreEqual(2, rows.Count);
                CollectionAssert.AreEqual(new[]
                    {
                        DateOnly.FromDateTime(start),
                        DateOnly.FromDateTime(start.AddDays(1))
                    },
                    rows.Select(x => x.Date).ToArray());
                CollectionAssert.AreEqual(new[] { 10, 20 }, rows.Select(x => x.TotalCapacity).ToArray());
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }

            return true;
        });
    }

    [TestMethod]
    public async Task EnrollmentQueries_RespectOwnerStatus_AndCancelledClassVisibility()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync(cancelled: true);
        var id = await host.DbAsync(async db =>
        {
            var active = new Enrollment
            {
                ClassId = scenario.ClassId,
                UserId = scenario.UserId,
                Seats = 2,
                Status = "active",
                CreatedAt = host.Clock.Now.UtcDateTime
            };
            db.Enrollments.AddRange(active, new Enrollment
                {
                    ClassId = scenario.ClassId,
                    UserId = scenario.UserId,
                    Seats = 3,
                    Status = "cancelled",
                    CreatedAt = host.Clock.Now.UtcDateTime
                });
            await db.SaveChangesAsync();

            return active.Id;
        });
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        // Act & Assert
        Assert.IsNull(await unitOfWork.Class.GetByIdAsync(scenario.ClassId));
        Assert.IsTrue((await unitOfWork.Class.GetIncludingCancelledAsync(scenario.ClassId))!.IsCancelled);
        Assert.AreEqual(2L, await unitOfWork.Enrollment.GetActiveSeatsAsync(scenario.ClassId));
        Assert.AreEqual(0L, await unitOfWork.Enrollment.GetActiveSeatsAsync(long.MaxValue));
        Assert.AreEqual(id, (await unitOfWork.Enrollment.GetForUserAsync(id, scenario.UserId))!.Id);
        Assert.IsNull(await unitOfWork.Enrollment.GetForUserAsync(id, long.MaxValue));
    }

    [TestMethod]
    public async Task ExplicitRollback_DiscardsChangesAfterSave()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();

        // Act
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using var transaction = await unitOfWork.BeginTransactionAsync();
            (await unitOfWork.Pass.GetByUserAsync(scenario.UserId))!.RemainingVisits = 0;
            await unitOfWork.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        // Assert
        await host.AssertUnchangedAsync(scenario);
    }
}
