using MySqlConnector;

namespace NexusCore.Api.Services;

/// <summary>
/// Builds the MySqlConnector connection for the API. The API must connect with the
/// restricted account created by database/provision-users.sh (DML only), never root.
/// </summary>
public class DbService
{
    /// <summary>
    /// Preferred: ConnectionStrings:DefaultConnection (env var ConnectionStrings__DefaultConnection).
    /// Fallback for local development: the legacy DB_HOST / DB_USER / DB_PASSWORD / DB_NAME settings.
    /// </summary>
    public string ConnectionString =>
        config.GetConnectionString("DefaultConnection") is { Length: > 0 } cs
            ? cs
            : $"Server={Get("DB_HOST", "localhost")};User={Get("DB_USER", "root")};Password={Get("DB_PASSWORD", "")};Database={Get("DB_NAME", "nexuscore")};";

    public DbService(IConfiguration config)
    {
        string Get(string key, string fallback = "") =>
            Environment.GetEnvironmentVariable(key) ?? config[key] ?? fallback;

        var user = Get("DB_USER");
        var password = Get("DB_PASSWORD");
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password))
            throw new InvalidOperationException(
                "DB_USER and DB_PASSWORD must be set (see nexuscore/database/README.md).");
        if (user.Equals("root", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The API must not connect as MySQL root. Use the least-privilege account from database/provision-users.sh.");

        var host = Get("DB_HOST", "localhost");
        var isLocal = host is "localhost" or "127.0.0.1" or "::1";
        var sslMode = Enum.TryParse<MySqlSslMode>(Get("DB_SSL_MODE"), ignoreCase: true, out var parsed)
            ? parsed
            : (isLocal ? MySqlSslMode.Preferred : MySqlSslMode.Required);

        ConnectionString = new MySqlConnectionStringBuilder
        {
            Server = host,
            Port = uint.TryParse(Get("DB_PORT", "3306"), out var port) ? port : 3306,
            UserID = user,
            Password = password,
            Database = Get("DB_NAME", "nexuscore"),
            CharacterSet = "utf8mb4",
            SslMode = sslMode,
            ConnectionTimeout = 10,
            MaximumPoolSize = 50,
        }.ConnectionString;
    }

    public MySqlConnection CreateConnection() => new(ConnectionString);
}
