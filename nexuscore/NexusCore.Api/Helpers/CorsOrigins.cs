namespace NexusCore.Api.Helpers;

public static class CorsOrigins
{
    /// <summary>
    /// Origins the browser is allowed to call this API from.
    /// Production: ONLY what is configured in CORS_ALLOWED_ORIGINS (comma-separated) and/or PUBLIC_WEB_URL.
    /// Development: the same, plus the local Vite dev-server origins.
    /// </summary>
    public static string[] GetAllowedOrigins(bool isDevelopment = false)
    {
        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (isDevelopment)
        {
            origins.Add("http://localhost:5173");
            origins.Add("http://127.0.0.1:5173");
            origins.Add("http://localhost:5174");
            origins.Add("http://127.0.0.1:5174");

            var webPort = Environment.GetEnvironmentVariable("WEB_PORT") ?? "5173";
            if (webPort is not "5173" and not "5174")
            {
                origins.Add($"http://localhost:{webPort}");
                origins.Add($"http://127.0.0.1:{webPort}");
            }
        }

        var publicWeb = Environment.GetEnvironmentVariable("PUBLIC_WEB_URL")?.Trim().TrimEnd('/');
        if (!string.IsNullOrEmpty(publicWeb))
            origins.Add(publicWeb);

        var configuredOrigins = new[]
        {
            Environment.GetEnvironmentVariable("CORS_ALLOWED_ORIGINS"),
            Environment.GetEnvironmentVariable("CORS_EXTRA_ORIGINS")
        };
        foreach (var configured in configuredOrigins)
        {
            if (string.IsNullOrWhiteSpace(configured))
                continue;

            foreach (var origin in configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                    && uri.Scheme is "http" or "https"
                    && uri.AbsolutePath == "/"
                    && string.IsNullOrEmpty(uri.Query)
                    && string.IsNullOrEmpty(uri.Fragment)
                    && string.IsNullOrEmpty(uri.UserInfo))
                {
                    origins.Add(uri.GetLeftPart(UriPartial.Authority));
                }
            }
        }

        return origins.ToArray();
    }
}
