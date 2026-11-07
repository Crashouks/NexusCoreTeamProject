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

        var extra = string.Join(",",
            Environment.GetEnvironmentVariable("CORS_ALLOWED_ORIGINS"),
            Environment.GetEnvironmentVariable("CORS_EXTRA_ORIGINS"));
        foreach (var raw in extra.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var o = raw.TrimEnd('/');
            // An origin is scheme://host[:port] - reject wildcards and anything with a path.
            if (Uri.TryCreate(o, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                && uri.AbsolutePath == "/")
                origins.Add(o);
        }

        return origins.ToArray();
    }
}
