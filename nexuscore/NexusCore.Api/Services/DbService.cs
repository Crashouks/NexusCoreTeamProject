using MySqlConnector;

namespace NexusCore.Api.Services;

public class DbService(IConfiguration config)
{
    /// <summary>
    /// Preferred: ConnectionStrings:DefaultConnection (env var ConnectionStrings__DefaultConnection).
    /// Fallback for local development: the legacy DB_HOST / DB_USER / DB_PASSWORD / DB_NAME settings.
    /// </summary>
    public string ConnectionString =>
        config.GetConnectionString("DefaultConnection") is { Length: > 0 } cs
            ? cs
            : $"Server={Get("DB_HOST", "localhost")};User={Get("DB_USER", "root")};Password={Get("DB_PASSWORD", "")};Database={Get("DB_NAME", "nexuscore")};";

    public MySqlConnection CreateConnection() => new(ConnectionString);

    private string Get(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) ?? config[key] ?? fallback;
}
