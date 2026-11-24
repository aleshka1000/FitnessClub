using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace WebApi.Tests;

[TestClass]
public class ApiAuthorizationTests
{
    [TestMethod]
    [DataRow("POST", "/enrollments")]
    [DataRow("POST", "/enrollments/1/seats")]
    [DataRow("POST", "/enrollments/1/cancel")]
    [DataRow("GET", "/enrollments/1")]
    [DataRow("GET", "/schedule?from=2040-01-02T00:00:00Z&to=2040-01-03T00:00:00Z")]
    [DataRow("GET", "/analytics/load?from=2040-01-02&to=2040-01-03")]
    [DataRow("POST", "/enrollments/waitlist")]
    [DataRow("POST", "/enrollments/waitlist/1/cancel")]
    public async Task AllRoutes_RequireAuthentication(string method, string path)
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        using var client = host.Client();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("Idempotency-Key", "authentication-test");

        if (method == "POST")
            request.Content = new StringContent("{\"classId\":1,\"seats\":1}", Encoding.UTF8, "application/json");

        // Act
        using var response = await client.SendAsync(request);

        // Assert
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task InvalidJwt_IsRejected()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        using var client = host.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid");

        // Act
        using var response = await client.PostAsync("/enrollments",
            new StringContent("{\"classId\":1,\"seats\":1}", Encoding.UTF8, "application/json"));

        // Assert
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
