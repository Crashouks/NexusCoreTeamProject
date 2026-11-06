using Dapper;
using NexusCore.Api.Services;

namespace NexusCore.Api.Data;

public class UserRepository(DbService db)
{
    public async Task<UserProfile?> GetProfileAsync(int userId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT user_id, username, avatar_url, bio, country, reg_date
            FROM users WHERE user_id = @userId
            """;
        await using var conn = db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<UserProfile>(new CommandDefinition(sql, new { userId }, cancellationToken: ct));
    }
}
