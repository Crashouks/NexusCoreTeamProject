using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexusCore.Api.Extensions;
using NexusCore.Api.Helpers;
using NexusCore.Api.Services;

namespace NexusCore.Api.Controllers;

/// <summary>
/// Manages the caller's game wishlist. All endpoints require authentication.
/// </summary>
[ApiController]
[Route("api/wishlist")]
[Authorize]
public class WishlistController(DbService db, NotificationService notifications) : ControllerBase
{
    /// <summary>
    /// Lists the full games in the caller's wishlist, most recently added first.
    /// </summary>
    /// <response code="200">Returns the wishlisted games.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> List()
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var rows = await conn.QueryAsync(
                @"SELECT g.*, w.added_at FROM wishlist w
                  JOIN games g ON w.game_id = g.game_id
                  WHERE w.user_id = @uid ORDER BY w.added_at DESC",
                new { uid = User.GetUserId() });
            return Ok(rows);
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    /// <summary>
    /// Lists just the game IDs in the caller's wishlist (a lightweight alternative to <see cref="List"/>).
    /// </summary>
    /// <response code="200">Returns an array of wishlisted game IDs.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpGet("ids")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Ids()
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var rows = await conn.QueryAsync<int>("SELECT game_id FROM wishlist WHERE user_id=@uid", new { uid = User.GetUserId() });
            return Ok(rows);
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    /// <summary>
    /// Adds an approved game to the caller's wishlist and sends the caller a confirmation notification.
    /// </summary>
    /// <param name="gameId">Route parameter: the ID of the game to add.</param>
    /// <response code="201">Game added to the wishlist.</response>
    /// <response code="404">No approved game exists with the given ID.</response>
    /// <response code="409">The game is already in the wishlist.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpPost("{gameId:int}")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Add(int gameId)
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var games = (await conn.QueryAsync("SELECT * FROM games WHERE game_id=@id AND status='approved'", new { id = gameId })).ToList();
            if (games.Count == 0) return ApiResults.Error(404, "Game not found", "NOT_FOUND");
            var uid = User.GetUserId();
            await conn.ExecuteAsync("INSERT INTO wishlist (user_id, game_id) VALUES (@uid, @gid)", new { uid, gid = gameId });
            var game = games[0];
            var name = (string?)game.name ?? "a game";
            var slug = (string?)game.slug;
            await notifications.CreateAsync(uid, "wishlist",
                $"Added {name} to your wishlist",
                slug != null ? $"/games/{slug}" : "/wishlist",
                refGameId: gameId);
            return StatusCode(201, new { message = "Added to wishlist" });
        }
        catch (MySqlConnector.MySqlException ex) when (ex.Number == 1062)
        {
            return ApiResults.Error(409, "Already in wishlist", "DUPLICATE");
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    /// <summary>
    /// Removes a game from the caller's wishlist.
    /// </summary>
    /// <remarks>Succeeds even if the game was not in the wishlist.</remarks>
    /// <param name="gameId">Route parameter: the ID of the game to remove.</param>
    /// <response code="200">Game removed (or was already absent).</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpDelete("{gameId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Remove(int gameId)
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            await conn.ExecuteAsync("DELETE FROM wishlist WHERE user_id=@uid AND game_id=@gid", new { uid = User.GetUserId(), gid = gameId });
            return Ok(new { message = "Removed from wishlist" });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }
}