using NexusCore.Api.Helpers;

namespace NexusCore.Api.Tests;

// Mutates process-wide environment variables, so it must not run in parallel with other tests that read them.
[CollectionDefinition("env", DisableParallelization = true)]
public class EnvCollection;

[Collection("env")]
public class CorsOriginsTests : IDisposable
{
    public CorsOriginsTests() => Reset();
    public void Dispose() => Reset();

    private static void Reset()
    {
        Environment.SetEnvironmentVariable("PUBLIC_WEB_URL", null);
        Environment.SetEnvironmentVariable("CORS_ALLOWED_ORIGINS", null);
        Environment.SetEnvironmentVariable("CORS_EXTRA_ORIGINS", null);
        Environment.SetEnvironmentVariable("WEB_PORT", null);
    }

    [Fact]
    public void Production_WithNothingConfigured_AllowsNoOrigin()
        => Assert.Empty(CorsOrigins.GetAllowedOrigins(isDevelopment: false));

    [Fact]
    public void Production_AllowsOnlyConfiguredFrontend_NotLocalhost()
    {
        Environment.SetEnvironmentVariable("PUBLIC_WEB_URL", "https://app.example.com/");
        var origins = CorsOrigins.GetAllowedOrigins(isDevelopment: false);
        Assert.Equal(["https://app.example.com"], origins);
    }

    [Fact]
    public void Development_AlsoAllowsViteDevServer()
    {
        var origins = CorsOrigins.GetAllowedOrigins(isDevelopment: true);
        Assert.Contains("http://localhost:5173", origins);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://evil.example.com/some/path")]
    [InlineData("ftp://app.example.com")]
    public void ExtraOrigins_RejectWildcardsPathsAndOtherSchemes(string bad)
    {
        Environment.SetEnvironmentVariable("CORS_ALLOWED_ORIGINS", bad);
        Assert.Empty(CorsOrigins.GetAllowedOrigins(isDevelopment: false));
    }

    [Fact]
    public void ExtraOrigins_AcceptsCommaSeparatedList()
    {
        Environment.SetEnvironmentVariable("CORS_ALLOWED_ORIGINS", "https://a.example.com, https://b.example.com/");
        var origins = CorsOrigins.GetAllowedOrigins(isDevelopment: false);
        Assert.Equal(2, origins.Length);
        Assert.Contains("https://b.example.com", origins);
    }

    [Fact]
    public void ExtraOrigins_AcceptsLegacyEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable("CORS_EXTRA_ORIGINS", "https://a.example.com");
        Assert.Equal(["https://a.example.com"], CorsOrigins.GetAllowedOrigins(isDevelopment: false));
    }
}
