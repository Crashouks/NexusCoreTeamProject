using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using NexusCore.Api.Security;

namespace NexusCore.Api.Tests;

/// <summary>Acceptance: exceeding the request limit returns 429 Too Many Requests.</summary>
public class RateLimitingTests
{
    [Fact]
    public void Defaults_MatchTheRequirement_TenRequestsPerMinute()
    {
        var s = new RateLimitSettings();
        Assert.Equal(10, s.SensitivePermitLimit);
        Assert.Equal(60, s.SensitiveWindowSeconds);
    }

    [Fact]
    public async Task SensitiveEndpoint_Allows10_Then429_WithRetryAfterAndJsonBody()
    {
        await using var app = await TestApp.StartAsync();
        var http = app.GetTestClient();
        var token = TestApp.Token(userId: 7);

        for (var i = 1; i <= 10; i++)
        {
            var ok = await http.SendAsync(TestApp.Req(HttpMethod.Post, "/api/sensitive", token));
            Assert.True(ok.StatusCode == HttpStatusCode.OK, $"request #{i} should pass but was {(int)ok.StatusCode}");
        }

        var limited = await http.SendAsync(TestApp.Req(HttpMethod.Post, "/api/sensitive", token));

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.TryGetValues("Retry-After", out var values));
        Assert.InRange(int.Parse(values!.Single()), 1, 60);
        Assert.Equal("RATE_LIMITED", (await limited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Limit_IsPerClient_OneUserCannotExhaustAnothersQuota()
    {
        await using var app = await TestApp.StartAsync();
        var http = app.GetTestClient();
        var heavy = TestApp.Token(userId: 1);
        var light = TestApp.Token(userId: 2);

        for (var i = 0; i < 11; i++)
            await http.SendAsync(TestApp.Req(HttpMethod.Post, "/api/sensitive", heavy));

        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await http.SendAsync(TestApp.Req(HttpMethod.Post, "/api/sensitive", heavy))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await http.SendAsync(TestApp.Req(HttpMethod.Post, "/api/sensitive", light))).StatusCode);
    }

    [Fact]
    public async Task AuthPolicy_LimitsUnauthenticatedCallers_ByIp()
    {
        await using var app = await TestApp.StartAsync(new RateLimitSettings { AuthPermitLimit = 3 });
        var http = app.GetTestClient();

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/api/login-like", null)).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await http.PostAsync("/api/login-like", null)).StatusCode);
    }

    [Fact]
    public async Task ForgedUserIdClaim_CannotChooseABucket()
    {
        // The login/auth policy must key on IP, never on a claim the caller controls.
        await using var app = await TestApp.StartAsync(new RateLimitSettings { AuthPermitLimit = 2 });
        var http = app.GetTestClient();

        for (var i = 0; i < 2; i++)
            await http.SendAsync(TestApp.Req(HttpMethod.Post, "/api/login-like", TestApp.Token(userId: 100 + i)));

        var r = await http.SendAsync(TestApp.Req(HttpMethod.Post, "/api/login-like", TestApp.Token(userId: 999)));
        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
    }

    [Fact]
    public async Task GlobalLimiter_AppliesToApi_ButExemptsHealthAndStaticFiles()
    {
        await using var app = await TestApp.StartAsync(new RateLimitSettings { GlobalPermitLimit = 3 });
        var http = app.GetTestClient();

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/health")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/uploads/pic.png")).StatusCode);
        }

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/open")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await http.GetAsync("/api/open")).StatusCode);
    }

    [Fact]
    public async Task Rejected_Request_DoesNotReachTheEndpoint_Even_WhenTokenIsInvalid()
    {
        // 429 is decided before authorization, so a flood of bad tokens is throttled too.
        await using var app = await TestApp.StartAsync(new RateLimitSettings { GlobalPermitLimit = 2 });
        var http = app.GetTestClient();

        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(TestApp.Req(HttpMethod.Get, "/api/secure", "garbage"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await http.SendAsync(TestApp.Req(HttpMethod.Get, "/api/secure", "garbage"))).StatusCode);
    }
}
