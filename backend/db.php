<?php
// PDO 连接（单例）

function db(): PDO {
    static $pdo = null;
    if ($pdo === null) {
        $config = require __DIR__ . '/config.php';
        $pdo = new PDO($config['db_dsn'], $config['db_user'], $config['db_pass'], [
            PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION,
            PDO::ATTR_DEFAULT_FETCH_MODE => PDO::FETCH_ASSOC,
        ]);
    }
    return $pdo;
}
