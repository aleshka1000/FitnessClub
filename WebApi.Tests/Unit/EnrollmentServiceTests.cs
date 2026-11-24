using BusinessLogicLayer.Models;
using BusinessLogicLayer.Services;
using DataAccessLayer.UnitOfWork;
using Moq;
using BusinessLogicLayer.Interfaces;
using Common.Caching;
using Microsoft.Extensions.Logging.Abstractions;

namespace WebApi.Tests;

[TestClass]
public class EnrollmentServiceTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public async Task InvalidSeatChange_IsRejectedBeforeDatabaseAccess(int seats)
    {
        // Arrange
        var unitOfWork = new Mock<IUnitOfWork>(MockBehavior.Strict);
        var cache = new Mock<ICacheService>(MockBehavior.Strict);
        var service = new EnrollmentService(unitOfWork.Object, TimeProvider.System, Mock.Of<IWaitlistPromotionService>(),
            cache.Object, new EnrollmentCardCacheOptions("test"), NullLogger<EnrollmentService>.Instance);

        // Act
        var result = await service.ChangeSeatsAsync(1, 1, seats);

        // Assert
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(BusinessErrorKind.Validation, result.Error.Kind);
        unitOfWork.VerifyNoOtherCalls();
        cache.VerifyNoOtherCalls();
    }

    [TestMethod]
    [DataRow(0, "key")]
    [DataRow(-1, "key")]
    [DataRow(1, "")]
    [DataRow(1, "   ")]
    public async Task InvalidInput_IsRejectedBeforeDatabaseAccess(int seats, string key)
    {
        // Arrange
        var unitOfWork = new Mock<IUnitOfWork>(MockBehavior.Strict);
        var service = new EnrollmentService(unitOfWork.Object, TimeProvider.System, Mock.Of<IWaitlistPromotionService>(),
            Mock.Of<ICacheService>(), new EnrollmentCardCacheOptions("test"), NullLogger<EnrollmentService>.Instance);

        // Act
        var result = await service.CreateAsync(1, new CreateEnrollmentCommand(1, seats, key));

        // Assert
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(BusinessErrorKind.Validation, result.Error.Kind);
        unitOfWork.VerifyNoOtherCalls();
    }
}
