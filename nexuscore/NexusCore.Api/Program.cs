using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;
using MySqlConnector;
using NexusCore.Api.Data;
using NexusCore.Api.Helpers;
using NexusCore.Api.Hubs;
using NexusCore.Api.Resilience;
using NexusCore.Api.Security;
using NexusCore.Api.Services;
using Polly;
using Polly.Retry;

var builder = WebApplication.CreateBuilder(args);
DapperSetup.Configure();

builder.Services.AddScoped<JwtTokenService>();
builder.Services.AddScoped<DbService>();
builder.Services.AddScoped<GameRepository>();
builder.Services.AddScoped<CloudServerService>();
builder.Services.AddScoped<StreamPrivacyService>();
builder.Services.AddSingleton<CloudDiagnosticsLog>();
builder.Services.AddSingleton<StreamTelemetryService>();
builder.Services.AddSingleton<SessionTelemetryService>();
builder.Services.AddSingleton<ChatService>();
builder.Services.AddSingleton<ForumService>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<MigrationService>();
builder.Services.AddSingleton<MediaFileService>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<GameCatalogCache>();

var jwtSettings = JwtSettings.Load(builder.Configuration, builder.Environment, msg => Console.Error.WriteLine($"[security] {msg}"));
builder.Services.AddNexusAuthentication(jwtSettings);
builder.Services.AddNexusRateLimiting(
    builder.Configuration.GetSection("RateLimiting").Get<RateLimitSettings>() ?? new RateLimitSettings());

builder.Services.AddRemoteImageProbe(
    builder.Configuration.GetSection("Resilience:RemoteImageProbe").Get<RemoteProbeOptions>() ?? new RemoteProbeOptions());

builder.Services.AddAuthorization();
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 2 * 1024 * 1024;
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
}).AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;

    var extraProxies = Environment.GetEnvironmentVariable("TRUSTED_PROXIES") ?? builder.Configuration["TRUSTED_PROXIES"] ?? "";
    foreach (var raw in extraProxies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (IPAddress.TryParse(raw, out var proxyIp)) options.KnownProxies.Add(proxyIp);
    }
});

const string FrontendCorsPolicy = "FrontendPolicy";
var allowedOrigins = CorsOrigins.GetAllowedOrigins(builder.Environment.IsDevelopment());
if (allowedOrigins.Length == 0)
    throw new InvalidOperationException(
        "No CORS origin configured. Set PUBLIC_WEB_URL (or CORS_ALLOWED_ORIGINS) to the deployed frontend origin.");

builder.Services.AddCors(options => options.AddPolicy(FrontendCorsPolicy, policy =>
    policy.WithOrigins(allowedOrigins)
        .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
        .AllowAnyHeader()
        .AllowCredentials()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

var startupRetry = new ResiliencePipelineBuilder()
    .AddRetry(new RetryStrategyOptions
    {
        MaxRetryAttempts = 5,
        Delay = TimeSpan.FromSeconds(2),
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,
        ShouldHandle = args => ValueTask.FromResult(
            args.Outcome.Exception is MySqlException or SocketException or TimeoutException),
        OnRetry = args =>
        {
            Console.Error.WriteLine($"Database not ready (attempt {args.AttemptNumber + 1}), retrying in {args.RetryDelay.TotalSeconds:0.#}s: {args.Outcome.Exception?.Message}");
            return default;
        }
    })
    .Build();

try
{
    using var migrationScope = app.Services.CreateScope();
    var migrations = migrationScope.ServiceProvider.GetRequiredService<MigrationService>();
    await startupRetry.ExecuteAsync(async _ => await migrations.RunAsync(), CancellationToken.None);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Migration failed: {ex.Message}");
    Console.Error.WriteLine("Check MySQL is running and .env credentials are correct.");
    return;
}

app.UseForwardedHeaders();

if (app.Environment.IsDevelopment() || Environment.GetEnvironmentVariable("ENABLE_SWAGGER") == "1")
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "NexusCore API v1");
        c.RoutePrefix = "swagger";
    });
}

app.UseCors(FrontendCorsPolicy);
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

var uploadsPath = Path.Combine(app.Environment.ContentRootPath, "uploads");
Directory.CreateDirectory(uploadsPath);
app.UseStaticFiles(new StaticFileOptions
{
    RequestPath = "/uploads",
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(uploadsPath)
});

app.MapControllers();
app.MapHub<ChatHub>("/hubs/chat");
app.MapHub<CloudStreamHub>("/hubs/cloud-stream");

var port = Environment.GetEnvironmentVariable("PORT") ?? builder.Configuration["Port"] ?? "5000";
var bindHost = Environment.GetEnvironmentVariable("API_BIND");
if (string.IsNullOrWhiteSpace(bindHost))
    bindHost = Environment.GetEnvironmentVariable("NETWORK_MODE") == "1" ? "0.0.0.0" : "localhost";

app.Urls.Add($"http://{bindHost}:{port}");

Console.WriteLine($"NexusCore API running on http://{bindHost}:{port}");
if (bindHost == "0.0.0.0")
    Console.WriteLine("Network mode: API reachable from other devices on this machine's IP or PUBLIC_API_URL");

app.Run();

static void LoadParentEnvFile(string contentRoot)
{
    var envPath = Path.GetFullPath(Path.Combine(contentRoot, "..", ".env"));
    if (!File.Exists(envPath)) return;
    foreach (var line in File.ReadAllLines(envPath))
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
        var idx = trimmed.IndexOf('=');
        if (idx <= 0) continue;
        var key = trimmed[..idx].Trim();
        var value = trimmed[(idx + 1)..].Trim();
        Environment.SetEnvironmentVariable(key, value);
    }
}
