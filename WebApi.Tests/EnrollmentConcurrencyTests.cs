using System.Net;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Tests;

[TestClass]
public class EnrollmentConcurrencyTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ExternalUpdate_IsDetectedWithoutManualVersionChange(bool changeClass)
    {
        var pause = new PauseActiveSeats();
        await using var host = new EnrollmentTestHost(pause);
        var scenario = await host.ScenarioAsync();
        var request = host.PostAsync(scenario.UserId, scenario.ClassId, 1, "key");
        await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));

        try
        {
            await host.DbAsync(db => changeClass
                ? db.Classes.Where(x => x.Id == scenario.ClassId)
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.Capacity, 0))
                : db.Passes.Where(x => x.UserId == scenario.UserId)
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.RemainingVisits, 0)));
        }
        finally
        {
            pause.Continue.TrySetResult();
        }

        using var response = await request;
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        Assert.AreEqual(changeClass ? "capacity-exceeded" : "pass-exhausted",
            (await EnrollmentTestHost.JsonAsync(response)).GetProperty("reason").GetString());
        await host.DbAsync(async db =>
        {
            Assert.AreEqual(changeClass ? 0 : 4, await db.Classes.Where(x => x.Id == scenario.ClassId)
                .Select(x => x.Capacity).SingleAsync());
            Assert.AreEqual(changeClass ? 10 : 0, await db.Passes.Where(x => x.UserId == scenario.UserId)
                .Select(x => x.RemainingVisits).SingleAsync());
            Assert.AreEqual(0, await db.Enrollments.CountAsync(x => x.UserId == scenario.UserId));
            Assert.AreEqual(0, await db.IdempotencyRequests.CountAsync(x => x.UserId == scenario.UserId));

            if (changeClass)
            {
                Assert.AreEqual(scenario.PassVersion, await db.Passes.Where(x => x.UserId == scenario.UserId)
                    .Select(x => x.Version).SingleAsync());
            }
            else
            {
                Assert.AreEqual(scenario.ClassVersion, await db.Classes.Where(x => x.Id == scenario.ClassId)
                    .Select(x => x.Version).SingleAsync());
            }

            return true;
        });
    }

    [TestMethod]
    public async Task FailureAfterSavingBooking_RollsBackBookingPassVersionsAndKey()
    {
        await using var host = new EnrollmentTestHost(new FailIdempotencyCompletion());
        var scenario = await host.ScenarioAsync();
        using var response = await host.PostAsync(scenario.UserId, scenario.ClassId, 2, "key");
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        await host.AssertUnchangedAsync(scenario);
    }

    [TestMethod]
    public async Task CompetingForLastSeat_OnlyOneBookingCommits()
    {
        await using var host = new EnrollmentTestHost(new ActiveSeatsBarrier());
        var scenario = await host.ScenarioAsync(capacity: 1);
        var otherUser = await host.AddUserAsync();
        var responses = await Task.WhenAll(host.PostAsync(scenario.UserId, scenario.ClassId),
            host.PostAsync(otherUser, scenario.ClassId));
        try
        {
            CollectionAssert.AreEquivalent(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict },
                responses.Select(x => x.StatusCode).ToArray());
            var refusal = await EnrollmentTestHost.JsonAsync(responses.Single(x => x.StatusCode == HttpStatusCode.Conflict));
            Assert.AreEqual("capacity-exceeded", refusal.GetProperty("reason").GetString());
            await host.DbAsync(async db =>
            {
                Assert.AreEqual(1, await db.Enrollments.Where(x => x.ClassId == scenario.ClassId).SumAsync(x => x.Seats));
                Assert.AreEqual(1, await db.IdempotencyRequests.CountAsync(x => x.ClassId == scenario.ClassId));
                Assert.AreEqual(19, await db.Passes.Where(x => x.UserId == scenario.UserId || x.UserId == otherUser)
                    .SumAsync(x => x.RemainingVisits));
                return true;
            });
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [TestMethod]
    public async Task CompetingForPassAcrossClasses_OnlyOneVisitIsSpent()
    {
        await using var host = new EnrollmentTestHost(new ActiveSeatsBarrier());
        var scenario = await host.ScenarioAsync(visits: 1);
        var otherClass = await host.AddClassAsync();
        var otherVersion = await host.DbAsync(db => db.Classes.Where(x => x.Id == otherClass)
            .Select(x => x.Version).SingleAsync());
        var responses = await Task.WhenAll(host.PostAsync(scenario.UserId, scenario.ClassId),
            host.PostAsync(scenario.UserId, otherClass));
        try
        {
            CollectionAssert.AreEquivalent(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict },
                responses.Select(x => x.StatusCode).ToArray());
            var refusal = await EnrollmentTestHost.JsonAsync(responses.Single(x => x.StatusCode == HttpStatusCode.Conflict));
            Assert.AreEqual("pass-exhausted", refusal.GetProperty("reason").GetString());
            await host.DbAsync(async db =>
            {
                Assert.AreEqual(0, (await db.Passes.SingleAsync(x => x.UserId == scenario.UserId)).RemainingVisits);
                Assert.AreEqual(1, await db.Enrollments.CountAsync(x => x.UserId == scenario.UserId));
                var firstVersion = await db.Classes.Where(x => x.Id == scenario.ClassId)
                    .Select(x => x.Version).SingleAsync();
                var secondVersion = await db.Classes.Where(x => x.Id == otherClass)
                    .Select(x => x.Version).SingleAsync();
                Assert.AreEqual(1, (firstVersion != scenario.ClassVersion ? 1 : 0)
                    + (secondVersion != otherVersion ? 1 : 0));
                return true;
            });
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [TestMethod]
    public async Task ConcurrentIdenticalKeys_CreateExactlyOneBooking()
    {
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync(capacity: 2, visits: 2);
        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => host.PostAsync(scenario.UserId, scenario.ClassId, 2, "same-key")));
        try
        {
            Assert.IsTrue(responses.All(x => x.StatusCode == HttpStatusCode.Created));
            var bodies = await Task.WhenAll(responses.Select(x => x.Content.ReadAsStringAsync()));
            Assert.AreEqual(1, bodies.Distinct().Count());
            await host.DbAsync(async db =>
            {
                Assert.AreEqual(0, (await db.Passes.SingleAsync(x => x.UserId == scenario.UserId)).RemainingVisits);
                Assert.AreEqual(1, await db.Enrollments.CountAsync(x => x.UserId == scenario.UserId));
                Assert.AreEqual(1, await db.IdempotencyRequests.CountAsync(x => x.UserId == scenario.UserId));
                return true;
            });
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(4)]
    public async Task ConcurrencyFailure_RetriesWholeOperation_WithoutDuplicateCharges(int failures)
    {
        var interceptor = new ForcedConcurrencyFailures(failures);
        await using var host = new EnrollmentTestHost(interceptor);
        var scenario = await host.ScenarioAsync();
        interceptor.UserId = scenario.UserId;
        using var response = await host.PostAsync(scenario.UserId, scenario.ClassId, 2, "key");
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.AreEqual(failures + 1, interceptor.Attempts);
        await host.DbAsync(async db =>
        {
            var pass = await db.Passes.SingleAsync(x => x.UserId == scenario.UserId);
            Assert.AreEqual(8, pass.RemainingVisits);
            Assert.AreNotEqual(scenario.PassVersion, pass.Version);
            Assert.AreEqual(1, await db.Enrollments.CountAsync(x => x.UserId == scenario.UserId));
            Assert.AreEqual(1, await db.IdempotencyRequests.CountAsync(x => x.UserId == scenario.UserId));
            return true;
        });
    }

    [TestMethod]
    public async Task FiveConflicts_Return503_AndReleaseKey()
    {
        var interceptor = new ForcedConcurrencyFailures(5);
        await using var host = new EnrollmentTestHost(interceptor);
        var scenario = await host.ScenarioAsync();
        interceptor.UserId = scenario.UserId;
        using var failed = await host.PostAsync(scenario.UserId, scenario.ClassId, 2, "key");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.AreEqual(503, (await EnrollmentTestHost.JsonAsync(failed)).GetProperty("status").GetInt32());
        Assert.AreEqual(5, interceptor.Attempts);
        await host.AssertUnchangedAsync(scenario);
        using var recovered = await host.PostAsync(scenario.UserId, scenario.ClassId, 2, "key");
        Assert.AreEqual(HttpStatusCode.Created, recovered.StatusCode);
    }

    [TestMethod]
    public async Task ConcurrentDifferentBodiesWithSameKey_OneCreatesAndOneConflicts()
    {
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        var responses = await Task.WhenAll(host.PostAsync(scenario.UserId, scenario.ClassId, 1, "key"),
            host.PostAsync(scenario.UserId, scenario.ClassId, 2, "key"));
        try
        {
            CollectionAssert.AreEquivalent(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict },
                responses.Select(x => x.StatusCode).ToArray());
            Assert.AreEqual("idempotency-key-reused", (await EnrollmentTestHost.JsonAsync(
                responses.Single(x => x.StatusCode == HttpStatusCode.Conflict))).GetProperty("reason").GetString());
            Assert.AreEqual(1, await host.DbAsync(db => db.Enrollments.CountAsync(x => x.UserId == scenario.UserId)));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }
}
