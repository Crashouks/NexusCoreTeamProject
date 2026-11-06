using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using NexusCore.Api.Controllers;

namespace NexusCore.Api.Tests;

/// <summary>
/// "Verify endpoints for proper authentication": every controller action must say explicitly
/// whether it is protected ([Authorize]) or public ([AllowAnonymous]), and the set of public
/// endpoints is pinned to an allow-list so a new one can't slip in unnoticed.
/// </summary>
public class AuthorizationAuditTests
{
    private static readonly HashSet<string> AllowedAnonymous =
    [
        "AuthController.Register", "AuthController.Login", "AuthController.Logout",
        // Agent endpoints authenticate with server id + password in the body (and are rate limited).
        "CloudAgentController.Heartbeat", "CloudAgentController.PollJobs",
        "CloudAgentController.SessionStatus", "CloudAgentController.UpdateJobStatus",
        "CloudAgentController.SessionEnded",
        "CloudController.Plans", "CloudController.SetupInfo",
        "ForumsController.List", "ForumsController.Detail",
        "GamesController.List", "GamesController.Carousel", "GamesController.OnSale",
        "GamesController.Featured", "GamesController.NewReleases", "GamesController.Detail",
        "HealthController.Health", "HealthController.HealthDb",
        "MediaController.GetGameImage",
        "UsersController.Countries",
    ];

    private record ActionInfo(Type Controller, MethodInfo Method)
    {
        public string Name => $"{Controller.Name}.{Method.Name}";

        public bool IsAnonymous =>
            Method.GetCustomAttributes<AllowAnonymousAttribute>(true).Any()
            || Controller.GetCustomAttributes<AllowAnonymousAttribute>(true).Any();

        public bool IsProtected =>
            Method.GetCustomAttributes<AuthorizeAttribute>(true).Any()
            || Controller.GetCustomAttributes<AuthorizeAttribute>(true).Any();

        public IEnumerable<string> Roles =>
            Controller.GetCustomAttributes<AuthorizeAttribute>(true).Concat(Method.GetCustomAttributes<AuthorizeAttribute>(true))
                .Select(a => a.Roles ?? "");
    }

    private static IEnumerable<ActionInfo> Actions() =>
        typeof(AuthController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<HttpMethodAttribute>(true).Any())
                .Select(m => new ActionInfo(t, m)));

    [Fact]
    public void Audit_FindsControllers()
    {
        Assert.True(Actions().Count() > 60, "reflection found suspiciously few actions");
    }

    [Fact]
    public void EveryAction_IsExplicitlyProtectedOrExplicitlyAnonymous()
    {
        var unprotected = Actions().Where(a => !a.IsAnonymous && !a.IsProtected).Select(a => a.Name).OrderBy(n => n).ToList();
        Assert.True(unprotected.Count == 0, "Actions without [Authorize]/[AllowAnonymous]: " + string.Join(", ", unprotected));
    }

    [Fact]
    public void AnonymousEndpoints_MatchTheAllowList()
    {
        var actual = Actions().Where(a => a.IsAnonymous).Select(a => a.Name).ToHashSet();
        var unexpected = actual.Except(AllowedAnonymous).OrderBy(n => n).ToList();
        var stale = AllowedAnonymous.Except(actual).OrderBy(n => n).ToList();
        Assert.True(unexpected.Count == 0, "New anonymous endpoints (review them!): " + string.Join(", ", unexpected));
        Assert.True(stale.Count == 0, "Allow-list entries that no longer exist/are anonymous: " + string.Join(", ", stale));
    }

    [Fact]
    public void AdminController_RequiresAdminRole_AtClassLevel()
    {
        var attr = typeof(AdminController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(attr);
        Assert.Equal("admin", attr!.Roles);
    }

    [Theory]
    [InlineData(typeof(UsersController), nameof(UsersController.DeveloperRequests))]
    [InlineData(typeof(UsersController), nameof(UsersController.ApproveDeveloper))]
    [InlineData(typeof(UsersController), nameof(UsersController.RejectDeveloper))]
    [InlineData(typeof(UsersController), nameof(UsersController.DeleteUser))]
    [InlineData(typeof(GamesController), nameof(GamesController.Pending))]
    [InlineData(typeof(GamesController), nameof(GamesController.Delete))]
    [InlineData(typeof(GamesController), nameof(GamesController.AdminReview))]
    [InlineData(typeof(CloudController), nameof(CloudController.SessionsAll))]
    [InlineData(typeof(CloudController), nameof(CloudController.ForceEnd))]
    [InlineData(typeof(TrialsController), nameof(TrialsController.All))]
    public void PrivilegedActions_DeclareAdminRole(Type controller, string method)
    {
        var action = Actions().Single(a => a.Controller == controller && a.Method.Name == method);
        Assert.Contains(action.Roles, r => r.Split(',').Contains("admin"));
    }

    [Fact]
    public void AnonymousWriteEndpoints_AreOnlyAuthAndAgentCallbacks()
    {
        var writes = Actions()
            .Where(a => a.IsAnonymous && !a.Method.GetCustomAttributes<HttpGetAttribute>(true).Any())
            .Select(a => a.Controller.Name).Distinct().OrderBy(n => n).ToList();
        Assert.Equal(["AuthController", "CloudAgentController"], writes);
    }
}
