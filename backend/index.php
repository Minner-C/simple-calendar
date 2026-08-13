<?php
// SimpleCalendar 后台服务（PHP + MySQL 版）
// 提供节假日 / 黄历宜忌 / 广告位接口，数据存 MySQL，管理后台见 admin.php。
// 兼容 PHP 7.4+，需开启 pdo_mysql 扩展（宝塔 PHP 默认已开启）。

declare(strict_types=1);

require __DIR__ . '/db.php';

header('Content-Type: application/json; charset=utf-8');
header('Access-Control-Allow-Origin: *'); // 方便浏览器调试；桌面客户端本身不需要

function respond($data, int $status = 200): void {
    http_response_code($status);
    $json = json_encode($data, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    if ($json === false) {
        // 数据含非法 UTF-8（如管理后台写入了 GBK 文本）时避免输出空白页
        http_response_code(500);
        $json = '{"code":500,"message":"数据编码错误，请检查管理后台录入的内容"}';
    }
    echo $json;
    exit;
}

// ---------------- 路由 ----------------
// 依赖伪静态把 /api/... 转发到本文件（见 README 的 Nginx/Apache 配置）

$path = parse_url($_SERVER['REQUEST_URI'], PHP_URL_PATH) ?: '/';
$path = rtrim($path, '/');
$method = $_SERVER['REQUEST_METHOD'];

// 兼容站点挂在子目录的情况：剥掉入口文件所在目录前缀
$base = rtrim(str_replace('\\', '/', dirname($_SERVER['SCRIPT_NAME'])), '/');
if ($base !== '' && strpos($path, $base) === 0) {
    $path = substr($path, strlen($base)) ?: '/';
}

// GET / —— 服务信息
if ($path === '' || $path === '/' || $path === '/index.php') {
    respond([
        'name' => 'SimpleCalendar Backend (PHP + MySQL)',
        'admin' => '/admin.php',
        'endpoints' => [
            'GET /api/holidays',
            'GET /api/holidays/{year}',
            'GET /api/holidays/check/{date}',
            'GET /api/almanac/{date}',
            'GET /api/ads/active',
            'POST /api/ads/{id}/click',
        ],
    ]);
}

// GET /api/holidays —— 全部节假日数据（客户端启动时拉取这份，本地缓存兜底）
if ($path === '/api/holidays' && $method === 'GET') {
    header('Cache-Control: public, max-age=3600');
    $rows = db()->query('SELECT date, name, type FROM holidays ORDER BY date')->fetchAll();
    respond($rows);
}

// GET /api/holidays/check/2026-10-01 —— 单日查询（注意要放在按年查询之前）
if (preg_match('#^/api/holidays/check/(\d{4}-\d{2}-\d{2})$#', $path, $m) && $method === 'GET') {
    $stmt = db()->prepare('SELECT date, name, type FROM holidays WHERE date = ?');
    $stmt->execute([$m[1]]);
    $item = $stmt->fetch();
    if (!$item) {
        respond(['code' => 404, 'date' => $m[1], 'message' => '非节假日/调班日']);
    }
    respond([
        'code' => 200,
        'date' => $item['date'],
        'name' => $item['name'],
        'type' => $item['type'],
        'isHoliday' => $item['type'] === 'holiday',
        'isWorkday' => $item['type'] === 'workday',
    ]);
}

// GET /api/holidays/2026 —— 按年份查询
if (preg_match('#^/api/holidays/(\d{4})$#', $path, $m) && $method === 'GET') {
    header('Cache-Control: public, max-age=3600');
    $stmt = db()->prepare("SELECT date, name, type FROM holidays WHERE date LIKE ? ORDER BY date");
    $stmt->execute([$m[1] . '-%']);
    respond($stmt->fetchAll());
}

// GET /api/almanac/2026-01-01 —— 黄历宜忌（无数据时客户端回退内置数据）
if (preg_match('#^/api/almanac/(\d{4}-\d{2}-\d{2})$#', $path, $m) && $method === 'GET') {
    $stmt = db()->prepare('SELECT yi, ji, festival FROM almanac WHERE date = ?');
    $stmt->execute([$m[1]]);
    $item = $stmt->fetch();
    if (!$item) {
        respond(['code' => 404, 'date' => $m[1], 'message' => '无该日数据']);
    }
    respond([
        'code' => 200,
        'date' => $m[1],
        'yi' => $item['yi'],
        'ji' => $item['ji'],
        'festival' => $item['festival'],
    ]);
}

// GET /api/ads/active —— 生效中的广告位
if ($path === '/api/ads/active' && $method === 'GET') {
    $rows = db()->query('SELECT id, title, image_url, link_url, position FROM ads ORDER BY id')->fetchAll();
    respond($rows);
}

// POST /api/ads/{id}/click —— 客户端点击上报，写入 ad_clicks 表
if (preg_match('#^/api/ads/(\d+)/click$#', $path, $m) && $method === 'POST') {
    $stmt = db()->prepare('INSERT INTO ad_clicks (ad_id, ip) VALUES (?, ?)');
    $stmt->execute([(int)$m[1], $_SERVER['REMOTE_ADDR'] ?? '-']);
    respond(['code' => 200]);
}

respond(['code' => 404, 'message' => '接口不存在'], 404);
