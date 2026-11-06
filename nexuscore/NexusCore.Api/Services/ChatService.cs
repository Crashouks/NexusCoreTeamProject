using Dapper;
using NexusCore.Api.Models;

namespace NexusCore.Api.Services;

public class ChatService(DbService db)
{
    public const int GlobalQueueLobbyChatId = 1;

    public static bool IsGlobalLobby(int chatId) => chatId == GlobalQueueLobbyChatId;

    public async Task EnsureChatSchemaAsync()
    {
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS chats (
              chat_id INT PRIMARY KEY AUTO_INCREMENT,
              name VARCHAR(100) NOT NULL,
              created_at DATETIME NOT NULL DEFAULT NOW(),
              description VARCHAR(255) NULL
            )");
        try
        {
            await conn.ExecuteAsync("ALTER TABLE chats ADD COLUMN game_id INT NULL");
        }
        catch (MySqlConnector.MySqlException ex) when (ex.Number == 1060) { }
        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS users_chat (
              user_chat_id INT PRIMARY KEY AUTO_INCREMENT,
              user_id INT NOT NULL,
              chat_id INT NOT NULL,
              joined_at DATETIME NOT NULL DEFAULT NOW(),
              is_admin BOOLEAN NOT NULL DEFAULT FALSE,
              UNIQUE KEY uq_user_chat (user_id, chat_id),
              FOREIGN KEY (user_id) REFERENCES users(user_id) ON DELETE CASCADE,
              FOREIGN KEY (chat_id) REFERENCES chats(chat_id) ON DELETE CASCADE
            )");
        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS chat_messages (
              message_id INT PRIMARY KEY AUTO_INCREMENT,
              chat_id INT NOT NULL,
              user_id INT NOT NULL,
              text TEXT NOT NULL,
              sent_at DATETIME NOT NULL DEFAULT NOW(),
              is_read BOOLEAN NOT NULL DEFAULT FALSE,
              FOREIGN KEY (chat_id) REFERENCES chats(chat_id) ON DELETE CASCADE,
              FOREIGN KEY (user_id) REFERENCES users(user_id) ON DELETE CASCADE,
              INDEX idx_chat_messages_chat_id (chat_id)
            )");

        var lobbyExists = await conn.ExecuteScalarAsync<int?>(
            "SELECT chat_id FROM chats WHERE chat_id=@id", new { id = GlobalQueueLobbyChatId });
        if (lobbyExists == null)
        {
            await conn.ExecuteAsync(
                @"INSERT INTO chats (chat_id, name, description) VALUES (@id, @name, @desc)",
                new
                {
                    id = GlobalQueueLobbyChatId,
                    name = "GeForce NOW Queue Lobby",
                    desc = "Chat with other players waiting in the cloud queue"
                });
        }
    }

    public async Task<int> GetOrCreateGameQueueChatAsync(int gameId)
    {
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        var existing = await conn.ExecuteScalarAsync<int?>(
            "SELECT chat_id FROM chats WHERE game_id=@gid", new { gid = gameId });
        if (existing != null) return existing.Value;

        var gameName = await conn.ExecuteScalarAsync<string>(
            "SELECT name FROM games WHERE game_id=@gid", new { gid = gameId }) ?? $"Game {gameId}";
        var chatId = await conn.ExecuteScalarAsync<int>(
            @"INSERT INTO chats (name, description, game_id) VALUES (@name, @desc, @gid);
              SELECT LAST_INSERT_ID();",
            new { name = $"Queue: {gameName}", desc = "Waiting room chat", gid = gameId });
        return chatId;
    }

    public async Task JoinChatAsync(int userId, int chatId, bool isAdmin = false)
    {
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            @"INSERT INTO users_chat (user_id, chat_id, is_admin) VALUES (@uid, @cid, @admin)
              ON DUPLICATE KEY UPDATE joined_at=NOW()",
            new { uid = userId, cid = chatId, admin = isAdmin });
    }

    public async Task LeaveChatAsync(int userId, int chatId)
    {
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "DELETE FROM users_chat WHERE user_id=@uid AND chat_id=@cid",
            new { uid = userId, cid = chatId });
    }

    public async Task<bool> IsMemberAsync(int userId, int chatId)
    {
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        var member = await conn.ExecuteScalarAsync<int?>(
            "SELECT 1 FROM users_chat WHERE user_id=@uid AND chat_id=@cid",
            new { uid = userId, cid = chatId });
        if (member != null) return true;

        // Allow queue chat if user is actively in cloud_queue for this game's chat
        var inQueue = await conn.ExecuteScalarAsync<int?>(@"
            SELECT 1 FROM cloud_queue cq
            JOIN chats c ON c.game_id = cq.game_id
            WHERE cq.user_id=@uid AND c.chat_id=@cid AND cq.status IN ('waiting','ready')",
            new { uid = userId, cid = chatId });
        return inQueue != null;
    }

    public async Task<bool> IsInAnyQueueAsync(int userId)
    {
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM cloud_queue WHERE user_id=@uid AND status IN ('waiting','ready')",
            new { uid = userId }) > 0;
    }

    public async Task<IEnumerable<dynamic>> GetUserChatsAsync(int userId)
    {
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        return await conn.QueryAsync(@"
            SELECT c.*, uc.joined_at, uc.is_admin,
              (SELECT COUNT(*) FROM users_chat uc2 WHERE uc2.chat_id=c.chat_id) AS member_count
            FROM chats c
            JOIN users_chat uc ON c.chat_id=uc.chat_id
            WHERE uc.user_id=@uid
            ORDER BY uc.joined_at DESC", new { uid = userId });
    }

    public async Task<IEnumerable<dynamic>> GetMessagesAsync(int chatId, int limit = 100, int offset = 0)
    {
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        return await conn.QueryAsync(@"
            SELECT m.message_id, m.chat_id, m.user_id, m.text, m.sent_at, m.is_read,
                   u.username, u.avatar_url
            FROM chat_messages m
            JOIN users u ON m.user_id=u.user_id
            WHERE m.chat_id=@cid
            ORDER BY m.sent_at ASC
            LIMIT @limit OFFSET @offset", new { cid = chatId, limit, offset });
    }

    public async Task<ChatMessageDto?> SaveMessageAsync(int chatId, int userId, string text)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text)) throw new ArgumentException("Message cannot be empty");
        if (text.Length > 2000) throw new ArgumentException("Message too long (max 2000)");

        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        var messageId = await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO chat_messages (chat_id, user_id, text) VALUES (@cid, @uid, @text);
            SELECT LAST_INSERT_ID();",
            new { cid = chatId, uid = userId, text });

        var rows = await conn.QueryAsync(@"
            SELECT m.message_id, m.chat_id, m.user_id, m.text, m.sent_at, m.is_read,
                   u.username, u.avatar_url
            FROM chat_messages m
            JOIN users u ON m.user_id=u.user_id
            WHERE m.message_id=@id", new { id = messageId });
        var row = rows.FirstOrDefault();
        return row == null ? null : ChatMessageDto.FromDynamic(row);
    }

    public async Task<bool> CanAccessChatAsync(int userId, int chatId)
    {
        // Access is per chat: the global lobby, a chat the user already belongs to, or the queue chat
        // of a game the user is currently queued for (IsMemberAsync covers the last two).
        // Previously "queued for ANY game" granted access to EVERY chat.
        if (IsGlobalLobby(chatId)) return true;
        return await IsMemberAsync(userId, chatId);
    }

    public async Task EnsureChatAccessAsync(int userId, int chatId)
    {
        if (!await CanAccessChatAsync(userId, chatId))
            throw new UnauthorizedAccessException("Not allowed to join this chat");

        if (IsGlobalLobby(chatId) || await IsInAnyQueueAsync(userId))
            await JoinChatAsync(userId, chatId);
    }

    public async Task<int?> GetQueueChatIdForUserAsync(int userId)
    {
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int?>(@"
            SELECT c.chat_id FROM cloud_queue cq
            JOIN chats c ON c.game_id = cq.game_id
            WHERE cq.user_id=@uid AND cq.status IN ('waiting','ready')
            LIMIT 1", new { uid = userId });
    }
}
