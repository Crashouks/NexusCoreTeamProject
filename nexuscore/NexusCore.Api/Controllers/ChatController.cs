using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexusCore.Api.Extensions;
using NexusCore.Api.Helpers;
using NexusCore.Api.Services;

namespace NexusCore.Api.Controllers;

[ApiController]
[Route("api/chat")]
[Authorize]
public class ChatController(ChatService chat) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List()
    {
        try
        {
            var chats = await chat.GetUserChatsAsync(User.GetUserId());
            return Ok(chats);
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpGet("queue-lobby")]
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

    [HttpGet("{chatId:int}/messages")]
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

    [HttpPost("{chatId:int}/join")]
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

    [HttpPost("{chatId:int}/leave")]
    public async Task<IActionResult> Leave(int chatId)
    {
        try
        {
            await chat.LeaveChatAsync(User.GetUserId(), chatId);
            return Ok(new { message = "Left chat" });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpPost("{chatId:int}/messages")]
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

    public record PostMessageRequest(string? Text);
}
