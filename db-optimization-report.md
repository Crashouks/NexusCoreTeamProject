# Database optimization and caching report

## Query reviewed

The game-detail path loads reviews with:

```sql
SELECT r.*, u.username, u.avatar_url
FROM reviews r
JOIN users u ON r.user_id = u.user_id
WHERE r.game_id = @gid
ORDER BY r.review_date DESC;
```

Migration V002 provided an index on `reviews(game_id)`. That index filters by game but does not
also provide the requested date ordering. Migration
[`V004__review_history_index.sql`](./nexuscore/database/migrations/V004__review_history_index.sql)
replaces it with `reviews(game_id, review_date DESC)`. The leftmost `game_id` column continues to
support the foreign-key lookup, while the second column supports newest-first review retrieval.
The composite index is created before dropping the named V002 index so a supporting foreign-key
index is always present during the migration.

## Measurement status

The index change is implemented, but no MySQL instance was available for before/after
`EXPLAIN ANALYZE` or latency measurements in this environment. No numeric improvement is claimed.
Capture a baseline on V003 before applying V004, then repeat against the same data after migration:

```sql
EXPLAIN ANALYZE
SELECT r.*, u.username, u.avatar_url
FROM reviews r
JOIN users u ON r.user_id = u.user_id
WHERE r.game_id = 1
ORDER BY r.review_date DESC;
```

| Measurement | Before (V003) | After (V004) |
|---|---|---|
| Execution time | Not measured | Not measured |
| Rows examined / access path | Not measured | Not measured |
| Sort operation | Not measured | Not measured |

## Cache implementation

`/api/v1/games` and `/api/v2/games` cache common first-page catalog responses in process memory
for 30 seconds. Only the first ten pages and page sizes 10, 20, 50, or 100 are cached to bound
cache-key growth. Cache generations are advanced after game updates, deletions, moderation
changes, and cover-image updates; uncommon page sizes and deeper pages bypass the cache.

The cache is per API instance and not shared across replicas. Its loader is covered by a test
that confirms a repeated request avoids another database read and that invalidation reloads the
page. Measure actual DB-call and response-time changes with the benchmark report before claiming
a numeric impact. The short expiry also bounds staleness when data changes outside the .NET API.
