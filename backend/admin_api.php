<?php
// SimpleCalendar 管理后台 API（给 admin.html 前端页面调用）
// 账号存数据库 admins 表（password_hash 哈希），会话保持登录状态；
// 除 login 外的所有动作都需要已登录。

declare(strict_types=1);

session_start();
require __DIR__ . '/db.php';

header('Content-Type: application/json; charset=utf-8');

function respond($data, int $status = 200): void {
    http_response_code($status);
    $json = json_encode($data, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    if ($json === false) {
        http_response_code(500);
        $json = '{"code":500,"message":"数据编码错误"}';
    }
    echo $json;
    exit;
}

function requireLogin(): void {
    if (empty($_SESSION['admin_id'])) {
        respond(['code' => 401, 'message' => '未登录'], 401);
    }
}

// POST 参数兼容 form 表单和 JSON body
$input = $_POST;
if (!$input && strpos($_SERVER['CONTENT_TYPE'] ?? '', 'application/json') !== false) {
    $input = json_decode(file_get_contents('php://input'), true) ?: [];
}

$action = $_GET['action'] ?? ($input['action'] ?? '');

switch ($action) {
    // ---------------- 登录 / 退出 / 状态 ----------------
    case 'login':
        $username = trim($input['username'] ?? '');
        $password = (string)($input['password'] ?? '');
        $stmt = db()->prepare('SELECT id, username, password_hash FROM admins WHERE username = ?');
        $stmt->execute([$username]);
        $user = $stmt->fetch();
        if (!$user || !password_verify($password, $user['password_hash'])) {
            respond(['code' => 403, 'message' => '账号或密码错误'], 403);
        }
        session_regenerate_id(true);
        $_SESSION['admin_id'] = (int)$user['id'];
        $_SESSION['admin_name'] = $user['username'];
        respond(['code' => 200, 'username' => $user['username']]);

    case 'logout':
        session_destroy();
        respond(['code' => 200]);

    case 'check':
        respond([
            'code' => 200,
            'logged_in' => !empty($_SESSION['admin_id']),
            'username' => $_SESSION['admin_name'] ?? '',
        ]);

    // ---------------- 账号管理 ----------------
    case 'change_password':
        requireLogin();
        $old = (string)($input['old_password'] ?? '');
        $new = (string)($input['new_password'] ?? '');
        if (strlen($new) < 6) respond(['code' => 400, 'message' => '新密码至少 6 位'], 400);
        $stmt = db()->prepare('SELECT password_hash FROM admins WHERE id = ?');
        $stmt->execute([$_SESSION['admin_id']]);
        $user = $stmt->fetch();
        if (!$user || !password_verify($old, $user['password_hash'])) {
            respond(['code' => 403, 'message' => '原密码错误'], 403);
        }
        $stmt = db()->prepare('UPDATE admins SET password_hash = ? WHERE id = ?');
        $stmt->execute([password_hash($new, PASSWORD_DEFAULT), $_SESSION['admin_id']]);
        respond(['code' => 200]);

    case 'admins':
        requireLogin();
        $rows = db()->query('SELECT id, username, created_at FROM admins ORDER BY id')->fetchAll();
        respond(['code' => 200, 'data' => $rows, 'self_id' => $_SESSION['admin_id']]);

    case 'admin_add':
        requireLogin();
        $username = trim($input['username'] ?? '');
        $password = (string)($input['password'] ?? '');
        if (!preg_match('/^[\w\-.]{2,50}$/', $username)) {
            respond(['code' => 400, 'message' => '账号只能包含字母数字（2-50 位）'], 400);
        }
        if (strlen($password) < 6) respond(['code' => 400, 'message' => '密码至少 6 位'], 400);
        $stmt = db()->prepare('SELECT COUNT(*) FROM admins WHERE username = ?');
        $stmt->execute([$username]);
        if ($stmt->fetchColumn() > 0) respond(['code' => 400, 'message' => '账号已存在'], 400);
        $stmt = db()->prepare('INSERT INTO admins (username, password_hash) VALUES (?, ?)');
        $stmt->execute([$username, password_hash($password, PASSWORD_DEFAULT)]);
        respond(['code' => 200]);

    case 'admin_delete':
        requireLogin();
        $id = (int)($input['id'] ?? 0);
        if ($id === (int)$_SESSION['admin_id']) {
            respond(['code' => 400, 'message' => '不能删除当前登录的账号'], 400);
        }
        if (db()->query('SELECT COUNT(*) FROM admins')->fetchColumn() <= 1) {
            respond(['code' => 400, 'message' => '至少保留一个管理员账号'], 400);
        }
        $stmt = db()->prepare('DELETE FROM admins WHERE id = ?');
        $stmt->execute([$id]);
        respond(['code' => 200]);

    // ---------------- 概况统计 ----------------
    case 'overview':
        requireLogin();
        $pdo = db();
        respond(['code' => 200, 'data' => [
            'holidays' => (int)$pdo->query('SELECT COUNT(*) FROM holidays')->fetchColumn(),
            'ads'      => (int)$pdo->query('SELECT COUNT(*) FROM ads')->fetchColumn(),
            'almanac'  => (int)$pdo->query('SELECT COUNT(*) FROM almanac')->fetchColumn(),
            'clicks'   => (int)$pdo->query('SELECT COUNT(*) FROM ad_clicks')->fetchColumn(),
            'admins'   => (int)$pdo->query('SELECT COUNT(*) FROM admins')->fetchColumn(),
        ]]);

    // ---------------- 广告点击统计 ----------------
    case 'ad_stats':
        requireLogin();
        $stats = db()->query(
            'SELECT a.id, a.title, a.position, COUNT(c.id) AS clicks
             FROM ads a LEFT JOIN ad_clicks c ON c.ad_id = a.id
             GROUP BY a.id, a.title, a.position ORDER BY clicks DESC'
        )->fetchAll();
        $recent = db()->query(
            'SELECT ad_id, ip, created_at FROM ad_clicks ORDER BY id DESC LIMIT 50'
        )->fetchAll();
        respond(['code' => 200, 'stats' => $stats, 'recent' => $recent]);

    // ---------------- 数据查询 ----------------
    case 'holidays':
        requireLogin();
        $year = preg_replace('/\D/', '', $_GET['year'] ?? date('Y'));
        $stmt = db()->prepare("SELECT date, name, type FROM holidays WHERE date LIKE ? ORDER BY date");
        $stmt->execute([$year . '-%']);
        respond(['code' => 200, 'data' => $stmt->fetchAll()]);

    case 'ads':
        requireLogin();
        $rows = db()->query('SELECT id, title, image_url, link_url, position FROM ads ORDER BY id')->fetchAll();
        respond(['code' => 200, 'data' => $rows]);

    case 'almanac':
        requireLogin();
        $year = preg_replace('/\D/', '', $_GET['year'] ?? date('Y'));
        $stmt = db()->prepare("SELECT date, yi, ji, festival FROM almanac WHERE date LIKE ? ORDER BY date");
        $stmt->execute([$year . '-%']);
        respond(['code' => 200, 'data' => $stmt->fetchAll()]);

    // ---------------- 节假日 ----------------
    case 'holiday_save':
        requireLogin();
        $date = trim($input['date'] ?? '');
        if (!preg_match('/^\d{4}-\d{2}-\d{2}$/', $date)) {
            respond(['code' => 400, 'message' => '日期格式应为 YYYY-MM-DD'], 400);
        }
        $stmt = db()->prepare('REPLACE INTO holidays (date, name, type) VALUES (?, ?, ?)');
        $stmt->execute([
            $date,
            trim($input['name'] ?? ''),
            ($input['type'] ?? 'holiday') === 'workday' ? 'workday' : 'holiday',
        ]);
        respond(['code' => 200]);

    case 'holiday_delete':
        requireLogin();
        $stmt = db()->prepare('DELETE FROM holidays WHERE date = ?');
        $stmt->execute([$input['date'] ?? '']);
        respond(['code' => 200]);

    // ---------------- 黄历 ----------------
    case 'almanac_save':
        requireLogin();
        $date = trim($input['date'] ?? '');
        if (!preg_match('/^\d{4}-\d{2}-\d{2}$/', $date)) {
            respond(['code' => 400, 'message' => '日期格式应为 YYYY-MM-DD'], 400);
        }
        $stmt = db()->prepare('REPLACE INTO almanac (date, yi, ji, festival) VALUES (?, ?, ?, ?)');
        $stmt->execute([
            $date,
            trim($input['yi'] ?? ''),
            trim($input['ji'] ?? ''),
            trim($input['festival'] ?? ''),
        ]);
        respond(['code' => 200]);

    case 'almanac_delete':
        requireLogin();
        $stmt = db()->prepare('DELETE FROM almanac WHERE date = ?');
        $stmt->execute([$input['date'] ?? '']);
        respond(['code' => 200]);

    // ---------------- 广告 ----------------
    case 'ad_save':
        requireLogin();
        $id = (int)($input['id'] ?? 0);
        $title = trim($input['title'] ?? '');
        $image = trim($input['image_url'] ?? '');
        $link = trim($input['link_url'] ?? '');
        $pos = trim($input['position'] ?? 'calendar_bottom');
        if ($id > 0) {
            $stmt = db()->prepare('UPDATE ads SET title=?, image_url=?, link_url=?, position=? WHERE id=?');
            $stmt->execute([$title, $image, $link, $pos, $id]);
        } else {
            $stmt = db()->prepare('INSERT INTO ads (title, image_url, link_url, position) VALUES (?, ?, ?, ?)');
            $stmt->execute([$title, $image, $link, $pos]);
        }
        respond(['code' => 200]);

    case 'ad_delete':
        requireLogin();
        $stmt = db()->prepare('DELETE FROM ads WHERE id = ?');
        $stmt->execute([(int)($input['id'] ?? 0)]);
        respond(['code' => 200]);
}

respond(['code' => 404, 'message' => '未知操作'], 404);
