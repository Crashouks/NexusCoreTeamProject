-- V002: integrity constraints and indexes for the hot query paths.
-- Plain ALTERs (MySQL has no ADD ... IF NOT EXISTS for these); the runner
-- applies each migration exactly once and records it in schema_migrations.

-- Orphan protection for achievements unlocked by users.
ALTER TABLE user_achievements
  ADD CONSTRAINT fk_user_ach_user FOREIGN KEY (user_id) REFERENCES users(user_id) ON DELETE CASCADE,
  ADD CONSTRAINT fk_user_ach_ach  FOREIGN KEY (achievement_id) REFERENCES achievements(achievement_id) ON DELETE CASCADE;

-- Data-quality rules enforced by the database, not just the API.
ALTER TABLE users
  ADD CONSTRAINT chk_users_balance CHECK (balance >= 0);
ALTER TABLE games
  ADD CONSTRAINT chk_games_price    CHECK (price >= 0),
  ADD CONSTRAINT chk_games_discount CHECK (discount_percent IS NULL OR discount_percent BETWEEN 1 AND 99);
ALTER TABLE libraries
  ADD CONSTRAINT chk_lib_progress CHECK (download_progress BETWEEN 0 AND 100),
  ADD CONSTRAINT chk_lib_playtime CHECK (playtime_mins >= 0);
ALTER TABLE cloud_sessions
  ADD CONSTRAINT chk_sess_duration CHECK (duration_mins >= 0);

-- Indexes for catalog browsing, ownership lookups and session history.
CREATE INDEX idx_games_status_release ON games (status, release_date);
CREATE INDEX idx_games_genre          ON games (genre);
CREATE INDEX idx_libraries_game       ON libraries (game_id);
CREATE INDEX idx_reviews_game         ON reviews (game_id);
CREATE INDEX idx_reviews_user         ON reviews (user_id);
CREATE INDEX idx_wishlist_game        ON wishlist (game_id);
CREATE INDEX idx_sessions_user_start  ON cloud_sessions (user_id, started_at);
CREATE INDEX idx_sessions_game        ON cloud_sessions (game_id);
CREATE INDEX idx_sessions_status      ON cloud_sessions (status);
CREATE INDEX idx_metrics_session_time ON stream_metrics (session_id, recorded_at);
