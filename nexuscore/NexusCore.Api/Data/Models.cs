namespace NexusCore.Api.Data;

public class GameSummary
{
    public int GameId { get; init; }
    public string Name { get; init; } = "";
    public string? Slug { get; init; }
    public string? ShortDesc { get; init; }
    public string? Genre { get; init; }
    public string? CoverUrl { get; init; }
    public decimal Price { get; init; }
    public bool IsFree { get; init; }
    public bool CloudEnabled { get; init; }
    public int? DiscountPercent { get; init; }
}

/// <summary>Public profile only. Password hash and balance are never loaded by this query.</summary>
public class UserProfile
{
    public int UserId { get; init; }
    public string Username { get; init; } = "";
    public string? AvatarUrl { get; init; }
    public string? Bio { get; init; }
    public string? Country { get; init; }
    public DateTime RegDate { get; init; }
}

public class OwnedGame
{
    public int GameId { get; init; }
    public string Name { get; init; } = "";
    public string? Slug { get; init; }
    public string? CoverUrl { get; init; }
    public DateTime PurchaseDate { get; init; }
    public decimal PurchasePrice { get; init; }
    public int PlaytimeMins { get; init; }
    public DateTime? LastPlayed { get; init; }
}

public class StreamingSessionRecord
{
    public int SessionId { get; init; }
    public int GameId { get; init; }
    public string GameName { get; init; } = "";
    public string Plan { get; init; } = "";
    public DateTime StartedAt { get; init; }
    public DateTime? EndedAt { get; init; }
    public int DurationMins { get; init; }
    public string Status { get; init; } = "";
    public string? StreamQuality { get; init; }
}
