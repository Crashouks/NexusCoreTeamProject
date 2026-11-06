namespace NexusCore.Api.Security;

/// <summary>
/// Single source of truth for JWT signing/validation parameters. Used by both the token
/// issuer (<see cref="Services.JwtTokenService"/>) and the JwtBearer validator so the two
/// can never disagree about the secret, issuer or audience.
/// </summary>
public sealed class JwtSettings
{
    /// <summary>Only ever used when running in the Development environment with no JWT_SECRET configured.</summary>
    public const string DevFallbackSecret = "dev-only-insecure-secret-do-not-use-in-prod-0123456789";

    public const int MinSecretLength = 32;

    private static readonly string[] KnownPlaceholders =
    [
        "change_this",
        "change_me",
        "nexuscore_jwt_secret_key_2025_min32chars", // value that used to be committed in appsettings.json
    ];

    public required string Secret { get; init; }
    public string Issuer { get; init; } = "nexuscore-api";
    public string Audience { get; init; } = "nexuscore-client";

    /// <summary>Allowed clock drift when checking token expiry (default framework value is 5 minutes).</summary>
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromSeconds(30);

    public static bool IsPlaceholder(string secret) =>
        KnownPlaceholders.Any(p => secret.Contains(p, StringComparison.OrdinalIgnoreCase));

    public static JwtSettings Load(IConfiguration config, IHostEnvironment env, Action<string>? warn = null)
    {
        var secret = Environment.GetEnvironmentVariable("JWT_SECRET") ?? config["JWT_SECRET"];

        if (string.IsNullOrWhiteSpace(secret) || secret.Length < MinSecretLength)
        {
            if (!env.IsDevelopment())
                throw new InvalidOperationException(
                    $"JWT_SECRET is missing or shorter than {MinSecretLength} characters. " +
                    "Copy nexuscore/.env.example to nexuscore/.env and set a long random value " +
                    "(for example: openssl rand -base64 48).");

            warn?.Invoke("JWT_SECRET is not set - using an insecure development-only key.");
            secret = DevFallbackSecret;
        }
        else if (IsPlaceholder(secret))
        {
            warn?.Invoke("JWT_SECRET still contains a placeholder value. Replace it with a random secret before deploying.");
        }

        return new JwtSettings
        {
            Secret = secret,
            Issuer = Environment.GetEnvironmentVariable("JWT_ISSUER") ?? config["JWT_ISSUER"] ?? "nexuscore-api",
            Audience = Environment.GetEnvironmentVariable("JWT_AUDIENCE") ?? config["JWT_AUDIENCE"] ?? "nexuscore-client",
        };
    }
}
