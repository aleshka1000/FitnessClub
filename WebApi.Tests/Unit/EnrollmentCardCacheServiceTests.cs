using System.Collections.Concurrent;
using System.Text.Json;
using BusinessLogicLayer.Interfaces;
using BusinessLogicLayer.Models;
using BusinessLogicLayer.Services;
using Common.Caching;
using DataAccessLayer.Models;
using DataAccessLayer.Repositories;
using DataAccessLayer.UnitOfWork;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;

namespace WebApi.Tests;

[TestClass]
public class EnrollmentCardCacheServiceTests
{
    [TestMethod]
    public async Task Hit_ReadsVersionsButDoesNotLoadFullCardOrRefreshTtl()
    {
        // Arrange
        var scenario = new Scenario();
        var card = EnrollmentDetailsResult.From(scenario.Data);
        scenario.Cache.Setup(
            x => x.GetAsync<EnrollmentDetailsResult>(
                "test:card:42:100:17:40",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(card);

        // Act
        var result = await scenario.Service.GetAsync(42, 100);

        // Assert
        Assert.IsTrue(result.IsSuccess);
        Assert.AreSame(card, result.Value);
        scenario.Repository.Verify(x => x.GetCardVersionsAsync(100, 42, It.IsAny<CancellationToken>()), Times.Once);
        scenario.Repository.Verify(
            x => x.GetDetailsAsync(
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        scenario.Cache.Verify(x => x.SetAsync(It.IsAny<string>(), It.IsAny<EnrollmentDetailsResult>(),
                It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Miss_StoresFullCardUnderVersionsFromFullQuery()
    {
        // Arrange
        var scenario = new Scenario();
        scenario.Data = scenario.Data with { RemainingVisits = 9, PassVersion = 41 };

        // Act
        var result = await scenario.Service.GetAsync(42, 100);

        // Assert
        Assert.AreEqual(9, result.Value.Pass.RemainingVisits);
        scenario.Cache.Verify(
            x => x.GetAsync<EnrollmentDetailsResult>(
                "test:card:42:100:17:40",
                It.IsAny<CancellationToken>()),
            Times.Once);
        scenario.Cache.Verify(x => x.SetAsync("test:card:42:100:17:41",
                It.Is<EnrollmentDetailsResult>(card => card.Pass.RemainingVisits == 9),
                TimeSpan.FromMinutes(5), It.IsAny<CancellationToken>()), Times.Once);
        scenario.Cache.Verify(x => x.SetAsync("test:card:42:100:17:40", It.IsAny<EnrollmentDetailsResult>(),
                It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task MissingOrForeignEnrollment_DoesNotAccessCache()
    {
        // Arrange
        var scenario = new Scenario();
        scenario.Repository.Setup(x => x.GetCardVersionsAsync(100, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((EnrollmentCardVersions?)null);

        // Act
        var result = await scenario.Service.GetAsync(42, 100);

        // Assert
        Assert.AreEqual(BusinessErrorKind.NotFound, result.Error.Kind);
        scenario.Cache.VerifyNoOtherCalls();
        scenario.Repository.Verify(
            x => x.GetDetailsAsync(
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task EnrollmentDisappearedAfterVersionQuery_DoesNotCache404()
    {
        // Arrange
        var scenario = new Scenario();
        scenario.Repository.Setup(x => x.GetDetailsAsync(100, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((EnrollmentDetailsData?)null);

        // Act
        var result = await scenario.Service.GetAsync(42, 100);

        // Assert
        Assert.AreEqual(BusinessErrorKind.NotFound, result.Error.Kind);
        scenario.Cache.Verify(x => x.SetAsync(It.IsAny<string>(), It.IsAny<EnrollmentDetailsResult>(),
                It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    [DataRow("connection")]
    [DataRow("timeout")]
    [DataRow("json")]
    public async Task CacheReadFailure_FallsBackToDatabase(string failure)
    {
        // Arrange
        var scenario = new Scenario();
        scenario.Cache.Setup(
            x => x.GetAsync<EnrollmentDetailsResult>(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(Failure(failure));

        // Act
        var result = await scenario.Service.GetAsync(42, 100);

        // Assert
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(10, result.Value.Pass.RemainingVisits);
        scenario.Repository.Verify(x => x.GetDetailsAsync(100, 42, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [DataRow("connection")]
    [DataRow("timeout")]
    public async Task CacheWriteFailure_ReturnsFreshCard(string failure)
    {
        // Arrange
        var scenario = new Scenario();
        scenario.Cache.Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<EnrollmentDetailsResult>(),
                It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>())).ThrowsAsync(Failure(failure));

        // Act
        var result = await scenario.Service.GetAsync(42, 100);

        // Assert
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(10, result.Value.Pass.RemainingVisits);
    }

    [TestMethod]
    public async Task Cancellation_IsNotTreatedAsCacheMiss()
    {
        // Arrange
        var scenario = new Scenario();
        scenario.Cache.Setup(
            x => x.GetAsync<EnrollmentDetailsResult>(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        // Act & Assert
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => scenario.Service.GetAsync(42, 100));

        // Assert
        scenario.Repository.Verify(x => x.GetDetailsAsync(100, 42, It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task LateOldFill_DoesNotReplaceCurrentVersion()
    {
        // Arrange
        var scenario = new Scenario();
        var cards = new ConcurrentDictionary<string, EnrollmentDetailsResult>();
        var oldWriteReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueOldWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Repository.Setup(x => x.GetCardVersionsAsync(100, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new EnrollmentCardVersions(scenario.Data.EnrollmentVersion, scenario.Data.PassVersion));
        scenario.Cache.Setup(
            x => x.GetAsync<EnrollmentDetailsResult>(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) => cards.GetValueOrDefault(key));
        scenario.Cache.Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<EnrollmentDetailsResult>(),
                It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Returns(async (string key, EnrollmentDetailsResult card, TimeSpan? _, CancellationToken ct) =>
        {
            if (card.Pass.RemainingVisits == 10)
            {
                oldWriteReached.TrySetResult();
                await continueOldWrite.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            }

            cards[key] = card;
        });

        // Act
        var oldRead = scenario.Service.GetAsync(42, 100);

        try
        {
            await oldWriteReached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            scenario.Data = scenario.Data with { RemainingVisits = 9, PassVersion = 41 };
            var currentRead = await scenario.Service.GetAsync(42, 100);
            Assert.AreEqual(9, currentRead.Value.Pass.RemainingVisits);
        }
        finally
        {
            continueOldWrite.TrySetResult();
        }

        await oldRead;
        var finalRead = await scenario.Service.GetAsync(42, 100);

        // Assert
        Assert.AreEqual(9, finalRead.Value.Pass.RemainingVisits);
        Assert.AreEqual(10, cards["test:card:42:100:17:40"].Pass.RemainingVisits);
        Assert.AreEqual(9, cards["test:card:42:100:17:41"].Pass.RemainingVisits);
        scenario.Repository.Verify(x => x.GetDetailsAsync(100, 42, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private static Exception Failure(string failure) => failure switch
        {
        "connection" => new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Cache unavailable."),
        "timeout" => new RedisTimeoutException("Cache timeout.", CommandStatus.Unknown),
        _ => new JsonException("Invalid cache payload.")
        };

    private sealed class Scenario
    {
        public EnrollmentDetailsData Data { get; set; } = new(100, "active", 1,
            new DateTime(2030, 1, 2, 12, 0, 0, DateTimeKind.Utc), 3, "Class",
            new DateTime(2030, 1, 3, 12, 0, 0, DateTimeKind.Utc), 60, false,
            4, "Room", 10, new DateOnly(2030, 2, 1), 17, 40);
        public Mock<IEnrollmentRepository> Repository { get; } = new(MockBehavior.Strict);
        public Mock<ICacheService> Cache { get; } = new(MockBehavior.Strict);
        public EnrollmentService Service { get; }

        public Scenario()
        {
            Repository.Setup(x => x.GetCardVersionsAsync(100, 42, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EnrollmentCardVersions(17, 40));
            Repository.Setup(x => x.GetDetailsAsync(100, 42, It.IsAny<CancellationToken>())).ReturnsAsync(() => Data);
            Cache.Setup(x => x.GetAsync<EnrollmentDetailsResult>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((EnrollmentDetailsResult?)null);
            Cache.Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<EnrollmentDetailsResult>(),
                    It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            var unitOfWork = new Mock<IUnitOfWork>(MockBehavior.Strict);
            unitOfWork.SetupGet(x => x.Enrollment).Returns(Repository.Object);
            Service = new EnrollmentService(unitOfWork.Object, TimeProvider.System,
                Mock.Of<IWaitlistPromotionService>(), Cache.Object,
                new EnrollmentCardCacheOptions("test"), NullLogger<EnrollmentService>.Instance);
        }
    }
}
