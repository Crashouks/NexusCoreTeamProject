using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using NexusCore.Api.Controllers;
using NexusCore.Api.Data;
using NexusCore.Api.Resilience;
using NexusCore.Api.Security;
using NexusCore.Api.Services;

namespace NexusCore.Api.Tests;

internal static class ApiTestApp
{
    public static async Task<WebApplication> StartAsync(IImageUrlProbe? imageProbe = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing"
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] =
                "Server=127.0.0.1;Port=1;User ID=nexus_test;Password=isolated-test-password;Database=nexuscore;Connection Timeout=1"
        });

        builder.Services.AddSingleton<DbService>();
        builder.Services.AddSingleton<GameRepository>();
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<GameCatalogCache>();
        builder.Services.AddSingleton<MediaFileService>();
        builder.Services.AddSingleton<JwtTokenService>();
        builder.Services.AddSingleton(imageProbe ?? new StubImageUrlProbe(new ProbeResult(ProbeStatus.Image, "image/png")));
        builder.Services.AddNexusAuthentication(TestApp.Jwt);
        builder.Services.AddNexusRateLimiting(new RateLimitSettings());
        builder.Services.AddAuthorization();
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(HealthController).Assembly);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.MapControllers();

        await app.StartAsync();
        return app;
    }
}

internal sealed class StubImageUrlProbe(ProbeResult result) : IImageUrlProbe
{
    private readonly List<string> _requestedUrls = [];

    public IReadOnlyList<string> RequestedUrls
    {
        get
        {
            lock (_requestedUrls)
                return _requestedUrls.ToArray();
        }
    }

    public Task<ProbeResult> ProbeAsync(string url, CancellationToken cancellationToken = default)
    {
        lock (_requestedUrls)
            _requestedUrls.Add(url);
        return Task.FromResult(result);
    }
}
