using MySqlConnector;

namespace NexusCore.Api.Services;

public sealed class DbService
{
    public string ConnectionString { get; }

    public DbService(IConfiguration config)
    {
        var configured = config.GetConnectionString("DefaultConnection");
        var builder = string.IsNullOrWhiteSpace(configured)
            ? BuildLegacyConnectionString(config)
            : new MySqlConnectionStringBuilder(configured);

        if (string.IsNullOrWhiteSpace(builder.UserID))
            throw new InvalidOperationException("DB_USER must be configured for the API.");
        if (builder.UserID.Equals("root", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The API must not connect as MySQL root.");
        if (string.IsNullOrEmpty(builder.Password))
            throw new InvalidOperationException("DB_PASSWORD must be configured for the API.");

        ConnectionString = builder.ConnectionString;
    }

    public MySqlConnection CreateConnection() => new(ConnectionString);

    private static MySqlConnectionStringBuilder BuildLegacyConnectionString(IConfiguration config)
    {
        string Get(string key, string fallback = "") =>
            Environment.GetEnvironmentVariable(key) ?? config[key] ?? fallback;

        var host = Get("DB_HOST", "localhost");
        var sslMode = Enum.TryParse<MySqlSslMode>(Get("DB_SSL_MODE"), true, out var parsed)
            ? parsed
            : (host is "localhost" or "127.0.0.1" or "::1"
                ? MySqlSslMode.Preferred
                : MySqlSslMode.Required);

        return new MySqlConnectionStringBuilder
        {
            Server = host,
            Port = uint.TryParse(Get("DB_PORT", "3306"), out var port) ? port : 3306,
            UserID = Get("DB_USER"),
            Password = Get("DB_PASSWORD"),
            Database = Get("DB_NAME", "nexuscore"),
            CharacterSet = "utf8mb4",
            SslMode = sslMode,
            ConnectionTimeout = 10,
            MaximumPoolSize = 50
        };
    }
}
