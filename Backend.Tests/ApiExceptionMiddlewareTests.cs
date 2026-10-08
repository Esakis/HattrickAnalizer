using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

public class ApiExceptionMiddlewareTests
{
    [Theory]
    [InlineData("validation", 400, "validation_error")]
    [InlineData("unprocessable", 422, "unprocessable_request")]
    public async Task Middleware_returns_structured_client_errors(string kind, int expectedStatus, string expectedCode)
    {
        Exception error = kind == "validation"
            ? new ArgumentException("bad input")
            : new InvalidOperationException("no eligible XI");
        var builder = new WebHostBuilder().Configure(app =>
        {
            app.UseApiExceptionHandling(NullLogger.Instance);
            app.Run(_ => Task.FromException(error));
        });
        using var server = new TestServer(builder);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/");
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal((HttpStatusCode)expectedStatus, response.StatusCode);
        Assert.Equal(expectedCode, body.GetProperty("code").GetString());
        Assert.Equal(expectedStatus, body.GetProperty("status").GetInt32());
        Assert.NotEmpty(body.GetProperty("error").GetString()!);
    }
}
