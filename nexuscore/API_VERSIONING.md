# Catalog API versions

The existing unversioned `GET /api/games` route remains unchanged for existing clients.

| Route | Query parameters | Response envelope |
|---|---|---|
| `GET /api/v1/games` | `page`, `limit` | `games`, `total`, `page`, `limit` |
| `GET /api/v2/games` | `page`, `pageSize` | `data`, `pagination` (`page`, `page_size`, `total_items`, `total_pages`) |

Both versions return the same approved-game item fields. Invalid page numbers or page sizes
(outside 1–100, or an offset too large for the repository query) return HTTP 400 with
`INVALID_PAGINATION`.

The first ten pages for common page sizes (10, 20, 50, 100) are cached in memory for 30 seconds.
Game updates, deletes, moderation actions, and image uploads invalidate the catalog cache.
The cache is local to one API process; other instances may serve a cached response until its
30-second expiry.
