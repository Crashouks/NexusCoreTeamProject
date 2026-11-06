using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.IdentityModel.Tokens;
using NexusCore.Api.Security;

namespace NexusCore.Api.Tests;

/// <summary>
/// Minimal in-memory host that wires up the *same* authentication and rate-limiting
/// extensions the real API uses (Program.cs), plus a few probe endpoints. No database needed.
/// </summary>
internal static class TestApp
{
    public const string Secret = "unit-test-secret-unit-test-secret-1234567890";
    public static readonly JwtSettings Jwt = new() { Secret = Secret };

    public static string Token(
        int userId = 1, string role = "user", TimeSpan? lifetime = null,
        string? secret = null, string? issuer = null, string? audience = null)
    {
        var now = DateTime.UtcNow;
        var life = lifetime ?? TimeSpan.FromMinutes(10);
        var notBefore = life < TimeSpan.Zero ? now + life - TimeSpan.FromMinutes(5) : now;
        var claims = new[]
        {
            new Claim("userId", userId.ToString()),
            new Claim("username", "user" + userId),
            new Claim("role", role),
        };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret ?? Secret));
        var token = new JwtSecurityToken(
            issuer ?? Jwt.Issuer, audience ?? Jwt.Audience, claims, notBefore, now + life,
            new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static async Task<WebApplication> StartAsync(RateLimitSettings? rateLimits = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Services.AddNexusAuthentication(Jwt);
        builder.Services.AddAuthorization();
        builder.Services.AddNexusRateLimiting(rateLimits ?? new RateLimitSettings());

        var app = builder.Build();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();

        app.MapGet("/api/open", () => Results.Ok()).AllowAnonymous();
        app.MapGet("/api/health", () => Results.Ok()).AllowAnonymous();
        app.MapGet("/uploads/pic.png", () => Results.Ok()).AllowAnonymous();
        app.MapGet("/api/secure", (ClaimsPrincipal user) => Results.Ok(new { id = user.FindFirst("userId")?.Value }))
            .RequireAuthorization();
        app.MapGet("/api/admin-only", () => Results.Ok())
            .RequireAuthorization(new AuthorizeAttribute { Roles = "admin" });
        app.MapPost("/api/sensitive", () => Results.Ok())
            .RequireAuthorization().RequireRateLimiting(RateLimitPolicies.Sensitive);
        app.MapPost("/api/login-like", () => Results.Ok())
            .AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Auth);

        await app.StartAsync();
        return app;
    }

    public static HttpRequestMessage Req(HttpMethod method, string url, string? bearer = null)
    {
        var r = new HttpRequestMessage(method, url);
        if (bearer != null) r.Headers.Authorization = new("Bearer", bearer);
        return r;
    }
}
