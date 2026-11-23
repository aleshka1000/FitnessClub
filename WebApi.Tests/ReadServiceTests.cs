using BusinessLogicLayer.Models;
using BusinessLogicLayer.Services;
using DataAccessLayer.UnitOfWork;
using DataAccessLayer.Models;
using DataAccessLayer.Repositories;
using Moq;

namespace WebApi.Tests;

[TestClass]
public class ReadServiceTests
{
    [TestMethod]
    public async Task Schedule_UsesLastReturnedRowForCursor_AndReturnsFinalPage()
    {
        var unitOfWork = new Mock<IUnitOfWork>(MockBehavior.Strict);
        var classes = new Mock<IClassRepository>(MockBehavior.Strict);
        unitOfWork.SetupGet(x => x.Class).Returns(classes.Object);
        var service = new ScheduleService(unitOfWork.Object);
        var start = new DateTime(2040, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddDays(1);
        var rows = Enumerable.Range(3, 3).Select(id => new ScheduleClassData(id, "Class", start,
            60, 7, "Room", 10, 4, 2)).ToList();
        classes.Setup(x => x.GetScheduleAsync(1, start, end, 7, null, 0, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(rows);
        var first = await service.GetAsync(1, new ScheduleQuery(start, end, 7, 2));
        Assert.IsTrue(first.IsSuccess);
        Assert.AreEqual(2, first.Value.Items.Count);
        Assert.AreEqual(6, first.Value.Items[0].FreeSeats);
        Assert.AreEqual(2, first.Value.Items[0].MySeats);
        Assert.IsNotNull(first.Value.ContinuationToken);

        classes.Setup(x => x.GetScheduleAsync(1, start, end, 7, start, 4, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(rows.Skip(2).ToList());
        var last = await service.GetAsync(1, new ScheduleQuery(start, end, 7, 2, first.Value.ContinuationToken));
        Assert.IsTrue(last.IsSuccess);
        Assert.AreEqual(5L, last.Value.Items.Single().ClassId);
        Assert.IsNull(last.Value.ContinuationToken);
        var invalid = await service.GetAsync(1, new ScheduleQuery(start, end, 8, 2, first.Value.ContinuationToken));
        Assert.AreEqual(BusinessErrorKind.Validation, invalid.Error.Kind);
    }

    [TestMethod]
    public async Task InvalidScheduleOrCursor_IsRejectedBeforeDatabaseAccess()
    {
        var unitOfWork = new Mock<IUnitOfWork>(MockBehavior.Strict);
        var service = new ScheduleService(unitOfWork.Object);
        var start = new DateTime(2040, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        foreach (var query in new[]
                 {
                     new ScheduleQuery(start, start), new ScheduleQuery(start, start.AddDays(1), Limit: 0),
                     new ScheduleQuery(start, start.AddDays(1), Limit: 101),
                     new ScheduleQuery(start, start.AddDays(1), ContinuationToken: "bad"),
                     new ScheduleQuery(start, start.AddDays(1), ContinuationToken: "e30"),
                     new ScheduleQuery(start, start.AddDays(1), ContinuationToken: "")
                 })
        {
            var result = await service.GetAsync(1, query);
            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(BusinessErrorKind.Validation, result.Error.Kind);
        }
        unitOfWork.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task InvertedLoadPeriod_IsRejectedBeforeDatabaseAccess()
    {
        var unitOfWork = new Mock<IUnitOfWork>(MockBehavior.Strict);
        var service = new AnalyticsService(unitOfWork.Object);
        var result = await service.GetLoadAsync(new LoadQuery(new DateOnly(2040, 1, 3), new DateOnly(2040, 1, 2)));
        Assert.AreEqual(BusinessErrorKind.Validation, result.Error.Kind);
        unitOfWork.VerifyNoOtherCalls();
    }
}
