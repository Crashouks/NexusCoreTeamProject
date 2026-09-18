using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexusCore.Api.Extensions;
using NexusCore.Api.Helpers;
using NexusCore.Api.Services;

namespace NexusCore.Api.Controllers;

[ApiController]
[Route("api/games")]
public class GamesController(DbService db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] GameQuery query)
    {
        try
        {
            var (where, parameters, order) = BuildGameQuery(query);
            var page = query.Page <= 0 ? 1 : query.Page;
            var limit = query.Limit <= 0 ? 20 : query.Limit;
            var offset = (page - 1) * limit;

            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var dynParams = new DynamicParameters(parameters);
            var total = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) AS total FROM games g WHERE {where}", dynParams);
            dynParams.Add("limit", limit);
            dynParams.Add("offset", offset);
            var games = (await conn.QueryAsync<dynamic>(
                $@"SELECT g.*, COALESCE(AVG(r.rating), 0) AS avg_rating, COUNT(r.review_id) AS review_count
                   FROM games g LEFT JOIN reviews r ON g.game_id=r.game_id
                   WHERE {where} GROUP BY g.game_id ORDER BY {order} LIMIT @limit OFFSET @offset",
                dynParams)).ToList();
            return Ok(new { games = PricingService.EnrichGames(games), total, page, limit });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpGet("carousel")]
    public async Task<IActionResult> Carousel()
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var sql = @"SELECT g.*, COALESCE(AVG(r.rating), 0) AS avg_rating
                        FROM games g LEFT JOIN reviews r ON g.game_id=r.game_id
                        WHERE g.is_carousel=TRUE AND g.status='approved'
                        GROUP BY g.game_id ORDER BY g.carousel_order ASC, g.release_date DESC LIMIT 10";
            var games = (await conn.QueryAsync<dynamic>(sql)).ToList();
            if (games.Count == 0)
            {
                games = (await conn.QueryAsync<dynamic>(
                    @"SELECT g.*, COALESCE(AVG(r.rating), 0) AS avg_rating
                      FROM games g LEFT JOIN reviews r ON g.game_id=r.game_id
                      WHERE g.is_featured=TRUE AND g.status='approved'
                      GROUP BY g.game_id ORDER BY g.release_date DESC LIMIT 5")).ToList();
            }
            return Ok(PricingService.EnrichGames(games));
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpGet("on-sale")]
    public async Task<IActionResult> OnSale()
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var games = (await conn.QueryAsync<dynamic>(
                @"SELECT g.*, COALESCE(AVG(r.rating), 0) AS avg_rating
                  FROM games g LEFT JOIN reviews r ON g.game_id=r.game_id
                  WHERE g.status='approved' AND g.is_free=FALSE
                    AND g.discount_percent IS NOT NULL AND g.discount_percent > 0
                    AND (g.discount_expires_at IS NULL OR g.discount_expires_at > NOW())
                  GROUP BY g.game_id ORDER BY g.discount_percent DESC, g.release_date DESC LIMIT 20")).ToList();
            return Ok(PricingService.EnrichGames(games));
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpGet("featured")]
    public async Task<IActionResult> Featured()
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var games = (await conn.QueryAsync<dynamic>(
                @"SELECT g.*, COALESCE(AVG(r.rating), 0) AS avg_rating
                  FROM games g LEFT JOIN reviews r ON g.game_id=r.game_id
                  WHERE g.is_featured=TRUE AND g.status='approved'
                  GROUP BY g.game_id LIMIT 5")).ToList();
            return Ok(PricingService.EnrichGames(games));
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpGet("new-releases")]
    public async Task<IActionResult> NewReleases()
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var games = (await conn.QueryAsync<dynamic>(
                "SELECT * FROM games WHERE status='approved' ORDER BY release_date DESC LIMIT 8")).ToList();
            return Ok(PricingService.EnrichGames(games));
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpPut("carousel/manage")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> ManageCarousel([FromBody] CarouselManageRequest body)
    {
        if (body.Items == null) return ApiResults.Error(400, "items array required", "INVALID_BODY");
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            await conn.ExecuteAsync("UPDATE games SET is_carousel=FALSE, carousel_order=0");
            for (var i = 0; i < body.Items.Count; i++)
            {
                var item = body.Items[i];
                await conn.ExecuteAsync(
                    "UPDATE games SET is_carousel=TRUE, carousel_order=@order WHERE game_id=@gid AND status='approved'",
                    new { order = item.CarouselOrder ?? i, gid = item.GameId });
            }
            return Ok(new { message = "Carousel updated", count = body.Items.Count });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpGet("pending")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Pending()
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            return Ok(await conn.QueryAsync("SELECT * FROM games WHERE status='pending' ORDER BY submitted_at ASC"));
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpGet("my")]
    [Authorize(Roles = "developer,admin")]
    public async Task<IActionResult> MyGames()
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var rows = await conn.QueryAsync(
                @"SELECT g.*,
                    (SELECT COUNT(*) FROM libraries l WHERE l.game_id=g.game_id) AS owners_count,
                    (SELECT COUNT(*) FROM trials t WHERE t.game_id=g.game_id) AS trial_starts,
                    (SELECT COUNT(*) FROM trials t WHERE t.game_id=g.game_id AND t.status='purchased') AS trial_purchases
                  FROM games g WHERE g.developer_id=@uid ORDER BY g.submitted_at DESC",
                new { uid = User.GetUserId() });
            return Ok(rows);
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpGet("{idOrSlug}")]
    public async Task<IActionResult> Detail(string idOrSlug)
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var isNum = int.TryParse(idOrSlug, out var gameId);
            List<dynamic> games;
            if (isNum)
                games = (await conn.QueryAsync<dynamic>("SELECT * FROM games WHERE game_id=@p", new { p = gameId })).ToList();
            else
                games = (await conn.QueryAsync<dynamic>("SELECT * FROM games WHERE slug=@p", new { p = idOrSlug })).ToList();
            if (games.Count == 0) return ApiResults.Error(404, "Game not found", "NOT_FOUND");
            var game = games[0];
            int? userId = null;
            string? role = null;
            if (User.Identity?.IsAuthenticated == true)
            {
                userId = User.GetUserId();
                role = User.GetRole();
            }
            if ((string)game.status != "approved" && (role != "admin" && userId != (int?)game.developer_id))
                return ApiResults.Error(404, "Game not found", "NOT_FOUND");

            var gid = (int)game.game_id;
            var ratings = await conn.QuerySingleAsync(
                "SELECT COALESCE(AVG(rating),0) AS avg_rating, COUNT(*) AS review_count FROM reviews WHERE game_id=@gid",
                new { gid });
            var media = await conn.QueryAsync("SELECT * FROM game_media WHERE game_id=@gid ORDER BY sort_order", new { gid });
            var reviews = await conn.QueryAsync(
                @"SELECT r.*, u.username, u.avatar_url FROM reviews r
                  JOIN users u ON r.user_id=u.user_id WHERE r.game_id=@gid ORDER BY r.review_date DESC", new { gid });

            var dict = PricingService.RowToDict(game);
            foreach (var kv in (IDictionary<string, object>)ratings)
                dict[kv.Key] = kv.Value is DBNull ? null : kv.Value;
            dict["media"] = media;
            dict["reviews"] = reviews;
            PricingService.EnrichGame(dict);
            return Ok(dict);
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpPost]
    [Authorize(Roles = "developer,admin")]
    public async Task<IActionResult> Submit([FromBody] GameSubmitRequest body)
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var slug = Slugify.FromName(body.Name ?? "");
            var username = await conn.ExecuteScalarAsync<string>(
                "SELECT username FROM users WHERE user_id=@id", new { id = User.GetUserId() });
            var id = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO games (name, slug, short_desc, description, genre, tags, developer_name, developer_id,
                  price, is_free, requirements, trailer_url, cover_url, cloud_enabled,
                  trial_enabled, trial_duration_mins, trial_level_limit, trial_discount_percent, status)
                  VALUES (@name, @slug, @short_desc, @description, @genre, @tags, @dev, @devId,
                  @price, @is_free, @req, @trailer, @cover, @cloud,
                  @trial_en, @trial_mins, @trial_limit, @trial_disc, 'pending');
                  SELECT LAST_INSERT_ID();",
                new
                {
                    name = body.Name, slug, short_desc = body.ShortDesc, description = body.Description,
                    genre = body.Genre, tags = body.Tags, dev = username, devId = User.GetUserId(),
                    price = body.Price ?? 0, is_free = body.IsFree ?? false, req = body.Requirements,
                    trailer = body.TrailerUrl, cover = body.CoverUrl, cloud = body.CloudEnabled ?? false,
                    trial_en = body.TrialEnabled ?? true, trial_mins = body.TrialDurationMins ?? 30,
                    trial_limit = body.TrialLevelLimit, trial_disc = body.TrialDiscountPercent ?? 10
                });
            return StatusCode(201, new { gameId = id, slug });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Update(int id, [FromBody] JsonElement body)
    {
        try
        {
            var fields = new[] { "name", "short_desc", "description", "genre", "tags", "price", "is_free", "is_featured",
                "is_carousel", "carousel_order", "discount_percent", "discount_expires_at",
                "cloud_enabled", "requirements", "trailer_url", "cover_url", "status",
                "trial_enabled", "trial_duration_mins", "trial_level_limit", "trial_discount_percent",
                "download_size_gb" };
            var updates = new List<string>();
            var parameters = new DynamicParameters();
            parameters.Add("id", id);
            foreach (var f in fields)
            {
                if (body.TryGetProperty(f, out var val))
                {
                    updates.Add($"{f}=@{f}");
                    parameters.Add(f, JsonToObject(val));
                }
            }
            if (body.TryGetProperty("name", out _))
            {
                var name = body.GetProperty("name").GetString() ?? "";
                updates.Add("slug=@slug");
                parameters.Add("slug", Slugify.FromName(name));
            }
            if (updates.Count == 0) return Ok(new { message = "Game updated" });
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            await conn.ExecuteAsync($"UPDATE games SET {string.Join(", ", updates)} WHERE game_id=@id", parameters);
            return Ok(new { message = "Game updated" });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Delete(int id)
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            await conn.ExecuteAsync("DELETE FROM libraries WHERE game_id=@id", new { id });
            await conn.ExecuteAsync("UPDATE trials SET status='expired' WHERE game_id=@id AND status='active'", new { id });
            await conn.ExecuteAsync("UPDATE cloud_sessions SET status='force_ended', ended_at=NOW() WHERE game_id=@id AND status='active'", new { id });
            await conn.ExecuteAsync("DELETE FROM reviews WHERE game_id=@id", new { id });
            await conn.ExecuteAsync("DELETE FROM game_media WHERE game_id=@id", new { id });
            await conn.ExecuteAsync("DELETE FROM games WHERE game_id=@id", new { id });
            return Ok(new { message = "Game deleted" });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpPost("{id:int}/review")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> AdminReview(int id, [FromBody] AdminReviewRequest body)
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            if (body.Action == "approve")
                await conn.ExecuteAsync("UPDATE games SET status='approved', reviewed_at=NOW() WHERE game_id=@id", new { id });
            else
                await conn.ExecuteAsync(
                    "UPDATE games SET status='rejected', rejection_reason=@reason, reviewed_at=NOW() WHERE game_id=@id",
                    new { reason = body.Reason, id });
            return Ok(new { message = $"Game {body.Action}d" });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpPost("{id:int}/user-review")]
    [Authorize]
    public async Task<IActionResult> UserReview(int id, [FromBody] UserReviewRequest body)
    {
        try
        {
            var userId = User.GetUserId();
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var owned = await conn.ExecuteScalarAsync<int?>(
                "SELECT 1 FROM libraries WHERE user_id=@uid AND game_id=@gid", new { uid = userId, gid = id });
            var trial = (await conn.QueryAsync(
                "SELECT status FROM trials WHERE user_id=@uid AND game_id=@gid AND status IN ('completed','purchased')",
                new { uid = userId, gid = id })).ToList();
            if (owned == null && trial.Count == 0)
                return ApiResults.Error(403, "Must own game or complete trial", "REVIEW_NOT_ALLOWED");
            await conn.ExecuteAsync(
                "INSERT INTO reviews (user_id, game_id, rating, review_text, is_recommended) VALUES (@uid, @gid, @rating, @text, @rec)",
                new { uid = userId, gid = id, rating = body.Rating, text = body.ReviewText, rec = body.IsRecommended ?? true });
            return StatusCode(201, new { message = "Review submitted" });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    private static (string where, DynamicParameters parameters, string order) BuildGameQuery(GameQuery query)
    {
        var where = new List<string> { "g.status='approved'" };
        var parameters = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            where.Add("(g.name LIKE @search OR g.short_desc LIKE @search OR g.tags LIKE @search OR g.developer_name LIKE @search)");
            parameters.Add("search", $"%{query.Search}%");
        }
        if (!string.IsNullOrWhiteSpace(query.Genre))
        {
            where.Add("g.genre=@genre");
            parameters.Add("genre", query.Genre);
        }
        if (query.Cloud == "1") where.Add("g.cloud_enabled=TRUE");
        if (query.Free == "1") where.Add("g.is_free=TRUE");
        if (query.Trial == "1") where.Add("(g.trial_enabled IS NULL OR g.trial_enabled=TRUE)");
        if (query.Upcoming == "1") where.Add("g.release_date > NOW()");
        var order = query.Sort switch
        {
            "price_asc" => "g.price ASC",
            "price_desc" => "g.price DESC",
            "az" => "g.name ASC",
            "rating" => "avg_rating DESC",
            _ => "g.release_date DESC"
        };
        return (string.Join(" AND ", where), parameters, order);
    }

    private static object? JsonToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => el.GetRawText()
    };

    public class GameQuery
    {
        public string? Search { get; set; }
        public string? Genre { get; set; }
        public string? Cloud { get; set; }
        public string? Free { get; set; }
        public string? Trial { get; set; }
        public string? Upcoming { get; set; }
        public string? Sort { get; set; }
        public int Page { get; set; }
        public int Limit { get; set; }
    }

    public record CarouselManageRequest(List<CarouselItem>? Items);
    public record CarouselItem(int GameId, int? CarouselOrder);
    public record GameSubmitRequest(string? Name, string? ShortDesc, string? Description, string? Genre, string? Tags,
        decimal? Price, bool? IsFree, string? Requirements, string? TrailerUrl, string? CoverUrl, bool? CloudEnabled,
        bool? TrialEnabled, int? TrialDurationMins, int? TrialLevelLimit, int? TrialDiscountPercent);
    public record AdminReviewRequest(string Action, string? Reason);
    public record UserReviewRequest(int Rating, string? ReviewText, bool? IsRecommended);
}
