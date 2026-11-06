using Dapper;
using NexusCore.Api.Services;

namespace NexusCore.Api.Data;

public class GameRepository(DbService db)
{
    public async Task<IReadOnlyList<GameSummary>> ListApprovedAsync(int limit, int offset, CancellationToken ct = default)
    {
        const string sql = """
            SELECT game_id, name, slug, short_desc, genre, cover_url, price, is_free, cloud_enabled, discount_percent
            FROM games
            WHERE status = 'approved'
            ORDER BY release_date DESC
            LIMIT @limit OFFSET @offset
            """;
        await using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<GameSummary>(new CommandDefinition(sql, new { limit, offset }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<GameSummary?> FindBySlugAsync(string slug, CancellationToken ct = default)
    {
        const string sql = """
            SELECT game_id, name, slug, short_desc, genre, cover_url, price, is_free, cloud_enabled, discount_percent
            FROM games WHERE slug = @slug AND status = 'approved'
            """;
        await using var conn = db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<GameSummary>(new CommandDefinition(sql, new { slug }, cancellationToken: ct));
    }

    public async Task<int> CountApprovedAsync(CancellationToken ct = default)
    {
        await using var conn = db.CreateConnection();
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM games WHERE status = 'approved'", cancellationToken: ct));
    }
}
