using Dapper;
using NexusCore.Api.Services;

namespace NexusCore.Api.Data;

/// <summary>Historical cloud-streaming sessions (the cloud_sessions table).</summary>
public class StreamingSessionRepository(DbService db)
{
    public async Task<IReadOnlyList<StreamingSessionRecord>> GetHistoryAsync(
        int userId, int limit = 50, CancellationToken ct = default)
    {
        const string sql = """
            SELECT s.session_id, s.game_id, g.name AS game_name, s.plan, s.started_at,
                   s.ended_at, s.duration_mins, s.status, s.stream_quality
            FROM cloud_sessions s
            JOIN games g ON g.game_id = s.game_id
            WHERE s.user_id = @userId
            ORDER BY s.started_at DESC
            LIMIT @limit
            """;
        await using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<StreamingSessionRecord>(
            new CommandDefinition(sql, new { userId, limit = Math.Clamp(limit, 1, 200) }, cancellationToken: ct));
        return rows.AsList();
    }
}
