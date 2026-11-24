using System.Net;
using System.Text.Json;

namespace WebApi.Tests;

internal static class ApiAssertions
{
    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.Clone();
    }

    public static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());

        return await JsonAsync(response);
    }

    public static async Task<long> CreatedIdAsync(HttpResponseMessage response)
    {
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());

        return (await JsonAsync(response)).GetProperty("id").GetInt64();
    }

    public static async Task ConflictAsync(HttpResponseMessage response, string reason)
    {
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, await response.Content.ReadAsStringAsync());
        var body = await JsonAsync(response);
        Assert.AreEqual(409, body.GetProperty("status").GetInt32());
        Assert.AreEqual(reason, body.GetProperty("reason").GetString());
    }
}
