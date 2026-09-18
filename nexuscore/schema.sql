CREATE DATABASE IF NOT EXISTS nexuscore;
USE nexuscore;

CREATE TABLE IF NOT EXISTS users (
  user_id               INT PRIMARY KEY AUTO_INCREMENT,
  username              VARCHAR(50)  UNIQUE NOT NULL,
  email                 VARCHAR(100) UNIQUE NOT NULL,
  password              VARCHAR(255) NOT NULL,
  avatar_url            VARCHAR(255),
  bio                   TEXT,
  country               VARCHAR(50)  DEFAULT '',
  reg_date              DATETIME     DEFAULT NOW(),
  balance               DECIMAL(10,2) DEFAULT 0.00,
  role                  ENUM('user','developer','admin') DEFAULT 'user',
  cloud_plan            ENUM('none','free','starter','pro','ultimate') DEFAULT 'free',
  cloud_plan_expires    DATETIME,
  cloud_free_used_today BOOLEAN  DEFAULT FALSE,
  cloud_free_reset_at   DATETIME,
  developer_company          VARCHAR(100),
  developer_requested_at     DATETIME,
  is_developer_approved      BOOLEAN DEFAULT FALSE
);

CREATE TABLE IF NOT EXISTS games (
  game_id          INT PRIMARY KEY AUTO_INCREMENT,
  name             VARCHAR(150) NOT NULL,
  slug             VARCHAR(150) UNIQUE,
  short_desc       VARCHAR(300),
  description      TEXT,
  genre            VARCHAR(60),
  tags             VARCHAR(255),
  developer_name   VARCHAR(100),
  developer_id     INT,
  cover_url        VARCHAR(255),
  trailer_url      VARCHAR(255),
  download_size_gb DECIMAL(8,2) DEFAULT 25.00,
  price            DECIMAL(8,2) DEFAULT 0.00,
  is_free          BOOLEAN DEFAULT FALSE,
  is_featured      BOOLEAN DEFAULT FALSE,
  is_carousel      BOOLEAN DEFAULT FALSE,
  carousel_order   INT DEFAULT 0,
  discount_percent INT DEFAULT NULL,
  discount_expires_at DATETIME DEFAULT NULL,
  cloud_enabled    BOOLEAN DEFAULT FALSE,
  trial_enabled    BOOLEAN DEFAULT TRUE,
  trial_duration_mins INT DEFAULT 30,
  trial_level_limit INT DEFAULT NULL,
  trial_discount_percent INT DEFAULT 10,
  release_date     DATETIME DEFAULT NOW(),
  requirements     TEXT,
  rejection_reason TEXT,
  reviewed_at      DATETIME,
  status           ENUM('pending','approved','rejected') DEFAULT 'pending',
  submitted_at     DATETIME DEFAULT NOW(),
  FOREIGN KEY (developer_id) REFERENCES users(user_id)
);

CREATE TABLE IF NOT EXISTS libraries (
  user_id        INT NOT NULL,
  game_id        INT NOT NULL,
  purchase_date  DATETIME      DEFAULT NOW(),
  purchase_price DECIMAL(8,2)  DEFAULT 0.00,
  playtime_mins  INT           DEFAULT 0,
  last_played    DATETIME,
  download_status ENUM('none','downloading','installed') DEFAULT 'none',
  download_progress INT DEFAULT 0,
  PRIMARY KEY (user_id, game_id),
  FOREIGN KEY (user_id) REFERENCES users(user_id),
  FOREIGN KEY (game_id) REFERENCES games(game_id)
);

CREATE TABLE IF NOT EXISTS friends (
  user_id    INT NOT NULL,
  friend_id  INT NOT NULL,
  status     ENUM('pending','accepted') DEFAULT 'pending',
  created_at DATETIME DEFAULT NOW(),
  PRIMARY KEY (user_id, friend_id),
  FOREIGN KEY (user_id)   REFERENCES users(user_id),
  FOREIGN KEY (friend_id) REFERENCES users(user_id)
);

CREATE TABLE IF NOT EXISTS reviews (
  review_id      INT PRIMARY KEY AUTO_INCREMENT,
  user_id        INT,
  game_id        INT,
  rating         INT CHECK (rating BETWEEN 1 AND 10),
  review_text    TEXT,
  is_recommended BOOLEAN DEFAULT TRUE,
  review_date    DATETIME DEFAULT NOW(),
  FOREIGN KEY (user_id) REFERENCES users(user_id),
  FOREIGN KEY (game_id) REFERENCES games(game_id)
);

CREATE TABLE IF NOT EXISTS game_media (
  media_id   INT PRIMARY KEY AUTO_INCREMENT,
  game_id    INT,
  media_type ENUM('image','video') DEFAULT 'image',
  url        VARCHAR(255),
  sort_order INT DEFAULT 0,
  FOREIGN KEY (game_id) REFERENCES games(game_id)
);

CREATE TABLE IF NOT EXISTS achievements (
  achievement_id INT PRIMARY KEY AUTO_INCREMENT,
  game_id        INT,
  name           VARCHAR(100),
  description    TEXT,
  icon_url       VARCHAR(255),
  FOREIGN KEY (game_id) REFERENCES games(game_id)
);

CREATE TABLE IF NOT EXISTS user_achievements (
  user_id        INT,
  achievement_id INT,
  unlocked_at    DATETIME DEFAULT NOW(),
  PRIMARY KEY (user_id, achievement_id)
);

CREATE TABLE IF NOT EXISTS cloud_plans (
  plan_id        INT PRIMARY KEY AUTO_INCREMENT,
  name           ENUM('starter','pro','ultimate') UNIQUE NOT NULL,
  display_name   VARCHAR(60),
  price_monthly  DECIMAL(8,2) NOT NULL,
  max_res        VARCHAR(10),
  max_fps        INT,
  ray_tracing    BOOLEAN DEFAULT FALSE,
  skip_queue     BOOLEAN DEFAULT TRUE,
  description    TEXT
);

CREATE TABLE IF NOT EXISTS cloud_queue (
  queue_id    INT PRIMARY KEY AUTO_INCREMENT,
  user_id     INT UNIQUE NOT NULL,
  game_id     INT NOT NULL,
  joined_at   DATETIME DEFAULT NOW(),
  position    INT,
  status      ENUM('waiting','ready','expired') DEFAULT 'waiting',
  notified_at DATETIME,
  expires_at  DATETIME,
  FOREIGN KEY (user_id) REFERENCES users(user_id),
  FOREIGN KEY (game_id) REFERENCES games(game_id)
);

CREATE TABLE IF NOT EXISTS cloud_sessions (
  session_id    INT PRIMARY KEY AUTO_INCREMENT,
  user_id       INT NOT NULL,
  game_id       INT NOT NULL,
  plan          ENUM('free','starter','pro','ultimate') NOT NULL,
  started_at    DATETIME DEFAULT NOW(),
  ended_at      DATETIME,
  duration_mins INT DEFAULT 0,
  max_duration_mins INT NOT NULL,
  status        ENUM('active','ended','expired','force_ended') DEFAULT 'active',
  stream_token  VARCHAR(255),
  server_id     INT DEFAULT NULL,
  server_region VARCHAR(50) DEFAULT 'eu-central',
  is_real_stream BOOLEAN NOT NULL DEFAULT FALSE,
  allow_spectators BOOLEAN NOT NULL DEFAULT FALSE,
  stream_quality VARCHAR(10) DEFAULT '1080p',
  FOREIGN KEY (user_id) REFERENCES users(user_id),
  FOREIGN KEY (game_id) REFERENCES games(game_id)
);

CREATE TABLE IF NOT EXISTS cloud_servers (
  server_id        INT PRIMARY KEY AUTO_INCREMENT,
  name             VARCHAR(100) NOT NULL,
  host             VARCHAR(255) NOT NULL,
  region           VARCHAR(50) NOT NULL DEFAULT 'eu-central',
  gpu_model        VARCHAR(100) DEFAULT 'RTX 4080',
  max_slots        INT NOT NULL DEFAULT 1,
  account_username VARCHAR(100) NOT NULL DEFAULT '',
  account_secret   VARCHAR(255) NOT NULL DEFAULT '',
  access_password_hash VARCHAR(255) DEFAULT NULL,
  player_password_hash VARCHAR(255) DEFAULT NULL,
  server_tier ENUM('free_fake','paid_fake','real') NOT NULL DEFAULT 'real',
  status           ENUM('online','offline','maintenance') NOT NULL DEFAULT 'offline',
  notes            TEXT,
  created_at       DATETIME DEFAULT NOW(),
  last_heartbeat   DATETIME DEFAULT NULL
);

CREATE TABLE IF NOT EXISTS cloud_server_games (
  server_id INT NOT NULL,
  game_id INT NOT NULL,
  executable_path VARCHAR(512) NOT NULL,
  PRIMARY KEY (server_id, game_id),
  FOREIGN KEY (server_id) REFERENCES cloud_servers(server_id) ON DELETE CASCADE,
  FOREIGN KEY (game_id) REFERENCES games(game_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS cloud_agent_jobs (
  job_id INT PRIMARY KEY AUTO_INCREMENT,
  session_id INT NOT NULL,
  server_id INT NOT NULL,
  game_id INT NOT NULL,
  job_type ENUM('launch','stop') NOT NULL,
  executable_path VARCHAR(512) DEFAULT NULL,
  status ENUM('pending','running','done','failed','cancelled') NOT NULL DEFAULT 'pending',
  error_message VARCHAR(500) DEFAULT NULL,
  created_at DATETIME DEFAULT NOW(),
  processed_at DATETIME DEFAULT NULL,
  FOREIGN KEY (session_id) REFERENCES cloud_sessions(session_id) ON DELETE CASCADE,
  FOREIGN KEY (server_id) REFERENCES cloud_servers(server_id) ON DELETE CASCADE,
  FOREIGN KEY (game_id) REFERENCES games(game_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS cart_items (
  user_id   INT NOT NULL,
  game_id   INT NOT NULL,
  added_at  DATETIME DEFAULT NOW(),
  PRIMARY KEY (user_id, game_id),
  FOREIGN KEY (user_id) REFERENCES users(user_id),
  FOREIGN KEY (game_id) REFERENCES games(game_id)
);

CREATE TABLE IF NOT EXISTS wishlist (
  user_id   INT NOT NULL,
  game_id   INT NOT NULL,
  added_at  DATETIME DEFAULT NOW(),
  PRIMARY KEY (user_id, game_id),
  FOREIGN KEY (user_id) REFERENCES users(user_id),
  FOREIGN KEY (game_id) REFERENCES games(game_id)
);

CREATE TABLE IF NOT EXISTS chats (
  chat_id       INT PRIMARY KEY AUTO_INCREMENT,
  name          VARCHAR(100) NOT NULL,
  created_at    DATETIME NOT NULL DEFAULT NOW(),
  description   VARCHAR(255) NULL,
  game_id       INT NULL,
  FOREIGN KEY (game_id) REFERENCES games(game_id) ON DELETE SET NULL
);

CREATE TABLE IF NOT EXISTS users_chat (
  user_chat_id  INT PRIMARY KEY AUTO_INCREMENT,
  user_id       INT NOT NULL,
  chat_id       INT NOT NULL,
  joined_at     DATETIME NOT NULL DEFAULT NOW(),
  is_admin      BOOLEAN NOT NULL DEFAULT FALSE,
  UNIQUE KEY uq_user_chat (user_id, chat_id),
  FOREIGN KEY (user_id) REFERENCES users(user_id) ON DELETE CASCADE,
  FOREIGN KEY (chat_id) REFERENCES chats(chat_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS chat_messages (
  message_id    INT PRIMARY KEY AUTO_INCREMENT,
  chat_id       INT NOT NULL,
  user_id       INT NOT NULL,
  text          TEXT NOT NULL,
  sent_at       DATETIME NOT NULL DEFAULT NOW(),
  is_read       BOOLEAN NOT NULL DEFAULT FALSE,
  FOREIGN KEY (chat_id) REFERENCES chats(chat_id) ON DELETE CASCADE,
  FOREIGN KEY (user_id) REFERENCES users(user_id) ON DELETE CASCADE,
  INDEX idx_chat_messages_chat_id (chat_id)
);

CREATE TABLE IF NOT EXISTS notifications (
  notification_id INT PRIMARY KEY AUTO_INCREMENT,
  user_id         INT NOT NULL,
  type            VARCHAR(32) NOT NULL,
  message         VARCHAR(255) NOT NULL,
  link            VARCHAR(255) NULL,
  is_read         BOOLEAN NOT NULL DEFAULT FALSE,
  created_at      DATETIME NOT NULL DEFAULT NOW(),
  ref_user_id     INT NULL,
  ref_game_id     INT NULL,
  FOREIGN KEY (user_id) REFERENCES users(user_id) ON DELETE CASCADE,
  INDEX idx_notifications_user (user_id, is_read, created_at)
);

CREATE TABLE IF NOT EXISTS forum_topics (
  topic_id    INT PRIMARY KEY AUTO_INCREMENT,
  title       VARCHAR(200) NOT NULL,
  created_by  INT NOT NULL,
  created_at  DATETIME NOT NULL DEFAULT NOW(),
  FOREIGN KEY (created_by) REFERENCES users(user_id)
);

CREATE TABLE IF NOT EXISTS forum_posts (
  post_id     INT PRIMARY KEY AUTO_INCREMENT,
  topic_id    INT NOT NULL,
  user_id     INT NOT NULL,
  content     TEXT NOT NULL,
  created_at  DATETIME NOT NULL DEFAULT NOW(),
  FOREIGN KEY (topic_id) REFERENCES forum_topics(topic_id) ON DELETE CASCADE,
  FOREIGN KEY (user_id) REFERENCES users(user_id),
  INDEX idx_forum_posts_topic (topic_id)
);

CREATE TABLE IF NOT EXISTS trials (
  trial_id      INT PRIMARY KEY AUTO_INCREMENT,
  user_id       INT NOT NULL,
  game_id       INT NOT NULL,
  started_at    DATETIME DEFAULT NOW(),
  ended_at      DATETIME,
  duration_mins INT DEFAULT 0,
  status        ENUM('active','completed','purchased','expired') DEFAULT 'active',
  UNIQUE KEY one_trial_per_game (user_id, game_id),
  FOREIGN KEY (user_id) REFERENCES users(user_id),
  FOREIGN KEY (game_id) REFERENCES games(game_id)
);
