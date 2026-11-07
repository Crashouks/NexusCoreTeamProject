using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexusCore.Api.Controllers;

namespace NexusCore.Api.Tests;

public class ApiVersioningTests
{
    [Fact]
    public void CatalogRoutes_ExposeV1AndV2WithExplicitAnonymousAccess()
    {
        var actions = typeof(VersionedGamesController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .ToDictionary(
                method => method.Name,
                method => (Method: method, Route: method.GetCustomAttribute<HttpGetAttribute>()?.Template));

        Assert.Equal("v1/games", actions[nameof(VersionedGamesController.ListV1)].Route);
        Assert.Equal("v2/games", actions[nameof(VersionedGamesController.ListV2)].Route);
        Assert.All(actions.Values, action =>
            Assert.NotNull(action.Method.GetCustomAttribute<AllowAnonymousAttribute>()));
    }
}
