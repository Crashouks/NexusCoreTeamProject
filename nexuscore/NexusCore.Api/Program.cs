using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using NexusCore.Api.Helpers;
using NexusCore.Api.Hubs;
using NexusCore.Api.Services;

var builder = WebApplication.CreateBuilder(args);

LoadParentEnvFile(builder.Environment.ContentRootPath);

builder.Services.AddSingleton<DbService>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<SessionExpiryService>();
builder.Services.AddSingleton<CloudServerService>();
builder.Services.AddSingleton<CloudDiagnosticsLog>();
builder.Services.AddSingleton<StreamPrivacyService>();
builder.Services.AddSingleton<ChatService>();
builder.Services.AddSingleton<ForumService>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<MigrationService>();
builder.Services.AddSingleton<MediaFileService>();

var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET")
    ?? builder.Configuration["JWT_SECRET"]
    ?? "nexuscore_jwt_secret_key_2025_min32chars";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Keep JWT claim names as-is (userId, role) — default inbound mapping breaks role checks.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            RoleClaimType = "role",
            NameClaimType = "username"
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (string.IsNullOrEmpty(context.Token)
                    && context.Request.Cookies.TryGetValue(AuthCookie.Name, out var cookieToken)
                    && !string.IsNullOrEmpty(cookieToken))
                {
                    context.Token = cookieToken;
                    return Task.CompletedTask;
                }

                var path = context.HttpContext.Request.Path;
                if (string.IsNullOrEmpty(context.Token) && path.StartsWithSegments("/hubs"))
                {
                    var accessToken = context.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(accessToken))
                        context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

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
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(CorsOrigins.GetAllowedOrigins())
     .AllowAnyHeader()
     .AllowAnyMethod()
     .AllowCredentials()));

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

try
{
    await app.Services.GetRequiredService<MigrationService>().RunAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Migration failed: {ex.Message}");
    Console.Error.WriteLine("Check MySQL is running and .env credentials are correct.");
    return;
}

app.UseForwardedHeaders();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "NexusCore API v1");
    c.RoutePrefix = "swagger";
});

app.UseCors();
app.UseAuthentication();
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
Console.WriteLine($"CORS origins: {string.Join(", ", CorsOrigins.GetAllowedOrigins())}");
Console.WriteLine($"Swagger UI: http://localhost:{port}/swagger");
Console.WriteLine($"Chat Hub: ws://localhost:{port}/hubs/chat");
Console.WriteLine($"Cloud Stream Hub: ws://localhost:{port}/hubs/cloud-stream");

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
