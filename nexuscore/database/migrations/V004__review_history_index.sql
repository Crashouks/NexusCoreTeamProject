-- The game detail endpoint filters reviews by game_id and orders them newest first.
-- Reuse the game_id prefix for the foreign key and add review_date for that ordering.
CREATE INDEX idx_reviews_game_date ON reviews (game_id, review_date DESC);
DROP INDEX idx_reviews_game ON reviews;
