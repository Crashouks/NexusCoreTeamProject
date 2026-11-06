using Dapper;

namespace NexusCore.Api.Data;

public static class DapperSetup
{
    /// <summary>Map snake_case columns (game_id) onto PascalCase properties (GameId).</summary>
    public static void Configure() => DefaultTypeMap.MatchNamesWithUnderscores = true;
}
