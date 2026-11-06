using Dapper;

namespace NexusCore.Api.Services;

public class MigrationService(IConfiguration config, DbService db, ChatService chat, ForumService forum, NotificationService notifications, CloudServerService cloudServers)
{
    private static readonly string[] Migrations =
    [
        "ALTER TABLE games ADD COLUMN trial_enabled BOOLEAN DEFAULT TRUE",
        "ALTER TABLE games ADD COLUMN trial_duration_mins INT DEFAULT 30",
        "ALTER TABLE games ADD COLUMN trial_level_limit INT DEFAULT NULL",
        "ALTER TABLE games ADD COLUMN trial_discount_percent INT DEFAULT 10",
        "ALTER TABLE games ADD COLUMN is_carousel BOOLEAN DEFAULT FALSE",
        "ALTER TABLE games ADD COLUMN carousel_order INT DEFAULT 0",
        "ALTER TABLE games ADD COLUMN discount_percent INT DEFAULT NULL",
        "ALTER TABLE games ADD COLUMN discount_expires_at DATETIME DEFAULT NULL",
        "ALTER TABLE games ADD COLUMN download_size_gb DECIMAL(8,2) DEFAULT 25.00",
        "ALTER TABLE libraries ADD COLUMN download_status ENUM('none','downloading','installed') DEFAULT 'none'",
        "ALTER TABLE libraries ADD COLUMN download_progress INT DEFAULT 0",
    ];

    /// <summary>Newest SQL migration (database/migrations) this build of the API expects.</summary>
    private const string RequiredSchemaVersion = "V003";

    public async Task RunAsync()
    {
        // Default: the API's restricted DB account cannot run DDL. The schema is owned by
        // database/migrate.sh; here we only verify it is up to date.
        // Set DB_AUTO_MIGRATE=true (with a privileged DB_USER) only for legacy local setups.
        var auto = (Environment.GetEnvironmentVariable("DB_AUTO_MIGRATE") ?? config["DB_AUTO_MIGRATE"] ?? "false")
            .Equals("true", StringComparison.OrdinalIgnoreCase);
        if (!auto)
        {
            await VerifySchemaAsync();
            return;
        }

        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        foreach (var sql in Migrations)
        {
            try { await conn.ExecuteAsync(sql); }
            catch (MySqlConnector.MySqlException ex) when (ex.Number == 1060) { /* duplicate column */ }
        }
        await conn.ExecuteAsync("UPDATE games SET trial_enabled=TRUE WHERE trial_enabled IS NULL");
        var seeds = new[] { ("neon-drift", 0), ("void-walker", 1), ("starfall-arena", 2), ("cyber-heist", 3) };
        foreach (var (slug, order) in seeds)
        {
            await conn.ExecuteAsync(
                "UPDATE games SET is_carousel=TRUE, carousel_order=@order WHERE slug=@slug AND is_carousel=FALSE",
                new { order, slug });
        }

        await conn.ExecuteAsync("UPDATE games SET trailer_url=NULL WHERE trailer_url LIKE '%youtube%' OR trailer_url LIKE '%dQw4w9WgXcQ%'");
        await conn.ExecuteAsync(@"
            UPDATE games SET download_size_gb=CASE slug
              WHEN 'neon-drift' THEN 45 WHEN 'void-walker' THEN 100 WHEN 'pixel-siege' THEN 10
              WHEN 'starfall-arena' THEN 25 WHEN 'cyber-heist' THEN 80 WHEN 'mystic-realms' THEN 120
              WHEN 'turbo-kart' THEN 15 WHEN 'shadow-protocol' THEN 55 ELSE COALESCE(download_size_gb, 25)
            END WHERE download_size_gb IS NULL OR download_size_gb=25");
        await chat.EnsureChatSchemaAsync();
        await forum.EnsureForumSchemaAsync();
        await notifications.EnsureSchemaAsync();
        await cloudServers.EnsureSchemaAsync(conn);
    }

    private async Task VerifySchemaAsync()
    {
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        string? current;
        try
        {
            current = await conn.ExecuteScalarAsync<string?>(
                "SELECT version FROM schema_migrations ORDER BY version DESC LIMIT 1");
        }
        catch (MySqlConnector.MySqlException ex) when (ex.Number == 1146)
        {
            throw new InvalidOperationException(
                "Database schema is missing. Run nexuscore/database/migrate.sh first.");
        }
        if (string.CompareOrdinal(current ?? "", RequiredSchemaVersion) < 0)
            throw new InvalidOperationException(
                $"Database schema is at {current ?? "none"}, API needs {RequiredSchemaVersion}. Run nexuscore/database/migrate.sh.");
    }
}
