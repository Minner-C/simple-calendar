-- SimpleCalendar 后台数据库（MySQL 5.7+ / 8.x，utf8mb4）
-- 宝塔 → 数据库 → 创建数据库后，在 phpMyAdmin 里执行本文件即可

CREATE DATABASE IF NOT EXISTS simple_calendar DEFAULT CHARSET utf8mb4 COLLATE utf8mb4_general_ci;
USE simple_calendar;

-- 节假日 / 调班
CREATE TABLE IF NOT EXISTS holidays (
    date CHAR(10) PRIMARY KEY COMMENT 'YYYY-MM-DD',
    name VARCHAR(50) NOT NULL DEFAULT '' COMMENT '名称，如 春节 / 春节调班',
    type VARCHAR(10) NOT NULL DEFAULT 'holiday' COMMENT 'holiday=休假 workday=调班'
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 黄历宜忌
CREATE TABLE IF NOT EXISTS almanac (
    date CHAR(10) PRIMARY KEY COMMENT 'YYYY-MM-DD',
    yi VARCHAR(500) NOT NULL DEFAULT '',
    ji VARCHAR(500) NOT NULL DEFAULT '',
    festival VARCHAR(100) NOT NULL DEFAULT ''
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 广告位
CREATE TABLE IF NOT EXISTS ads (
    id INT AUTO_INCREMENT PRIMARY KEY,
    title VARCHAR(200) NOT NULL DEFAULT '',
    image_url VARCHAR(500) NOT NULL DEFAULT '',
    link_url VARCHAR(500) NOT NULL DEFAULT '',
    position VARCHAR(50) NOT NULL DEFAULT 'calendar_bottom'
        COMMENT 'calendar_bottom / weather_bottom / hourly_bottom'
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 管理员账号（密码用 password_hash 哈希存储，切勿存明文）
CREATE TABLE IF NOT EXISTS admins (
    id INT AUTO_INCREMENT PRIMARY KEY,
    username VARCHAR(50) NOT NULL UNIQUE,
    password_hash VARCHAR(255) NOT NULL,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 初始管理员：admin / admin123（首次登录后请立即在"账号管理"里修改）
INSERT INTO admins (username, password_hash)
SELECT 'admin', '$2y$10$hllIFGFoAPmXNhi.BUGqy.crEWkFlhSpP5IZ0IMNKIdnuPq9rB8v2'
FROM DUAL WHERE NOT EXISTS (SELECT 1 FROM admins WHERE username = 'admin');

-- 广告点击记录
CREATE TABLE IF NOT EXISTS ad_clicks (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    ad_id INT NOT NULL,
    ip VARCHAR(45) NOT NULL DEFAULT '',
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    INDEX idx_ad_id (ad_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ===== 初始数据：2025-2026 法定节假日 =====
INSERT INTO holidays (date, name, type) VALUES
('2025-01-01','元旦','holiday'),
('2025-01-28','春节','holiday'),('2025-01-29','春节','holiday'),('2025-01-30','春节','holiday'),
('2025-01-31','春节','holiday'),('2025-02-01','春节','holiday'),('2025-02-02','春节','holiday'),
('2025-02-03','春节','holiday'),('2025-02-04','春节','holiday'),
('2025-01-26','春节调班','workday'),('2025-02-08','春节调班','workday'),
('2025-04-04','清明节','holiday'),('2025-04-05','清明节','holiday'),('2025-04-06','清明节','holiday'),
('2025-05-01','劳动节','holiday'),('2025-05-02','劳动节','holiday'),('2025-05-03','劳动节','holiday'),
('2025-05-04','劳动节','holiday'),('2025-05-05','劳动节','holiday'),
('2025-04-27','劳动节调班','workday'),
('2025-05-31','端午节','holiday'),('2025-06-01','端午节','holiday'),('2025-06-02','端午节','holiday'),
('2025-10-01','国庆节','holiday'),('2025-10-02','国庆节','holiday'),('2025-10-03','国庆节','holiday'),
('2025-10-04','中秋节','holiday'),('2025-10-05','国庆节','holiday'),('2025-10-06','国庆节','holiday'),
('2025-10-07','国庆节','holiday'),('2025-10-08','国庆节','holiday'),
('2025-09-28','国庆调班','workday'),('2025-10-11','国庆调班','workday'),
('2026-01-01','元旦','holiday'),('2026-01-02','元旦','holiday'),('2026-01-03','元旦','holiday'),
('2025-12-28','元旦调班','workday'),
('2026-02-15','春节','holiday'),('2026-02-16','春节','holiday'),('2026-02-17','春节','holiday'),
('2026-02-18','春节','holiday'),('2026-02-19','春节','holiday'),('2026-02-20','春节','holiday'),
('2026-02-21','春节','holiday'),
('2026-02-14','春节调班','workday'),('2026-02-22','春节调班','workday'),
('2026-04-04','清明节','holiday'),('2026-04-05','清明节','holiday'),('2026-04-06','清明节','holiday'),
('2026-05-01','劳动节','holiday'),('2026-05-02','劳动节','holiday'),('2026-05-03','劳动节','holiday'),
('2026-05-04','劳动节','holiday'),('2026-05-05','劳动节','holiday'),
('2026-04-26','劳动节调班','workday'),
('2026-06-19','端午节','holiday'),('2026-06-20','端午节','holiday'),('2026-06-21','端午节','holiday'),
('2026-10-01','国庆节','holiday'),('2026-10-02','国庆节','holiday'),('2026-10-03','国庆节','holiday'),
('2026-10-04','中秋节','holiday'),('2026-10-05','国庆节','holiday'),('2026-10-06','国庆节','holiday'),
('2026-10-07','国庆节','holiday'),('2026-10-08','国庆节','holiday'),
('2026-09-27','国庆调班','workday'),('2026-10-10','国庆调班','workday')
ON DUPLICATE KEY UPDATE name = VALUES(name), type = VALUES(type);

-- 黄历示例数据
INSERT INTO almanac (date, yi, ji, festival) VALUES
('2026-01-01','祭祀 祈福 求嗣','嫁娶 开市 动土','元旦')
ON DUPLICATE KEY UPDATE yi = VALUES(yi), ji = VALUES(ji), festival = VALUES(festival);
