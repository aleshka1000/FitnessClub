using System.Net;
using System.Text;

namespace WebApi.Tests;

[TestClass]
public class EnrollmentOwnershipApiTests
{
    [TestMethod]
    [DataRow("GET", "")]
    [DataRow("POST", "/seats")]
    [DataRow("POST", "/cancel")]
    public async Task ForeignAndMissingBooking_Return404WithoutChanges(string method, string suffix)
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        using var created = await host.PostAsync(scenario.UserId, scenario.ClassId);
        var id = await ApiAssertions.CreatedIdAsync(created);
        var other = await host.AddUserAsync();
        var before = await host.GetClassStateAsync(scenario.ClassId, scenario.UserId, other);
        using var client = host.Client(other);

        foreach (var target in new[] { id, long.MaxValue })
        {
            // Arrange
            using var request = new HttpRequestMessage(new HttpMethod(method), $"/enrollments/{target}{suffix}");

            if (suffix == "/seats")
                request.Content = new StringContent("{\"seats\":2}", Encoding.UTF8, "application/json");

            // Act
            using var response = await client.SendAsync(request);

            // Assert
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        }

        // Assert
        Assert.AreEqual(before, await host.GetClassStateAsync(scenario.ClassId, scenario.UserId, other));
    }

    [TestMethod]
    [DataRow("GET", "/enrollments/not-a-number")]
    [DataRow("POST", "/enrollments/not-a-number/seats")]
    public async Task MalformedBookingId_Returns400(string method, string path)
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        using var client = host.Client(await host.AddUserAsync());
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        if (method == "POST")
            request.Content = new StringContent("{\"seats\":2}", Encoding.UTF8, "application/json");

        // Act
        using var response = await client.SendAsync(request);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
