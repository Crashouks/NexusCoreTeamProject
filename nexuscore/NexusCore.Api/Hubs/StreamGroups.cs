namespace NexusCore.Api.Hubs;

public static class StreamGroups
{
    public static string GetSessionGroup(string sessionId) => $"session_{sessionId}";
    public static string GetUserGroup(string userId) => $"user_{userId}";
    public static string Player(int sessionId) => $"player_{sessionId}";
    public static string Agent(int sessionId) => $"agent_{sessionId}";
    public static string Spectator(int sessionId) => $"spectator_{sessionId}";
}

