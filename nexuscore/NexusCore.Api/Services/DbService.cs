using MySqlConnector;

namespace NexusCore.Api.Services;

/// <summary>
/// Builds the MySqlConnector connection for the API. The API must connect with the
/// restricted account created by database/provision-users.sh (DML only), never root.
/// </summary>
public class DbService(IConfiguration config)
{
    public string ConnectionString { get; } = BuildConnectionString(config);

    public MySqlConnection CreateConnection() => new(ConnectionString);

    private static string BuildConnectionString(IConfiguration config)
    {
        var configured = config.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        string Get(string key, string fallback = "") =>
            Environment.GetEnvironmentVariable(key) ?? config[key] ?? fallback;

        var user = Get("DB_USER");
        if (string.IsNullOrWhiteSpace(user))
            throw new InvalidOperationException("DB_USER must be set (see nexuscore/database/README.md).");

        var host = Get("DB_HOST", "localhost");
        var isLocal = host is "localhost" or "127.0.0.1" or "::1";
        var sslMode = Enum.TryParse<MySqlSslMode>(Get("DB_SSL_MODE"), ignoreCase: true, out var parsed)
            ? parsed
            : (isLocal ? MySqlSslMode.Preferred : MySqlSslMode.Required);

        return new MySqlConnectionStringBuilder
        {
            Server = host,
            Port = uint.TryParse(Get("DB_PORT", "3306"), out var port) ? port : 3306,
            UserID = user,
            Password = Get("DB_PASSWORD", ""),
            Database = Get("DB_NAME", "nexuscore"),
            CharacterSet = "utf8mb4",
            SslMode = sslMode,
            ConnectionTimeout = 10,
            MaximumPoolSize = 50,
        }.ConnectionString;
    }
}
