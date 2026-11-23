using BusinessLogicLayer.Interfaces;
using BusinessLogicLayer.Models;
using BusinessLogicLayer.Services;
using Common.Database.UnitOfWork;
using DataAccessLayer.Entities;
using DataAccessLayer.Models;
using DataAccessLayer.Repositories;
using DataAccessLayer.UnitOfWork;
using Moq;

namespace WebApi.Tests;

[TestClass]
public class SeatChangeServiceTests
{
    [TestMethod]
    [DataRow(1, 11)]
    [DataRow(3, 9)]
    public async Task Change_UpdatesOnlyDifference_AndCommits(int seats, int balance)
    {
        var scenario = new Scenario();
        var result = await scenario.Service.ChangeSeatsAsync(1, 2, seats);
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(seats, scenario.Enrollment.Seats);
        Assert.AreEqual(balance, scenario.Pass.RemainingVisits);
        Assert.AreEqual(balance, result.Value.Pass.RemainingVisits);
        scenario.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        scenario.Classes.Verify(x => x.MarkForConcurrencyCheck(scenario.Class), Times.Once);
        scenario.Transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        scenario.Promotion.Verify(x => x.PromoteAsync(scenario.Class, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            seats < 2 ? Times.Once() : Times.Never());
    }

    [TestMethod]
    public async Task SameSeats_OnExpiredPass_DoesNotWriteOrPromote()
    {
        var scenario = new Scenario();
        scenario.Pass.ValidUntil = new DateOnly(2020, 1, 1);
        var result = await scenario.Service.ChangeSeatsAsync(1, 2, 2);
        Assert.IsTrue(result.IsSuccess);
        scenario.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        scenario.Classes.Verify(x => x.MarkForConcurrencyCheck(It.IsAny<Class>()), Times.Never);
        scenario.Promotion.VerifyNoOtherCalls();
    }

    [TestMethod]
    [DataRow("enrollment-cancelled")]
    [DataRow("class-cancelled")]
    [DataRow("class-started")]
    [DataRow("pass-expired")]
    [DataRow("capacity-exceeded")]
    [DataRow("pass-exhausted")]
    public async Task Refusal_DoesNotSaveOrCommit(string reason)
    {
        var scenario = new Scenario();
        switch (reason)
        {
            case "enrollment-cancelled": scenario.Enrollment.Status = "cancelled"; break;
            case "class-cancelled": scenario.Class.IsCancelled = true; break;
            case "class-started": scenario.Class.StartsAt = scenario.Clock.Now.UtcDateTime; break;
            case "pass-expired": scenario.Pass.ValidUntil = new DateOnly(2020, 1, 1); break;
            case "capacity-exceeded": scenario.Class.Capacity = 2; break;
            case "pass-exhausted": scenario.Pass.RemainingVisits = 0; break;
        }
        var result = await scenario.Service.ChangeSeatsAsync(1, 2, 3);
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(reason, result.Error.Reason);
        Assert.AreEqual(2, scenario.Enrollment.Seats);
        scenario.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        scenario.Transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Decrease_OnExpiredPass_PromotesBeforeReadingFinalCard()
    {
        var scenario = new Scenario();
        scenario.Pass.ValidUntil = new DateOnly(2020, 1, 1);
        scenario.Promotion.Setup(x => x.PromoteAsync(scenario.Class, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                Assert.AreEqual(1, scenario.Enrollment.Seats);
                Assert.AreEqual(11, scenario.Pass.RemainingVisits);
                scenario.Pass.RemainingVisits = 9;
            }).Returns(Task.CompletedTask);
        var result = await scenario.Service.ChangeSeatsAsync(1, 2, 1);
        Assert.AreEqual(9, result.Value.Pass.RemainingVisits);
    }

    private sealed class Scenario
    {
        public FixedTimeProvider Clock { get; } = new();
        public Enrollment Enrollment { get; } = new() { Id = 2, UserId = 1, ClassId = 3, Seats = 2, Status = "active" };
        public Class Class { get; } = new() { Id = 3, Capacity = 5, Title = "Test" };
        public Pass Pass { get; } = new() { UserId = 1, RemainingVisits = 10, ValidUntil = new DateOnly(2031, 1, 1) };
        public Mock<IUnitOfWork> UnitOfWork { get; } = new(MockBehavior.Strict);
        public Mock<IClassRepository> Classes { get; } = new();
        public Mock<IUnitOfWorkTransaction> Transaction { get; } = new();
        public Mock<IWaitlistPromotionService> Promotion { get; } = new();
        public EnrollmentService Service { get; }

        public Scenario()
        {
            Class.StartsAt = Clock.Now.UtcDateTime.AddHours(1);
            var enrollments = new Mock<IEnrollmentRepository>();
            enrollments.Setup(x => x.GetForUserAsync(2, 1, It.IsAny<CancellationToken>())).ReturnsAsync(Enrollment);
            enrollments.Setup(x => x.GetActiveSeatsAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(2);
            enrollments.Setup(x => x.GetDetailsAsync(2, 1, It.IsAny<CancellationToken>())).ReturnsAsync(() =>
                new EnrollmentDetailsData(2, Enrollment.Status, Enrollment.Seats, Clock.Now.UtcDateTime,
                    3, "Test", Class.StartsAt, 60, Class.IsCancelled, 4, "Room", Pass.RemainingVisits, Pass.ValidUntil));
            var passes = new Mock<IPassRepository>();
            passes.Setup(x => x.GetByUserAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(Pass);
            Classes.Setup(x => x.GetIncludingCancelledAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(Class);
            UnitOfWork.SetupGet(x => x.Enrollment).Returns(enrollments.Object);
            UnitOfWork.SetupGet(x => x.Class).Returns(Classes.Object);
            UnitOfWork.SetupGet(x => x.Pass).Returns(passes.Object);
            UnitOfWork.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Transaction.Object);
            UnitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            UnitOfWork.Setup(x => x.ExecuteWithConcurrencyRetryAsync(
                    It.IsAny<Func<CancellationToken, Task<Result<EnrollmentDetailsResult>>>>(), It.IsAny<CancellationToken>()))
                .Returns((Func<CancellationToken, Task<Result<EnrollmentDetailsResult>>> operation, CancellationToken ct) => operation(ct));
            Service = new EnrollmentService(UnitOfWork.Object, Clock, Promotion.Object);
        }
    }
}
