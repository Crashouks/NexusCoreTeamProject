-- V003: initial catalog and cloud plans. Re-runnable: rows are keyed on the
-- UNIQUE slug / plan name, existing rows are left untouched.
-- No user accounts are created here, on purpose: seeded credentials in SQL
-- would be a known-password backdoor. Register the first account through the
-- app, then promote it once (see database/README.md).

INSERT IGNORE INTO cloud_plans (plan_id, name, display_name, price_monthly, max_res, max_fps, ray_tracing, skip_queue, description) VALUES
  (1, 'starter',  'Starter',      4.99, '1080p',  60, FALSE, TRUE, '1080p/60fps, skip queue'),
  (2, 'pro',      'Pro',          9.99, '1440p', 120, FALSE, TRUE, '1440p/120fps, skip queue'),
  (3, 'ultimate', 'Ultimate RTX',19.99, '4K',    144, TRUE,  TRUE, '4K/144fps, RTX, skip queue');

INSERT IGNORE INTO games
  (name, slug, short_desc, genre, tags, developer_name, cover_url, price, is_free,
   is_featured, cloud_enabled, status, download_size_gb, is_carousel, carousel_order,
   discount_percent, discount_expires_at)
VALUES
  ('Neon Drift',      'neon-drift',      'High-octane cyberpunk racing through neon-lit cityscapes.', 'Racing',   'Racing,Multiplayer,Cloud',   'Nexus Studios',     'https://picsum.photos/seed/neondrift/400/560',   29.99, FALSE, TRUE,  TRUE,  'approved',  45, TRUE,  0, 15, DATE_ADD(NOW(), INTERVAL 7 DAY)),
  ('Void Walker',     'void-walker',     'Explore a shattered dimension in this atmospheric action RPG.', 'RPG',  'RPG,Adventure,Singleplayer', 'Dark Matter Games', 'https://picsum.photos/seed/voidwalker/400/560',  49.99, FALSE, TRUE,  TRUE,  'approved', 100, TRUE,  1, 25, DATE_ADD(NOW(), INTERVAL 30 DAY)),
  ('Pixel Siege',     'pixel-siege',     'Retro tower defense with modern twists.',                    'Strategy', 'Strategy,Indie',             'Pixel Forge',       'https://picsum.photos/seed/pixelsiege/400/560',   9.99, FALSE, FALSE, FALSE, 'approved',  10, FALSE, 0, NULL, NULL),
  ('Starfall Arena',  'starfall-arena',  'Competitive space combat arena shooter.',                    'Action',   'Action,Multiplayer,Cloud',   'Orbital Games',     'https://picsum.photos/seed/starfall/400/560',     0.00, TRUE,  TRUE,  TRUE,  'approved',  25, TRUE,  2, NULL, NULL),
  ('Cyber Heist',     'cyber-heist',     'Plan and execute the ultimate corporate heist.',             'Action',   'Action,Stealth,Cloud',       'Nexus Studios',     'https://picsum.photos/seed/cyberheist/400/560',  39.99, FALSE, TRUE,  TRUE,  'approved',  80, TRUE,  3, 20, DATE_ADD(NOW(), INTERVAL 14 DAY)),
  ('Mystic Realms',   'mystic-realms',   'Open-world fantasy adventure with deep lore.',               'RPG',      'RPG,Fantasy,Open World',     'Enchanted Byte',    'https://picsum.photos/seed/mystic/400/560',      59.99, FALSE, FALSE, FALSE, 'approved', 120, FALSE, 0, NULL, NULL),
  ('Turbo Kart',      'turbo-kart',      'Casual kart racing fun for everyone.',                       'Racing',   'Racing,Casual',              'Fun Games Co',      'https://picsum.photos/seed/turbokart/400/560',   14.99, FALSE, FALSE, TRUE,  'approved',  15, FALSE, 0, NULL, NULL),
  ('Shadow Protocol', 'shadow-protocol', 'Tactical espionage thriller.',                               'Action',   'Action,Stealth,Cloud',       'Stealth Labs',      'https://picsum.photos/seed/shadow/400/560',      34.99, FALSE, FALSE, TRUE,  'approved',  55, FALSE, 0, NULL, NULL);
