using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexusCore.Api.Extensions;
using NexusCore.Api.Helpers;
using NexusCore.Api.Services;

namespace NexusCore.Api.Controllers;

/// <summary>
/// Manages the game catalog: browsing/searching approved games, curated lists (carousel,
/// on-sale, featured, new releases), game submission and moderation, and user reviews.
/// Most read endpoints are anonymous; write endpoints require the developer or admin role.
/// </summary>
[ApiController]
[Route("api/games")]
public class GamesController(DbService db) : ControllerBase
{
    /// <summary>
    /// Searches and lists approved games with filtering, sorting, and pagination.
    /// </summary>
    /// <param name="query">
    /// Query parameters: <c>search</c> (text match on name/description/tags/developer),
    /// <c>genre</c> (exact genre match), <c>cloud</c> ("1" to filter cloud-enabled games),
    /// <c>free</c> ("1" to filter free games), <c>trial</c> ("1" to filter trial-enabled games),
    /// <c>upcoming</c> ("1" to filter unreleased games), <c>sort</c> ("price_asc", "price_desc",
    /// "az", "rating", or default release-date-descending), <c>page</c> (1-based, defaults to 1),
    /// <c>limit</c> (page size, defaults to 20).
    /// </param>
    /// <response code="200">Returns the matching games, total count, page, and limit.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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

    /// <summary>
    /// Returns the homepage carousel games. Falls back to featured games if no carousel
    /// games are configured.
    /// </summary>
    /// <response code="200">Returns up to 10 carousel games (or up to 5 featured games as a fallback).</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpGet("carousel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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

    /// <summary>
    /// Lists approved, paid games that currently have an active discount.
    /// </summary>
    /// <response code="200">Returns up to 20 discounted games, largest discount first.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpGet("on-sale")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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

    /// <summary>
    /// Lists games flagged as featured.
    /// </summary>
    /// <response code="200">Returns up to 5 featured, approved games.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpGet("featured")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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

    /// <summary>
    /// Lists the most recently released approved games.
    /// </summary>
    /// <response code="200">Returns up to 8 games, most recently released first.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpGet("new-releases")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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

    /// <summary>
    /// Replaces the entire homepage carousel selection and ordering. Admin only.
    /// </summary>
    /// <param name="body">The full ordered list of games to place in the carousel.</param>
    /// <response code="200">Carousel updated; returns the number of items set.</response>
    /// <response code="400">The <c>items</c> array was missing.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpPut("carousel/manage")]
    [Authorize(Roles = "admin")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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

    /// <summary>
    /// Lists games awaiting moderation review. Admin only.
    /// </summary>
    /// <response code="200">Returns pending games, oldest submission first.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpGet("pending")]
    [Authorize(Roles = "admin")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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

    /// <summary>
    /// Lists games submitted by the caller, with ownership/trial stats. Developer or admin role required.
    /// </summary>
    /// <response code="200">Returns the caller's submitted games with owner and trial counts.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpGet("my")]
    [Authorize(Roles = "developer,admin")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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

    /// <summary>
    /// Retrieves full details for a single game, including ratings, media, and reviews.
    /// </summary>
    /// <remarks>
    /// Accepts either a numeric game ID or a slug. Non-approved games are only visible to
    /// the game's own developer or an admin.
    /// </remarks>
    /// <param name="idOrSlug">Route parameter: the game's numeric ID or URL slug.</param>
    /// <response code="200">Returns the game's full detail, including reviews and media.</response>
    /// <response code="404">No matching game was found, or it is not approved and the caller may not view it.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpGet("{idOrSlug}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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

    /// <summary>
    /// Submits a new game for moderation review. Developer or admin role required.
    /// </summary>
    /// <remarks>The new game is created with status "pending" and must be approved (see <see cref="AdminReview"/>) before it appears publicly.</remarks>
    /// <param name="body">The game's metadata, pricing, and trial/cloud configuration.</param>
    /// <response code="201">Game submitted; returns its new ID and generated slug.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpPost]
    [Authorize(Roles = "developer,admin")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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

    /// <summary>
    /// Partially updates a game's fields. Admin only.
    /// </summary>
    /// <remarks>
    /// Only the fields present in the request body are updated; unrecognized fields are
    /// ignored. If <c>name</c> is included, the game's slug is regenerated from it.
    /// </remarks>
    /// <param name="id">Route parameter: the game to update.</param>
    /// <param name="body">
    /// A JSON object containing any subset of updatable game fields (name, short_desc,
    /// description, genre, tags, price, is_free, is_featured, is_carousel, carousel_order,
    /// discount_percent, discount_expires_at, cloud_enabled, requirements, trailer_url,
    /// cover_url, status, trial_enabled, trial_duration_mins, trial_level_limit,
    /// trial_discount_percent, download_size_gb).
    /// </param>
    /// <response code="200">Game updated (or no recognized fields were present, in which case no changes are made).</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpPut("{id:int}")]
    [Authorize(Roles = "admin")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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

    /// <summary>
    /// Permanently deletes a game and all related data (libraries, trials, cloud sessions,
    /// reviews, media). Admin only.
    /// </summary>
    /// <param name="id">Route parameter: the game to delete.</param>
    /// <response code="200">Game and related data deleted.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpDelete("{id:int}")]
    [Authorize(Roles = "admin")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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

    /// <summary>
    /// Approves or rejects a pending game submission. Admin only.
    /// </summary>
    /// <param name="id">Route parameter: the game being reviewed.</param>
    /// <param name="body">The moderation action ("approve" or any other value treated as a rejection) and, for rejections, a reason.</param>
    /// <response code="200">Game status updated to approved or rejected.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpPost("{id:int}/review")]
    [Authorize(Roles = "admin")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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

    /// <summary>
    /// Submits a user rating/review for a game. Requires the caller to either own the game
    /// or have completed a trial of it.
    /// </summary>
    /// <param name="id">Route parameter: the game being reviewed.</param>
    /// <param name="body">Rating, optional review text, and optional recommendation flag.</param>
    /// <response code="201">Review submitted.</response>
    /// <response code="403">The caller neither owns the game nor has completed/purchased a trial of it.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpPost("{id:int}/user-review")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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

    /// <summary>Query parameters accepted by <see cref="List"/>.</summary>
    public class GameQuery
    {
        /// <summary>Free-text search across name, short description, tags, and developer name.</summary>
        public string? Search { get; set; }
        /// <summary>Exact genre filter.</summary>
        public string? Genre { get; set; }
        /// <summary>"1" to only return cloud-enabled games.</summary>
        public string? Cloud { get; set; }
        /// <summary>"1" to only return free games.</summary>
        public string? Free { get; set; }
        /// <summary>"1" to only return trial-enabled games.</summary>
        public string? Trial { get; set; }
        /// <summary>"1" to only return games with a future release date.</summary>
        public string? Upcoming { get; set; }
        /// <summary>Sort order: "price_asc", "price_desc", "az", "rating", or default (newest release first).</summary>
        public string? Sort { get; set; }
        /// <summary>1-based page number.</summary>
        public int Page { get; set; }
        /// <summary>Page size.</summary>
        public int Limit { get; set; }
    }

    /// <summary>Request body for <see cref="ManageCarousel"/>.</summary>
    /// <param name="Items">Ordered list of games to place in the carousel.</param>
    public record CarouselManageRequest(List<CarouselItem>? Items);

    /// <summary>A single carousel slot.</summary>
    /// <param name="GameId">The game to place in this slot.</param>
    /// <param name="CarouselOrder">Explicit sort order; defaults to the item's position in the list if omitted.</param>
    public record CarouselItem(int GameId, int? CarouselOrder);

    /// <summary>Request body for <see cref="Submit"/>.</summary>
    public record GameSubmitRequest(string? Name, string? ShortDesc, string? Description, string? Genre, string? Tags,
        decimal? Price, bool? IsFree, string? Requirements, string? TrailerUrl, string? CoverUrl, bool? CloudEnabled,
        bool? TrialEnabled, int? TrialDurationMins, int? TrialLevelLimit, int? TrialDiscountPercent);

    /// <summary>Request body for <see cref="AdminReview"/>.</summary>
    /// <param name="Action">"approve" to approve the game; any other value rejects it.</param>
    /// <param name="Reason">Rejection reason, used when <paramref name="Action"/> is not "approve".</param>
    public record AdminReviewRequest(string Action, string? Reason);

    /// <summary>Request body for <see cref="UserReview"/>.</summary>
    /// <param name="Rating">Numeric rating given by the user.</param>
    /// <param name="ReviewText">Optional free-text review.</param>
    /// <param name="IsRecommended">Whether the user recommends the game (defaults to true).</param>
    public record UserReviewRequest(int Rating, string? ReviewText, bool? IsRecommended);
}