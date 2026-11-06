using Microsoft.AspNetCore.Mvc;

namespace NexusCore.Api.Helpers;

public static class ApiResults
{
    public static IActionResult Error(int status, string message, string code) =>
        new ObjectResult(new { error = message, code }) { StatusCode = status };

    /// <summary>
    /// 500 response that does not leak exception text (SQL, connection strings, paths ...) to the client.
    /// The full exception goes to the server log only.
    /// </summary>
    public static IActionResult ServerError(Exception ex)
    {
        Console.Error.WriteLine($"[500] {ex}");
        return Error(500, "Internal server error", "SERVER_ERROR");
    }
}
