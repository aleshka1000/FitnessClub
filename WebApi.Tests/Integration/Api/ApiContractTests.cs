using System.Net;
using System.Text.Json;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Tests;

[TestClass]
public class ApiContractTests
{
    [TestMethod]
    public async Task Swagger_DeclaresRequiredBodyAndIdempotencyKey()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        using var client = host.Client();

        // Act
        using var response = await client.GetAsync("/swagger/v1/swagger.json");

        // Assert
        var body = await ApiAssertions.JsonAsync(response);
        var operation = body.GetProperty("paths").GetProperty("/enrollments").GetProperty("post");
        Assert.IsTrue(operation.GetProperty("requestBody").GetProperty("required").GetBoolean());
        var header = operation.GetProperty("parameters").EnumerateArray()
            .Single(x => x.GetProperty("name").GetString() == "Idempotency-Key");
        Assert.IsTrue(header.GetProperty("required").GetBoolean());
    }

    [TestMethod]
    public async Task Swagger_DeclaresRequiredTypedQueryDates()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        using var client = host.Client();

        // Act
        var swagger = await ApiAssertions.GetJsonAsync(client, "/swagger/v1/swagger.json");

        // Assert
        foreach (var (path, format) in new[] { ("/schedule", "date-time"), ("/analytics/load", "date") })
        {
            var parameters = swagger.GetProperty("paths").GetProperty(path).GetProperty("get")
                .GetProperty("parameters").EnumerateArray().ToArray();

            foreach (var name in new[] { "from", "to" })
            {
                var parameter = parameters.Single(x => x.GetProperty("name").GetString() == name);
                Assert.AreEqual("query", parameter.GetProperty("in").GetString());
                Assert.IsTrue(parameter.GetProperty("required").GetBoolean());
                Assert.AreEqual("string", parameter.GetProperty("schema").GetProperty("type").GetString());
                Assert.AreEqual(format, parameter.GetProperty("schema").GetProperty("format").GetString());
            }
        }
    }
}
