using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using NexusCore.Api.Security;

namespace NexusCore.Api.Tests;

/// <summary>Acceptance: rejects requests without tokens, with expired tokens, or without proper permissions.</summary>
public class AuthenticationTests : IAsyncLifetime
{
    private Microsoft.AspNetCore.Builder.WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _app = await TestApp.StartAsync();
        _http = _app.GetTestClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private static async Task<string?> CodeOf(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    [Fact]
    public async Task NoToken_Returns401_Unauthenticated()
    {
        var r = await _http.GetAsync("/api/secure");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal(AuthenticationExtensions.Codes.Unauthenticated, await CodeOf(r));
    }

    [Fact]
    public async Task ExpiredToken_Returns401_TokenExpired()
    {
        var token = TestApp.Token(lifetime: TimeSpan.FromHours(-1));
        var r = await _http.SendAsync(TestApp.Req(HttpMethod.Get, "/api/secure", token));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal(AuthenticationExtensions.Codes.TokenExpired, await CodeOf(r));
    }

    [Fact]
    public async Task TokenSignedWithOtherKey_Returns401_TokenInvalid()
    {
        var token = TestApp.Token(secret: "another-secret-another-secret-another-secret!!");
        var r = await _http.SendAsync(TestApp.Req(HttpMethod.Get, "/api/secure", token));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal(AuthenticationExtensions.Codes.TokenInvalid, await CodeOf(r));
    }

    [Theory]
    [InlineData("evil-issuer", null)]
    [InlineData(null, "evil-audience")]
    public async Task TokenWithWrongIssuerOrAudience_Returns401(string? issuer, string? audience)
    {
        var token = TestApp.Token(issuer: issuer, audience: audience);
        var r = await _http.SendAsync(TestApp.Req(HttpMethod.Get, "/api/secure", token));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task UnsignedAlgNoneToken_Returns401()
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var forged = $"{B64("{\"alg\":\"none\",\"typ\":\"JWT\"}")}.{B64($"{{\"userId\":\"1\",\"role\":\"admin\",\"exp\":{exp}}}")}.";
        var r = await _http.SendAsync(TestApp.Req(HttpMethod.Get, "/api/admin-only", forged));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task MalformedToken_Returns401()
    {
        var r = await _http.SendAsync(TestApp.Req(HttpMethod.Get, "/api/secure", "not.a.jwt"));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task ValidToken_Returns200_WithUserIdClaim()
    {
        var r = await _http.SendAsync(TestApp.Req(HttpMethod.Get, "/api/secure", TestApp.Token(userId: 42)));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("42", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task TokenInAuthCookie_IsAccepted()
    {
        var req = TestApp.Req(HttpMethod.Get, "/api/secure");
        req.Headers.Add("Cookie", $"nc_auth={TestApp.Token()}");
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task TokenInQueryString_IsIgnoredOutsideHubs()
    {
        var r = await _http.GetAsync($"/api/secure?access_token={TestApp.Token()}");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task UserRoleOnAdminEndpoint_Returns403_Forbidden()
    {
        var r = await _http.SendAsync(TestApp.Req(HttpMethod.Get, "/api/admin-only", TestApp.Token(role: "user")));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(AuthenticationExtensions.Codes.Forbidden, await CodeOf(r));
    }

    [Fact]
    public async Task AdminRoleOnAdminEndpoint_Returns200()
    {
        var r = await _http.SendAsync(TestApp.Req(HttpMethod.Get, "/api/admin-only", TestApp.Token(role: "admin")));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task NoToken_OnAdminEndpoint_Returns401_NotForbidden()
    {
        var r = await _http.GetAsync("/api/admin-only");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task AnonymousEndpoint_StaysOpen()
    {
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/api/open")).StatusCode);
    }
}

public class JwtSettingsTests
{
    private static IConfiguration Config(string? secret) =>
        new ConfigurationBuilder().AddInMemoryCollection(secret == null
            ? new Dictionary<string, string?>()
            : new() { ["JWT_SECRET"] = secret }).Build();

    private sealed class FakeEnv(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static IHostEnvironment Env(string name) => new FakeEnv(name);

    [Fact]
    public void MissingSecret_InProduction_FailsFast()
    {
        Environment.SetEnvironmentVariable("JWT_SECRET", null);
        Assert.Throws<InvalidOperationException>(() => JwtSettings.Load(Config(null), Env("Production")));
    }

    [Fact]
    public void ShortSecret_InProduction_FailsFast()
    {
        Environment.SetEnvironmentVariable("JWT_SECRET", null);
        Assert.Throws<InvalidOperationException>(() => JwtSettings.Load(Config("too-short"), Env("Production")));
    }

    [Fact]
    public void MissingSecret_InDevelopment_UsesDevKey_AndWarns()
    {
        Environment.SetEnvironmentVariable("JWT_SECRET", null);
        var warnings = new List<string>();
        var s = JwtSettings.Load(Config(null), Env("Development"), warnings.Add);
        Assert.Equal(JwtSettings.DevFallbackSecret, s.Secret);
        Assert.Single(warnings);
    }

    [Fact]
    public void StrongSecret_IsUsedAsIs()
    {
        Environment.SetEnvironmentVariable("JWT_SECRET", null);
        var secret = new string('x', 48);
        Assert.Equal(secret, JwtSettings.Load(Config(secret), Env("Production")).Secret);
    }
}
