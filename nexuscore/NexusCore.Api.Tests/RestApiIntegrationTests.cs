using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using NexusCore.Api.Resilience;

namespace NexusCore.Api.Tests;

public class RestApiIntegrationTests
{
    [Fact]
    public async Task HealthEndpoint_ShouldReturnOkAndStatusPayload()
    {
        await using var app = await ApiTestApp.StartAsync();
        using var http = app.GetTestClient();

        var response = await http.GetAsync("/api/health");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.Equal("NexusCore", body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task RegisterEndpoint_ShouldReturnValidationError_WhenRegistrationDataIsInvalid()
    {
        await using var app = await ApiTestApp.StartAsync();
        using var http = app.GetTestClient();

        var response = await http.PostAsJsonAsync("/api/auth/register", new
        {
            username = "ab",
            email = "player@example.test",
            password = "secure123"
        });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_ERROR", body.GetProperty("code").GetString());
        Assert.Equal("Username must be 3–50 characters", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task LoginEndpoint_ShouldReturnValidationError_WhenEmailIsMalformed()
    {
        await using var app = await ApiTestApp.StartAsync();
        using var http = app.GetTestClient();

        var response = await http.PostAsJsonAsync("/api/auth/login", new
        {
            email = "not-an-email",
            password = "some-password"
        });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_ERROR", body.GetProperty("code").GetString());
        Assert.Equal("Invalid email format", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ProtectedMediaEndpoint_ShouldRejectAnonymousRequest()
    {
        await using var app = await ApiTestApp.StartAsync();
        using var http = app.GetTestClient();

        var response = await http.GetAsync("/api/media/validate-url?url=https%3A%2F%2Fimages.example.test%2Fcover.png");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MediaUrlValidation_ShouldUseProbeAndReturnItsImageResult()
    {
        const string imageUrl = "https://images.example.test/cover.png";
        var probe = new StubImageUrlProbe(new ProbeResult(ProbeStatus.Image, "image/png"));
        await using var app = await ApiTestApp.StartAsync(probe);
        using var http = app.GetTestClient();
        using var request = TestApp.Req(
            HttpMethod.Get,
            "/api/media/validate-url?url=https%3A%2F%2Fimages.example.test%2Fcover.png",
            TestApp.Token());

        var response = await http.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("valid").GetBoolean());
        Assert.Equal("image/png", body.GetProperty("contentType").GetString());
        Assert.Equal([imageUrl], probe.RequestedUrls);
    }

    [Theory]
    [InlineData(ProbeStatus.NotImage, HttpStatusCode.BadRequest, "NOT_IMAGE")]
    [InlineData(ProbeStatus.TimedOut, HttpStatusCode.GatewayTimeout, "UPSTREAM_TIMEOUT")]
    [InlineData(ProbeStatus.CircuitOpen, HttpStatusCode.ServiceUnavailable, "UPSTREAM_UNAVAILABLE")]
    [InlineData(ProbeStatus.Blocked, HttpStatusCode.BadRequest, "INVALID_URL")]
    public async Task MediaUrlValidation_ShouldMapProbeOutcomeToHttpResponse(
        ProbeStatus status, HttpStatusCode expectedStatus, string expectedCode)
    {
        var probe = new StubImageUrlProbe(new ProbeResult(status, RetryAfterSeconds: status == ProbeStatus.CircuitOpen ? 7 : null));
        await using var app = await ApiTestApp.StartAsync(probe);
        using var http = app.GetTestClient();
        using var request = TestApp.Req(
            HttpMethod.Get,
            "/api/media/validate-url?url=https%3A%2F%2Fimages.example.test%2Fcover.png",
            TestApp.Token());

        var response = await http.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedCode, body.GetProperty("code").GetString());
        Assert.Single(probe.RequestedUrls);
        if (status == ProbeStatus.CircuitOpen)
            Assert.Equal("7", response.Headers.GetValues("Retry-After").Single());
    }
}

public class ApiExceptionHandlingTests
{
    [Fact]
    public async Task DatabaseHealthEndpoint_ShouldReturnSanitizedUnavailableResponse_WhenDatabaseCannotBeReached()
    {
        await using var app = await ApiTestApp.StartAsync();
        using var http = app.GetTestClient();

        var response = await http.GetAsync("/api/health/db");
        var content = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(content).RootElement;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("db_unavailable", body.GetProperty("status").GetString());
        Assert.DoesNotContain("isolated-test-password", content, StringComparison.Ordinal);
        Assert.DoesNotContain("error", body.EnumerateObject().Select(property => property.Name));
    }
}
