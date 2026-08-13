# SimpleCalendar 后台服务（PHP + MySQL）

为 SimpleCalendar 桌面软件提供节假日 / 黄历宜忌 / 广告位接口，
带网页管理后台，数据存 MySQL。无框架、无第三方依赖，PHP 7.4+ 即可运行
（宝塔面板部署见 [宝塔部署教程.md](宝塔部署教程.md)）。

## 文件说明

| 文件 | 作用 |
|------|------|
| `index.php` | 公开 API（客户端调用的接口） |
| `admin.html` | 管理后台前端页面（纯 HTML + CSS + JS） |
| `admin_api.php` | 管理后台 JSON 接口（登录 + 数据增删改，供 admin.html 调用） |
| `config.php` | 数据库连接（**部署后务必修改**） |
| `db.php` | PDO 连接 |
| `install.sql` | 建表 + 初始节假日数据（2025–2026） |

## 接口

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/api/holidays` | 全部节假日数据（客户端启动时拉取） |
| GET | `/api/holidays/{year}` | 按年份查询，如 `/api/holidays/2026` |
| GET | `/api/holidays/check/{date}` | 单日查询，如 `/api/holidays/check/2026-10-01` |
| GET | `/api/almanac/{date}` | 黄历宜忌，如 `/api/almanac/2026-01-01` |
| GET | `/api/ads/active` | 生效中的广告位列表 |
| POST | `/api/ads/{id}/click` | 广告点击上报（写入 `ad_clicks` 表） |

## 管理后台

访问 `https://你的域名/admin.html`（纯 HTML 前端，通过 `admin_api.php` 读写数据）。

管理员账号存数据库 `admins` 表（bcrypt 哈希），初始账号 **admin / admin123**，
首次登录后请立即在"账号管理"里修改。功能：

- **账号管理** — 修改自己的密码、添加/删除管理员（不能删当前登录账号，至少保留一个）
- **节假日** — 按年份浏览，添加/修改/删除（同日期重复保存即覆盖）
- **广告位** — 标题、图片、链接、位置（日历底部 / 天气卡片底部 / 小时预报底部）
- **黄历宜忌** — 按日期维护宜、忌、节日（客户端查不到的日期回退内置数据）
- **点击统计** — 各广告点击量汇总 + 最近 50 条点击记录

所有修改**立即对 API 生效**，无需重启任何东西。

## 伪静态配置

接口路径（如 `/api/holidays`）需要转发到 `index.php`：

**Nginx**（宝塔 → 站点设置 → 伪静态）：

```nginx
location / {
    try_files $uri $uri/ /index.php?$query_string;
}
```

**Apache**：已附带 `.htaccess`，无需额外配置。

## 客户端配置

桌面软件设置里的"后台 API 地址"（`%APPDATA%\SimpleCalendar\clock_settings.json` 中的 `ApiUrl`）
指向本服务即可，例如 `https://你的域名/api`。软件启动时拉取节假日数据并本地缓存，
接口不可用时回退到内置数据。
