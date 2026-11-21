using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace WebApi.Tests;

[TestClass]
public class RepositoryTests
{
    [TestMethod]
    public async Task EnrollmentQueries_RespectOwnerStatus_AndCancelledClassVisibility()
    {
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync(cancelled: true);
        var id = await host.DbAsync(async db =>
        {
            var active = new Enrollment
            {
                ClassId = scenario.ClassId, UserId = scenario.UserId, Seats = 2,
                Status = "active", CreatedAt = host.Clock.Now.UtcDateTime
            };
            db.Enrollments.AddRange(active, new Enrollment
            {
                ClassId = scenario.ClassId, UserId = scenario.UserId, Seats = 3,
                Status = "cancelled", CreatedAt = host.Clock.Now.UtcDateTime
            });
            await db.SaveChangesAsync();
            return active.Id;
        });
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
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
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using var transaction = await unitOfWork.BeginTransactionAsync();
            (await unitOfWork.Pass.GetByUserAsync(scenario.UserId))!.RemainingVisits = 0;
            await unitOfWork.SaveChangesAsync();
            await transaction.RollbackAsync();
        }
        await host.AssertUnchangedAsync(scenario);
    }
}
