using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using NexusCore.Api.Hubs;
using NexusCore.Api.Resilience;
using NexusCore.Api.Security;
using NexusCore.Api.Services;
using Polly;
using Polly.Retry;

var builder = WebApplication.CreateBuilder(args);

// Додавання сервісів JWT Token та PasswordHasher
builder.Services.AddScoped<JwtTokenService>();
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddScoped<DbService>();
builder.Services.AddScoped<CloudServerService>();
builder.Services.AddScoped<StreamPrivacyService>();
builder.Services.AddSingleton<CloudDiagnosticsLog>();
builder.Services.AddSingleton<StreamTelemetryService>();

// Налаштування автентифікації JWT
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!)),
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"],
            ValidateLifetime = true
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddControllers();
builder.Services.AddSignalR();

var app = builder.Build();

// Порядок виклику Middleware є критично важливим!
app.UseAuthentication(); // 1. Спочатку перевіряємо хід подій автентифікації (хто ти?)
app.UseAuthorization();  // 2. Потім перевіряємо права доступу (чи дозволено?)

app.MapControllers();
app.MapHub<CloudStreamHub>("/hubs/cloud-stream");

app.Run();
