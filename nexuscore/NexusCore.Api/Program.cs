using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.OpenApi;
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

// Register authentication and data services.
builder.Services.AddScoped<JwtTokenService>();
builder.Services.AddScoped<DbService>();
builder.Services.AddScoped<GameRepository>();
builder.Services.AddScoped<CloudServerService>();
builder.Services.AddScoped<StreamPrivacyService>();
builder.Services.AddSingleton<CloudDiagnosticsLog>();
builder.Services.AddSingleton<StreamTelemetryService>();
builder.Services.AddSingleton<SessionTelemetryService>();
builder.Services.AddScoped<ChatService>();
builder.Services.AddScoped<ForumService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<MigrationService>();
builder.Services.AddSingleton<MediaFileService>();

var jwtSettings = JwtSettings.Load(builder.Configuration, builder.Environment, msg => Console.Error.WriteLine($"[security] {msg}"));
builder.Services.AddNexusAuthentication(jwtSettings);

builder.Services.AddNexusRateLimiting(
    builder.Configuration.GetSection("RateLimiting").Get<RateLimitSettings>() ?? new RateLimitSettings());

builder.Services.AddRemoteImageProbe(
    builder.Configuration.GetSection("Resilience:RemoteImageProbe").Get<RemoteProbeOptions>() ?? new RemoteProbeOptions());

builder.Services.AddAuthorization();
builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = 2 * 1024 * 1024;
    o.EnableDetailedErrors = builder.Environment.IsDevelopment();
}).AddJsonProtocol(o =>
{
    o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        o.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
    });

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Only honour X-Forwarded-* from loopback (Tailscale Serve / local reverse proxy / ngrok agent)
    // plus anything listed in TRUSTED_PROXIES. Trusting every sender would let any client spoof its IP
    // and walk around per-client rate limits. The framework default already trusts loopback.
    options.ForwardLimit = 1;
    var extraProxies = Environment.GetEnvironmentVariable("TRUSTED_PROXIES") ?? builder.Configuration["TRUSTED_PROXIES"] ?? "";
    foreach (var raw in extraProxies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        if (IPAddress.TryParse(raw, out var proxyIp)) options.KnownProxies.Add(proxyIp);
});

const string FrontendCorsPolicy = "FrontendPolicy";
var allowedOrigins = CorsOrigins.GetAllowedOrigins(builder.Environment.IsDevelopment());
if (allowedOrigins.Length == 0)
    throw new InvalidOperationException(
        "No CORS origin configured. Set PUBLIC_WEB_URL (or CORS_ALLOWED_ORIGINS) to the deployed " +
        "frontend origin, e.g. https://app.example.com");

builder.Services.AddCors(o => o.AddPolicy(FrontendCorsPolicy, p =>
    p.WithOrigins(allowedOrigins)
     .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
     .AllowAnyHeader()          // SignalR sends X-SignalR-User-Agent
     .AllowCredentials()));     // auth cookie; safe because origins are an explicit list

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "NexusCore API",
        Version = "v1",
        Description = "NexusCore game store, cloud queue & real-time chat API"
    });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Example: Bearer {token}",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });
});

var app = builder.Build();

// Retry with exponential back-off while the database is still starting (e.g. MySQL container warming up).
// Migrations are idempotent, so repeating them after a partial run is safe.
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
        },
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

// Swagger is a development tool: off in Production unless ENABLE_SWAGGER=1.
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
app.UseRateLimiter();      // after authentication so limits can be keyed by user id
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

var port = Environment.GetEnvironmentVariable("PORT")
    ?? builder.Configuration["Port"]
    ?? "5000";
var bindHost = Environment.GetEnvironmentVariable("API_BIND");
if (string.IsNullOrWhiteSpace(bindHost))
    bindHost = Environment.GetEnvironmentVariable("NETWORK_MODE") == "1" ? "0.0.0.0" : "localhost";
app.Urls.Add($"http://{bindHost}:{port}");

var publicApi = Environment.GetEnvironmentVariable("PUBLIC_API_URL");
var publicWeb = Environment.GetEnvironmentVariable("PUBLIC_WEB_URL");
var httpsMode = Environment.GetEnvironmentVariable("HTTPS_MODE") == "1"
    || (publicWeb?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ?? false);
Console.WriteLine($"NexusCore API running on http://{bindHost}:{port}");
if (bindHost == "0.0.0.0")
    Console.WriteLine("Network mode: API reachable from other devices on this machine's IP or PUBLIC_API_URL");
if (httpsMode)
    Console.WriteLine("HTTPS mode: players should use PUBLIC_WEB_URL (Tailscale Serve or reverse proxy)");
if (!string.IsNullOrEmpty(publicApi))
    Console.WriteLine($"PUBLIC_API_URL (agent): {publicApi}");
if (!string.IsNullOrEmpty(publicWeb))
    Console.WriteLine($"PUBLIC_WEB_URL (players): {publicWeb}");
Console.WriteLine($"CORS origins: {string.Join(", ", allowedOrigins)}");
Console.WriteLine($"Swagger UI: http://localhost:{port}/swagger");
Console.WriteLine($"Chat Hub: ws://localhost:{port}/hubs/chat");
Console.WriteLine($"Cloud Stream Hub: ws://localhost:{port}/hubs/cloud-stream");

app.Run();
