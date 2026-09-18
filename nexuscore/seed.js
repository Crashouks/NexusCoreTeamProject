const bcrypt = require('bcrypt');
const mysql = require('mysql2/promise');
const path = require('path');
require('dotenv').config({ path: path.join(__dirname, '.env') });

async function seed() {
  const conn = await mysql.createConnection({
    host: process.env.DB_HOST || 'localhost',
    user: process.env.DB_USER || 'root',
    password: process.env.DB_PASSWORD || '',
    multipleStatements: true,
  });

  const fs = require('fs');
  const schema = fs.readFileSync(path.join(__dirname, 'schema.sql'), 'utf8');
  await conn.query(schema);

  const migrations = [
    'ALTER TABLE nexuscore.games ADD COLUMN trial_enabled BOOLEAN DEFAULT TRUE',
    'ALTER TABLE nexuscore.games ADD COLUMN trial_duration_mins INT DEFAULT 30',
    'ALTER TABLE nexuscore.games ADD COLUMN trial_level_limit INT DEFAULT NULL',
    'ALTER TABLE nexuscore.games ADD COLUMN trial_discount_percent INT DEFAULT 10',
    'ALTER TABLE nexuscore.games ADD COLUMN is_carousel BOOLEAN DEFAULT FALSE',
    'ALTER TABLE nexuscore.games ADD COLUMN carousel_order INT DEFAULT 0',
    'ALTER TABLE nexuscore.games ADD COLUMN discount_percent INT DEFAULT NULL',
    'ALTER TABLE nexuscore.games ADD COLUMN discount_expires_at DATETIME DEFAULT NULL',
    'ALTER TABLE nexuscore.games ADD COLUMN download_size_gb DECIMAL(8,2) DEFAULT 25.00',
    "ALTER TABLE nexuscore.libraries ADD COLUMN download_status ENUM('none','downloading','installed') DEFAULT 'none'",
    'ALTER TABLE nexuscore.libraries ADD COLUMN download_progress INT DEFAULT 0',
    `CREATE TABLE IF NOT EXISTS nexuscore.cart_items (
      user_id INT NOT NULL, game_id INT NOT NULL, added_at DATETIME DEFAULT NOW(),
      PRIMARY KEY (user_id, game_id),
      FOREIGN KEY (user_id) REFERENCES users(user_id), FOREIGN KEY (game_id) REFERENCES games(game_id))`,
    `CREATE TABLE IF NOT EXISTS nexuscore.wishlist (
      user_id INT NOT NULL, game_id INT NOT NULL, added_at DATETIME DEFAULT NOW(),
      PRIMARY KEY (user_id, game_id),
      FOREIGN KEY (user_id) REFERENCES users(user_id), FOREIGN KEY (game_id) REFERENCES games(game_id))`,
  ];
  for (const sql of migrations) {
    try { await conn.query(sql); } catch (e) {
      if (!['ER_DUP_FIELDNAME', 'ER_TABLE_EXISTS_ERROR', 'ER_DUP_KEYNAME'].includes(e.code)) {
        console.warn('Migration skip:', e.message);
      }
    }
  }

  const hash = await bcrypt.hash('admin123', 10);
  await conn.query(
    `INSERT IGNORE INTO users (user_id, username, email, password, role, balance, cloud_plan)
     VALUES (1, 'admin', 'admin@nexuscore.com', ?, 'admin', 1000.00, 'ultimate')`,
    [hash]
  );

  await conn.query(`
    INSERT IGNORE INTO cloud_plans (plan_id, name, display_name, price_monthly, max_res, max_fps, ray_tracing, skip_queue, description) VALUES
    (1,'none','None',0.00,NULL,NULL,FALSE,FALSE,'No cloud gaming access'),
    (2,'free','Free',0.00,'1080p',60,FALSE,FALSE,'1 hour/day, queue required'),
    (3,'starter','Starter',4.99,'1080p',60,FALSE,TRUE,'1080p/60fps, skip queue'),
    (4,'pro','Pro',9.99,'1440p',120,FALSE,TRUE,'1440p/120fps, skip queue'),
    (5,'ultimate','Ultimate RTX',19.99,'4K',144,TRUE,TRUE,'4K/144fps, RTX, skip queue')
  `);

  const devHash = await bcrypt.hash('dev123', 10);
  await conn.query(
    `INSERT IGNORE INTO users (user_id, username, email, password, role, balance, cloud_plan, is_developer_approved)
     VALUES (2, 'devuser', 'dev@nexuscore.com', ?, 'developer', 500.00, 'pro', TRUE)`,
    [devHash]
  );

  const games = [
    ['Neon Drift', 'neon-drift', 'High-octane cyberpunk racing through neon-lit cityscapes.', 'Racing', 'Racing,Multiplayer,Cloud', 'Nexus Studios', 2, 'https://picsum.photos/seed/neondrift/400/560', null, 29.99, false, true, true, 'approved', 45],
    ['Void Walker', 'void-walker', 'Explore a shattered dimension in this atmospheric action RPG.', 'RPG', 'RPG,Adventure,Singleplayer', 'Dark Matter Games', 2, 'https://picsum.photos/seed/voidwalker/400/560', null, 49.99, false, true, true, 'approved', 100],
    ['Pixel Siege', 'pixel-siege', 'Retro tower defense with modern twists.', 'Strategy', 'Strategy,Indie', 'Pixel Forge', 2, 'https://picsum.photos/seed/pixelsiege/400/560', null, 9.99, false, false, false, 'approved', 10],
    ['Starfall Arena', 'starfall-arena', 'Competitive space combat arena shooter.', 'Action', 'Action,Multiplayer,Cloud', 'Orbital Games', 2, 'https://picsum.photos/seed/starfall/400/560', null, 0, true, true, true, 'approved', 25],
    ['Cyber Heist', 'cyber-heist', 'Plan and execute the ultimate corporate heist.', 'Action', 'Action,Stealth,Cloud', 'Nexus Studios', 2, 'https://picsum.photos/seed/cyberheist/400/560', null, 39.99, false, true, true, 'approved', 80],
    ['Mystic Realms', 'mystic-realms', 'Open-world fantasy adventure with deep lore.', 'RPG', 'RPG,Fantasy,Open World', 'Enchanted Byte', 2, 'https://picsum.photos/seed/mystic/400/560', null, 59.99, false, false, false, 'approved', 120],
    ['Turbo Kart', 'turbo-kart', 'Casual kart racing fun for everyone.', 'Racing', 'Racing,Casual', 'Fun Games Co', 2, 'https://picsum.photos/seed/turbokart/400/560', null, 14.99, false, false, true, 'approved', 15],
    ['Shadow Protocol', 'shadow-protocol', 'Tactical espionage thriller.', 'Action', 'Action,Stealth,Cloud', 'Stealth Labs', 2, 'https://picsum.photos/seed/shadow/400/560', null, 34.99, false, false, true, 'approved', 55],
  ];

  for (const g of games) {
    await conn.query(
      `INSERT IGNORE INTO games (name, slug, short_desc, genre, tags, developer_name, developer_id, cover_url, trailer_url, price, is_free, is_featured, cloud_enabled, status, download_size_gb)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      g
    );
  }

  await conn.query(`
    UPDATE nexuscore.games SET trailer_url=NULL;
    UPDATE nexuscore.games SET is_carousel=TRUE, carousel_order=0 WHERE slug='neon-drift';
    UPDATE nexuscore.games SET is_carousel=TRUE, carousel_order=1 WHERE slug='void-walker';
    UPDATE nexuscore.games SET is_carousel=TRUE, carousel_order=2 WHERE slug='starfall-arena';
    UPDATE nexuscore.games SET is_carousel=TRUE, carousel_order=3 WHERE slug='cyber-heist';
    UPDATE nexuscore.games SET discount_percent=25, discount_expires_at=DATE_ADD(NOW(), INTERVAL 30 DAY) WHERE slug='void-walker';
    UPDATE nexuscore.games SET discount_percent=20, discount_expires_at=DATE_ADD(NOW(), INTERVAL 14 DAY) WHERE slug='cyber-heist';
    UPDATE nexuscore.games SET discount_percent=15, discount_expires_at=DATE_ADD(NOW(), INTERVAL 7 DAY) WHERE slug='neon-drift';
    UPDATE nexuscore.games SET download_size_gb=45 WHERE slug='neon-drift';
    UPDATE nexuscore.games SET download_size_gb=100 WHERE slug='void-walker';
    UPDATE nexuscore.games SET download_size_gb=10 WHERE slug='pixel-siege';
    UPDATE nexuscore.games SET download_size_gb=25 WHERE slug='starfall-arena';
    UPDATE nexuscore.games SET download_size_gb=80 WHERE slug='cyber-heist';
    UPDATE nexuscore.games SET download_size_gb=120 WHERE slug='mystic-realms';
    UPDATE nexuscore.games SET download_size_gb=15 WHERE slug='turbo-kart';
    UPDATE nexuscore.games SET download_size_gb=55 WHERE slug='shadow-protocol';
  `);

  console.log('Seed complete! NexusCore is ready.');
  console.log('Admin: admin@nexuscore.com / admin123');
  console.log('Developer: dev@nexuscore.com / dev123');
  await conn.end();
}

seed().catch(err => { console.error(err); process.exit(1); });