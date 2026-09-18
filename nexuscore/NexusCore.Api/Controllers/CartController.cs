using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexusCore.Api.Extensions;
using NexusCore.Api.Helpers;
using NexusCore.Api.Services;

namespace NexusCore.Api.Controllers;

[ApiController]
[Route("api/cart")]
[Authorize]
public class CartController(DbService db) : ControllerBase
{
    [HttpGet]
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

    [HttpPost("checkout")]
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

    [HttpPost("{gameId:int}")]
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

    [HttpDelete("{gameId:int}")]
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

    [HttpDelete]
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
