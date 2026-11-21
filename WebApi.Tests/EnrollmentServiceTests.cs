using BusinessLogicLayer.Models;
using BusinessLogicLayer.Services;
using DataAccessLayer.UnitOfWork;
using Moq;

namespace WebApi.Tests;

[TestClass]
public class EnrollmentServiceTests
{
    [TestMethod]
    [DataRow(0, "key")]
    [DataRow(-1, "key")]
    [DataRow(1, "")]
    [DataRow(1, "   ")]
    public async Task InvalidInput_IsRejectedBeforeDatabaseAccess(int seats, string key)
    {
        var unitOfWork = new Mock<IUnitOfWork>(MockBehavior.Strict);
        var service = new EnrollmentService(unitOfWork.Object, TimeProvider.System);
        var result = await service.CreateAsync(1, new CreateEnrollmentCommand(1, seats, key));
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(BusinessErrorKind.Validation, result.Error.Kind);
        unitOfWork.VerifyNoOtherCalls();
    }
}
