using Dapper;
using NexusCore.Api.Services;

namespace NexusCore.Api.Data;

/// <summary>Game ownership (the libraries table).</summary>
public class LibraryRepository(DbService db)
{
    public async Task<IReadOnlyList<OwnedGame>> GetOwnedGamesAsync(int userId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT g.game_id, g.name, g.slug, g.cover_url,
                   l.purchase_date, l.purchase_price, l.playtime_mins, l.last_played
            FROM libraries l
            JOIN games g ON g.game_id = l.game_id
            WHERE l.user_id = @userId
            ORDER BY l.purchase_date DESC
            """;
        await using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<OwnedGame>(new CommandDefinition(sql, new { userId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<bool> OwnsGameAsync(int userId, int gameId, CancellationToken ct = default)
    {
        await using var conn = db.CreateConnection();
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM libraries WHERE user_id = @userId AND game_id = @gameId)",
            new { userId, gameId }, cancellationToken: ct));
    }
}
