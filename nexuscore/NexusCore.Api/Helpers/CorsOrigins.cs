namespace NexusCore.Api.Helpers;

public static class CorsOrigins
{
    public static string[] GetAllowedOrigins()
    {
        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "http://localhost:5173",
            "http://127.0.0.1:5173",
            "http://localhost:5174",
            "http://127.0.0.1:5174",
        };

        var webPort = Environment.GetEnvironmentVariable("WEB_PORT") ?? "5173";
        if (webPort is not "5173" and not "5174")
        {
            origins.Add($"http://localhost:{webPort}");
            origins.Add($"http://127.0.0.1:{webPort}");
        }

        var publicWeb = Environment.GetEnvironmentVariable("PUBLIC_WEB_URL")?.Trim().TrimEnd('/');
        if (!string.IsNullOrEmpty(publicWeb))
            origins.Add(publicWeb);

        // Extra browser origins that may call the API / open the SignalR hub with credentials, e.g. the same PC
        // reached by both its Tailscale IP and its MagicDNS name:
        //   CORS_EXTRA_ORIGINS=http://100.101.102.103:5173,https://my-pc.tail1234.ts.net
        var extra = Environment.GetEnvironmentVariable("CORS_EXTRA_ORIGINS");
        if (!string.IsNullOrWhiteSpace(extra))
        {
            foreach (var origin in extra.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                    origins.Add(origin.TrimEnd('/'));
            }
        }

        return origins.ToArray();
    }
}
