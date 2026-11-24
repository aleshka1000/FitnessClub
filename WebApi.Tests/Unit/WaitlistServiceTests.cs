using BusinessLogicLayer.Interfaces;
using BusinessLogicLayer.Models;
using BusinessLogicLayer.Services;
using DataAccessLayer.UnitOfWork;
using Moq;

namespace WebApi.Tests;

[TestClass]
public class WaitlistServiceTests
{
    [TestMethod]
    [DataRow(0, "key")]
    [DataRow(-1, "key")]
    [DataRow(1, "")]
    [DataRow(1, "   ")]
    public async Task InvalidJoin_IsRejectedBeforeDatabaseAccess(int seats, string key)
    {
        // Arrange
        var unit = new Mock<IUnitOfWork>(MockBehavior.Strict);
        var service = new WaitlistService(unit.Object, new ManualTimeProvider(), Mock.Of<IWaitlistPromotionService>());

        // Act
        var result = await service.JoinAsync(1, new JoinWaitlistCommand(1, seats, key));

        // Assert
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(BusinessErrorKind.Validation, result.Error.Kind);
        unit.VerifyNoOtherCalls();
    }
}
