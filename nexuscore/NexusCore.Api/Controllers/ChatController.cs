using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexusCore.Api.Extensions;
using NexusCore.Api.Helpers;
using NexusCore.Api.Services;

namespace NexusCore.Api.Controllers;

/// <summary>
/// Manages chat rooms: the caller's chat list, the cloud-queue lobby, joining/leaving chats,
/// and reading or posting messages. All endpoints require authentication.
/// </summary>
[ApiController]
[Route("api/chat")]
[Authorize]
public class ChatController(ChatService chat) : ControllerBase
{
    /// <summary>
    /// Lists every chat the caller is currently a member of.
    /// </summary>
    /// <response code="200">Returns the caller's chats.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> List()
    {
        try
        {
            var chats = await chat.GetUserChatsAsync(User.GetUserId());
            return Ok(chats);
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    /// <summary>
    /// Joins the caller to the global cloud-queue lobby chat, and to their game-specific
    /// queue chat if they are currently queued for a game.
    /// </summary>
    /// <response code="200">Returns the global lobby chat ID, the game-specific chat ID (if any), and the active chat ID.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpGet("queue-lobby")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> QueueLobby()
    {
        try
        {
            var userId = User.GetUserId();
            var gameChatId = await chat.GetQueueChatIdForUserAsync(userId);
            var chatId = gameChatId ?? ChatService.GlobalQueueLobbyChatId;

            await chat.JoinChatAsync(userId, ChatService.GlobalQueueLobbyChatId);

            if (gameChatId.HasValue)
                await chat.JoinChatAsync(userId, gameChatId.Value);

            return Ok(new
            {
                global_lobby_id = ChatService.GlobalQueueLobbyChatId,
                game_chat_id = gameChatId,
                active_chat_id = chatId
            });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    /// <summary>
    /// Retrieves a page of messages for a chat the caller can access.
    /// </summary>
    /// <remarks>
    /// Automatically joins the caller to the chat first if it is the global lobby, or if the
    /// caller is currently in any cloud queue.
    /// </remarks>
    /// <param name="chatId">Route parameter: the chat to read messages from.</param>
    /// <param name="limit">Query parameter: maximum number of messages to return. Capped at 200. Defaults to 100.</param>
    /// <param name="offset">Query parameter: number of messages to skip, for pagination. Defaults to 0.</param>
    /// <response code="200">Returns the requested page of messages.</response>
    /// <response code="403">The caller does not have access to this chat.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpGet("{chatId:int}/messages")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Messages(int chatId, [FromQuery] int limit = 100, [FromQuery] int offset = 0)
    {
        try
        {
            var userId = User.GetUserId();
            if (!await chat.CanAccessChatAsync(userId, chatId))
                return ApiResults.Error(403, "Access denied", "FORBIDDEN");

            if (ChatService.IsGlobalLobby(chatId) || await chat.IsInAnyQueueAsync(userId))
                await chat.JoinChatAsync(userId, chatId);

            var messages = await chat.GetMessagesAsync(chatId, Math.Min(limit, 200), offset);
            return Ok(messages);
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    /// <summary>
    /// Joins the caller to a chat.
    /// </summary>
    /// <remarks>
    /// Any chat other than the global lobby requires the caller to be in a cloud queue first.
    /// </remarks>
    /// <param name="chatId">Route parameter: the chat to join.</param>
    /// <response code="200">Joined the chat.</response>
    /// <response code="403">The caller must join the cloud queue before joining this chat.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpPost("{chatId:int}/join")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Join(int chatId)
    {
        try
        {
            var userId = User.GetUserId();
            if (chatId != ChatService.GlobalQueueLobbyChatId && !await chat.IsInAnyQueueAsync(userId))
                return ApiResults.Error(403, "Join the cloud queue first", "QUEUE_REQUIRED");

            await chat.JoinChatAsync(userId, chatId);
            return Ok(new { message = "Joined chat", chat_id = chatId });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    /// <summary>
    /// Removes the caller from a chat.
    /// </summary>
    /// <param name="chatId">Route parameter: the chat to leave.</param>
    /// <response code="200">Left the chat.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpPost("{chatId:int}/leave")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Leave(int chatId)
    {
        try
        {
            await chat.LeaveChatAsync(User.GetUserId(), chatId);
            return Ok(new { message = "Left chat" });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    /// <summary>
    /// Posts a new message to a chat the caller is a member of.
    /// </summary>
    /// <param name="chatId">Route parameter: the chat to post to.</param>
    /// <param name="body">The message text.</param>
    /// <response code="201">Message saved; returns the saved message.</response>
    /// <response code="400">The message text failed validation (e.g. empty).</response>
    /// <response code="403">The caller is not a member of this chat.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpPost("{chatId:int}/messages")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> PostMessage(int chatId, [FromBody] PostMessageRequest body)
    {
        try
        {
            if (!await chat.IsMemberAsync(User.GetUserId(), chatId))
                return ApiResults.Error(403, "Access denied", "FORBIDDEN");

            var saved = await chat.SaveMessageAsync(chatId, User.GetUserId(), body.Text ?? "");
            return StatusCode(201, saved);
        }
        catch (ArgumentException ex)
        {
            return ApiResults.Error(400, ex.Message, "VALIDATION_ERROR");
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    /// <summary>Request body for <see cref="PostMessage"/>.</summary>
    /// <param name="Text">The message text to post.</param>
    public record PostMessageRequest(string? Text);
}