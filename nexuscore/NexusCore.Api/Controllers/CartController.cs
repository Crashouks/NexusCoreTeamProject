using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexusCore.Api.Extensions;
using NexusCore.Api.Helpers;
using NexusCore.Api.Services;

namespace NexusCore.Api.Controllers;

/// <summary>
/// Manages the current user's shopping cart: listing items, adding/removing games, and
/// checking out to purchase everything in the cart at once. All endpoints require authentication.
/// </summary>
[ApiController]
[Route("api/cart")]
[Authorize]
public class CartController(DbService db) : ControllerBase
{
    /// <summary>
    /// Lists the games currently in the caller's cart, with pricing applied.
    /// </summary>
    /// <response code="200">Returns the cart items, running total, and item count.</response>
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
            var rows = (await conn.QueryAsync<dynamic>(
                @"SELECT g.*, c.added_at FROM cart_items c
                  JOIN games g ON c.game_id = g.game_id
                  WHERE c.user_id = @uid ORDER BY c.added_at DESC",
                new { uid = User.GetUserId() })).ToList();
            var items = PricingService.EnrichGames(rows);
            var total = items.Sum(g => DbValue.IsTrue(g["is_free"]) ? 0m : PricingService.GetPurchasePrice(g));
            return Ok(new { items, total, count = items.Count });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    /// <summary>
    /// Purchases every item currently in the caller's cart in a single transaction.
    /// </summary>
    /// <remarks>
    /// Deducts the total cost from the user's balance, adds each game to their library,
    /// marks any matching trials as purchased, and empties the cart. Free games are added
    /// without a balance check.
    /// </remarks>
    /// <response code="200">Checkout succeeded; returns items purchased, total charged, and new balance.</response>
    /// <response code="400">The cart is empty.</response>
    /// <response code="402">The user's balance is insufficient to cover the total cost.</response>
    /// <response code="500">Unexpected server error; the transaction is rolled back.</response>
    [HttpPost("checkout")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status402PaymentRequired)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Checkout()
    {
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            var userId = User.GetUserId();
            var items = (await conn.QueryAsync<dynamic>(
                "SELECT g.* FROM cart_items c JOIN games g ON c.game_id = g.game_id WHERE c.user_id = @uid",
                new { uid = userId }, tx)).ToList();
            if (items.Count == 0)
            {
                await tx.RollbackAsync();
                return ApiResults.Error(400, "Cart is empty", "EMPTY_CART");
            }
            decimal totalCost = 0;
            foreach (var g in items)
            {
                if (!DbValue.IsTrue(g.is_free) && Convert.ToDecimal(g.price) > 0)
                    totalCost += PricingService.GetPurchasePrice(g);
            }
            var balance = await conn.ExecuteScalarAsync<decimal>(
                "SELECT balance FROM users WHERE user_id=@uid FOR UPDATE", new { uid = userId }, tx);
            if (balance < totalCost)
            {
                await tx.RollbackAsync();
                return new ObjectResult(new { error = "Insufficient balance", code = "INSUFFICIENT_BALANCE", total = totalCost }) { StatusCode = 402 };
            }
            if (totalCost > 0)
                await conn.ExecuteAsync("UPDATE users SET balance=balance-@cost WHERE user_id=@uid", new { cost = totalCost, uid = userId }, tx);
            foreach (var g in items)
            {
                var gid = (int)g.game_id;
                var exists = await conn.ExecuteScalarAsync<int?>(
                    "SELECT 1 FROM libraries WHERE user_id=@uid AND game_id=@gid", new { uid = userId, gid }, tx);
                if (exists == null)
                {
                    var price = DbValue.IsTrue(g.is_free) ? 0m : PricingService.GetPurchasePrice(g);
                    await conn.ExecuteAsync(
                        "INSERT INTO libraries (user_id, game_id, purchase_price) VALUES (@uid, @gid, @price)",
                        new { uid = userId, gid, price }, tx);
                    await conn.ExecuteAsync(
                        "UPDATE trials SET status='purchased' WHERE user_id=@uid AND game_id=@gid AND status IN ('active','completed')",
                        new { uid = userId, gid }, tx);
                }
            }
            await conn.ExecuteAsync("DELETE FROM cart_items WHERE user_id=@uid", new { uid = userId }, tx);
            await tx.CommitAsync();
            var newBalance = await conn.ExecuteScalarAsync<decimal>("SELECT balance FROM users WHERE user_id=@uid", new { uid = userId });
            return Ok(new { message = "Checkout complete", purchased = items.Count, total = totalCost, balance = newBalance });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return ApiResults.Error(500, ex.Message, "SERVER_ERROR");
        }
    }

    /// <summary>
    /// Adds a single approved game to the caller's cart.
    /// </summary>
    /// <param name="gameId">Route parameter: the ID of the game to add.</param>
    /// <response code="201">Game added to the cart.</response>
    /// <response code="404">No approved game exists with the given ID.</response>
    /// <response code="409">The game is already owned, or already in the cart.</response>
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
            var userId = User.GetUserId();
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var games = (await conn.QueryAsync("SELECT * FROM games WHERE game_id=@id AND status='approved'", new { id = gameId })).ToList();
            if (games.Count == 0) return ApiResults.Error(404, "Game not found", "NOT_FOUND");
            var owned = await conn.ExecuteScalarAsync<int?>(
                "SELECT 1 FROM libraries WHERE user_id=@uid AND game_id=@gid", new { uid = userId, gid = gameId });
            if (owned != null) return ApiResults.Error(409, "Already owned", "ALREADY_OWNED");
            await conn.ExecuteAsync("INSERT INTO cart_items (user_id, game_id) VALUES (@uid, @gid)", new { uid = userId, gid = gameId });
            return StatusCode(201, new { message = "Added to cart" });
        }
        catch (MySqlConnector.MySqlException ex) when (ex.Number == 1062)
        {
            return ApiResults.Error(409, "Already in cart", "DUPLICATE");
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    /// <summary>
    /// Removes a single game from the caller's cart.
    /// </summary>
    /// <remarks>Succeeds even if the game was not in the cart.</remarks>
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
            await conn.ExecuteAsync("DELETE FROM cart_items WHERE user_id=@uid AND game_id=@gid",
                new { uid = User.GetUserId(), gid = gameId });
            return Ok(new { message = "Removed from cart" });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    /// <summary>
    /// Removes all items from the caller's cart.
    /// </summary>
    /// <response code="200">Cart cleared.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Clear()
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            await conn.ExecuteAsync("DELETE FROM cart_items WHERE user_id=@uid", new { uid = User.GetUserId() });
            return Ok(new { message = "Cart cleared" });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }
}