using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexusCore.Api.Data;
using NexusCore.Api.Helpers;
using NexusCore.Api.Services;

namespace NexusCore.Api.Controllers;

[ApiController]
[Route("api")]
public sealed class VersionedGamesController(GameRepository games, GameCatalogCache catalogCache) : ControllerBase
{
    private static readonly HashSet<int> CacheablePageSizes = [10, 20, 50, 100];

    /// <summary>Returns the approved catalog using the original page envelope.</summary>
    [HttpGet("v1/games")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(GameCatalogV1Response), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ListV1(
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsValidPage(page, limit))
                return ApiResults.Error(400, "page must be positive and limit must be between 1 and 100", "INVALID_PAGINATION");

            var result = await GetPageAsync(page, limit, cancellationToken);
            return Ok(new GameCatalogV1Response(result.Games, result.Total, page, limit));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return ApiResults.ServerError(ex); }
    }

    /// <summary>Returns the approved catalog using the v2 data and pagination envelope.</summary>
    [HttpGet("v2/games")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(GameCatalogV2Response), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ListV2(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsValidPage(page, pageSize))
                return ApiResults.Error(400, "page must be positive and pageSize must be between 1 and 100", "INVALID_PAGINATION");

            var result = await GetPageAsync(page, pageSize, cancellationToken);
            var totalPages = result.Total == 0 ? 0 : (result.Total - 1) / pageSize + 1;
            return Ok(new GameCatalogV2Response(
                result.Games,
                new GameCatalogPagination(page, pageSize, result.Total, totalPages)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return ApiResults.ServerError(ex); }
    }

    private Task<ApprovedGamePage> GetPageAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var key = $"page:{page}:size:{pageSize}";
        if (page <= 10 && CacheablePageSizes.Contains(pageSize))
            return catalogCache.GetOrCreateAsync(key, () => LoadPageAsync(page, pageSize, cancellationToken));

        return LoadPageAsync(page, pageSize, cancellationToken);
    }

    private async Task<ApprovedGamePage> LoadPageAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var offset = (int)((long)(page - 1) * pageSize);
        var pageGames = await games.ListApprovedAsync(pageSize, offset, cancellationToken);
        var total = await games.CountApprovedAsync(cancellationToken);
        return new ApprovedGamePage(pageGames, total);
    }

    private static bool IsValidPage(int page, int pageSize) =>
        page > 0
        && pageSize is >= 1 and <= 100
        && (long)(page - 1) * pageSize <= int.MaxValue;

    private sealed record ApprovedGamePage(IReadOnlyList<GameSummary> Games, int Total);
}

public sealed record GameCatalogV1Response(
    IReadOnlyList<GameSummary> Games,
    int Total,
    int Page,
    int Limit);

public sealed record GameCatalogV2Response(
    IReadOnlyList<GameSummary> Data,
    GameCatalogPagination Pagination);

public sealed record GameCatalogPagination(
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages);
