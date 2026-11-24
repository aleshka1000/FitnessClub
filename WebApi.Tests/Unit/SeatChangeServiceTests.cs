using BusinessLogicLayer.Interfaces;
using BusinessLogicLayer.Models;
using BusinessLogicLayer.Services;
using Common.Caching;
using Common.Database.UnitOfWork;
using DataAccessLayer.Entities;
using DataAccessLayer.Models;
using DataAccessLayer.Repositories;
using DataAccessLayer.UnitOfWork;
using Moq;
using Microsoft.Extensions.Logging.Abstractions;

namespace WebApi.Tests;

[TestClass]
public class SeatChangeServiceTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task Change_ReadsFinalCardWithoutUsingCache(int seats)
    {
        // Arrange
        var scenario = new Scenario();

        // Act
        var result = await scenario.Service.ChangeSeatsAsync(1, 2, seats);

        // Assert
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(seats, result.Value.Seats);
        scenario.Cache.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task SameSeats_OnExpiredPass_DoesNotWriteOrPromote()
    {
        // Arrange
        var scenario = new Scenario();
        scenario.Pass.ValidUntil = new DateOnly(2020, 1, 1);

        // Act
        var result = await scenario.Service.ChangeSeatsAsync(1, 2, 2);

        // Assert
        Assert.IsTrue(result.IsSuccess);
        scenario.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        scenario.Classes.Verify(x => x.MarkForConcurrencyCheck(It.IsAny<Class>()), Times.Never);
        scenario.Promotion.VerifyNoOtherCalls();
    }

    private sealed class Scenario
    {
        public ManualTimeProvider Clock { get; } = new();
        public Enrollment Enrollment { get; } = new() { Id = 2, UserId = 1, ClassId = 3, Seats = 2, Status = "active" };
        public Class Class { get; } = new() { Id = 3, Capacity = 5, Title = "Test" };
        public Pass Pass { get; } = new() { UserId = 1, RemainingVisits = 10, ValidUntil = new DateOnly(2031, 1, 1) };
        public Mock<IUnitOfWork> UnitOfWork { get; } = new(MockBehavior.Strict);
        public Mock<IClassRepository> Classes { get; } = new();
        public Mock<IUnitOfWorkTransaction> Transaction { get; } = new();
        public Mock<IWaitlistPromotionService> Promotion { get; } = new();
        public Mock<ICacheService> Cache { get; } = new(MockBehavior.Strict);
        public EnrollmentService Service { get; }

        public Scenario()
        {
            Class.StartsAt = Clock.Now.UtcDateTime.AddHours(1);
            var enrollments = new Mock<IEnrollmentRepository>();
            enrollments.Setup(x => x.GetForUserAsync(2, 1, It.IsAny<CancellationToken>())).ReturnsAsync(Enrollment);
            enrollments.Setup(x => x.GetActiveSeatsAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(2);
            enrollments.Setup(x => x.GetDetailsAsync(2, 1, It.IsAny<CancellationToken>())).ReturnsAsync(() =>
                new EnrollmentDetailsData(2, Enrollment.Status, Enrollment.Seats, Clock.Now.UtcDateTime,
                    3, "Test", Class.StartsAt, 60, Class.IsCancelled, 4, "Room", Pass.RemainingVisits, Pass.ValidUntil,
                    Enrollment.Version, Pass.Version));
            var passes = new Mock<IPassRepository>();
            passes.Setup(x => x.GetByUserAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(Pass);
            Classes.Setup(x => x.GetIncludingCancelledAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(Class);
            UnitOfWork.SetupGet(x => x.Enrollment).Returns(enrollments.Object);
            UnitOfWork.SetupGet(x => x.Class).Returns(Classes.Object);
            UnitOfWork.SetupGet(x => x.Pass).Returns(passes.Object);
            UnitOfWork.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Transaction.Object);
            UnitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            UnitOfWork.Setup(x => x.ExecuteWithConcurrencyRetryAsync(
                    It.IsAny<Func<CancellationToken, Task<Result<EnrollmentDetailsResult>>>>(), It.IsAny<CancellationToken>()))
                .Returns(
                (
                    Func<CancellationToken, Task<Result<EnrollmentDetailsResult>>> operation,
                    CancellationToken ct) => operation(ct));
            Service = new EnrollmentService(UnitOfWork.Object, Clock, Promotion.Object,
                Cache.Object,
                new EnrollmentCardCacheOptions("test"), NullLogger<EnrollmentService>.Instance);
        }
    }
}
